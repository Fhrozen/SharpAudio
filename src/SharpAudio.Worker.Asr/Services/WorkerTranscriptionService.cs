using SharpAudio.Api.Models;
using SharpAudio.Api.Options;
using SharpAudio.Api.Services;
using SharpAudio.Worker.Asr.Grpc;
using Grpc.Core;

namespace SharpAudio.Worker.Asr.Services;

/// <summary>
/// gRPC service that performs ASR transcription in the worker process (mirrors
/// WorkerSynthesisService). Routes by request.Engine to Whisper or Nemotron, keeping only the
/// most-recently-used engine loaded at a time.
/// </summary>
public sealed class WorkerTranscriptionService : WorkerTranscription.WorkerTranscriptionBase, IDisposable
{
    private const int VadSampleRate = 16000;
    private const string SileroVadAsset = "silero_vad.onnx";

    private readonly IdleMonitor _idleMonitor;
    private WhisperAsrEngine? _whisperEngine;
    private NemotronAsrEngine? _nemotronEngine;
    private ISileroVadEngine? _vadEngine;
    private string? _loadedModelKey;
    private string? _loadedVadKey;
    private readonly object _engineLock = new();
    private readonly SemaphoreSlim _vadGate = new(1, 1);
    private bool _disposed;

    public WorkerTranscriptionService(IdleMonitor idleMonitor)
    {
        _idleMonitor = idleMonitor;
    }

    public override async Task<TranscribeResponse> Transcribe(TranscribeRequest request, ServerCallContext context)
    {
        _idleMonitor.RecordActivity();
        var startTime = DateTime.UtcNow;

        Console.WriteLine($"[Worker.Asr] Transcribe request: engine={request.Engine}, model={request.ModelName}, audio_bytes={request.AudioBytes.Length}");

        var audioBytes = request.AudioBytes.ToByteArray();
        var language = string.IsNullOrEmpty(request.Language) ? null : request.Language;

        SpanTranscription result;

        if (string.Equals(request.Engine, "nemotron-3.5", StringComparison.OrdinalIgnoreCase))
        {
            var engine = GetOrCreateNemotronEngine(request.ModelName, request.ModelPath);
            result = ToSpan(await Task.Run(() => engine.Transcribe(audioBytes, language, request.UseVad), context.CancellationToken));
        }
        else
        {
            var engine = GetOrCreateWhisperEngine(request.ModelName, request.ModelPath);
            result = request.UseVad
                ? await TranscribeWithVadAsync(engine, request, audioBytes, language, context.CancellationToken)
                : ToSpan(await engine.TranscribeAsync(audioBytes, language, context.CancellationToken));
        }

        var segments = request.IncludeSegments ? result.Segments : [];
        if (request.MinSegmentDurationSeconds > 0)
        {
            segments = SegmentMerger.MergeShortSegments(segments, request.MinSegmentDurationSeconds);
        }

        var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var audioDuration = WavAudioUtils.GetDurationSeconds(audioBytes);

        Console.WriteLine($"[Worker.Asr] Transcription complete: {processingTime:F2}s processing, {audioDuration:F2}s audio");

        var response = new TranscribeResponse
        {
            Text = result.Text,
            LanguageDetected = result.DetectedLanguage ?? string.Empty,
            ProcessingTimeSeconds = processingTime,
            AudioDurationSeconds = audioDuration
        };

        foreach (var segment in segments)
        {
            response.Segments.Add(new TranscriptionSegmentProto
            {
                Id = segment.Id,
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text
            });
        }

        return response;
    }

    private async Task<SpanTranscription> TranscribeWithVadAsync(WhisperAsrEngine engine, TranscribeRequest request, byte[] audioBytes, string? language, CancellationToken cancellationToken)
    {
        var vadEngine = GetOrCreateVadEngine(request.ModelName, Path.GetDirectoryName(request.ModelPath) ?? "");
        var options = new SharpAudio.Api.Options.VadOptions().WithOverrides(request.VadOnset, request.VadOffset);
        var samples = WavAudioUtils.ReadMonoFloat(audioBytes, VadSampleRate);
        var spanTranscriber = new VadSpanTranscriber(
            vadEngine,
            options,
            async (spanSamples, token) => ToSpan(await engine.TranscribeAsync(WavAudioUtils.EncodeMono16kWav(spanSamples), language, token)));

        // One utterance at a time per VAD engine, since the LSTM state is carried across calls.
        await _vadGate.WaitAsync(cancellationToken);
        try
        {
            return await spanTranscriber.TranscribeAsync(samples, cancellationToken);
        }
        finally
        {
            _vadGate.Release();
        }
    }

    private static SpanTranscription ToSpan((string Text, string? DetectedLanguage, IReadOnlyList<TranscriptionSegment> Segments) result)
    {
        return new SpanTranscription(result.Text, result.DetectedLanguage, result.Segments);
    }

    public override async Task TranscribeStream(
        IAsyncStreamReader<TranscribeStreamChunk> requestStream,
        IServerStreamWriter<TranscribeStreamUpdate> responseStream,
        ServerCallContext context)
    {
        _idleMonitor.RecordActivity();

        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.PayloadCase != TranscribeStreamChunk.PayloadOneofCase.Config)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first TranscribeStream message must set 'config'."));
        }

        var config = requestStream.Current.Config;
        Console.WriteLine($"[Worker.Asr] TranscribeStream started: engine={config.Engine}, model={config.ModelName}");
        var language = string.IsNullOrEmpty(config.Language) ? null : config.Language;
        var segmentSeconds = config.SegmentSeconds > 0 ? config.SegmentSeconds : new SharpAudio.Api.Options.AsrStreamingOptions().DefaultSegmentSeconds;
        // Endpoint thresholds come from the API's VadOptions, forwarded on the stream config.
        var vadOptions = new VadOptions { EnableEndpointing = config.EnableEndpointing, StopHistoryEouMs = config.StopHistoryEouMs };

        using IStreamingTranscriptionSession session = string.Equals(config.Engine, "nemotron-3.5", StringComparison.OrdinalIgnoreCase)
            ? GetOrCreateNemotronEngine(config.ModelName, config.ModelPath, vadOptions).CreateStreamingSession(language, config.UseVad, segmentSeconds, config.EnableEndpointing)
            : new WhisperStreamingSession(GetOrCreateWhisperEngine(config.ModelName, config.ModelPath), language, segmentSeconds);

        while (await requestStream.MoveNext(context.CancellationToken))
        {
            _idleMonitor.RecordActivity();
            var chunk = requestStream.Current;
            if (chunk.PayloadCase != TranscribeStreamChunk.PayloadOneofCase.AudioChunk)
            {
                continue;
            }

            var updates = await session.ProcessChunkAsync(chunk.AudioChunk.ToByteArray(), context.CancellationToken);
            foreach (var update in updates)
            {
                await responseStream.WriteAsync(new TranscribeStreamUpdate { Text = update.Text, IsFinal = false, IsSegmentFinal = update.IsSegmentFinal, IsEndpointFinal = update.IsEndpointFinal });
            }
        }

        var finalText = await session.FinishAsync(context.CancellationToken);
        await responseStream.WriteAsync(new TranscribeStreamUpdate { Text = finalText, IsFinal = true });
        Console.WriteLine("[Worker.Asr] TranscribeStream finished");
    }

    private WhisperAsrEngine GetOrCreateWhisperEngine(string modelName, string modelPath)
    {
        lock (_engineLock)
        {
            var modelKey = $"whisper:{modelName}";

            if (_whisperEngine == null || _loadedModelKey != modelKey)
            {
                _whisperEngine?.Dispose();
                _nemotronEngine?.Dispose();
                _nemotronEngine = null;

                Console.WriteLine($"[Worker.Asr] Loading Whisper model: {modelPath}");

                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException($"Model file not found: {modelPath}");
                }

                _whisperEngine = new WhisperAsrEngine(modelPath);
                _loadedModelKey = modelKey;
            }

            return _whisperEngine;
        }
    }

    private ISileroVadEngine GetOrCreateVadEngine(string modelName, string modelDirectory)
    {
        lock (_engineLock)
        {
            var vadPath = Path.Combine(modelDirectory, SileroVadAsset);

            if (!File.Exists(vadPath))
            {
                throw new FileNotFoundException($"Model file not found: {vadPath}");
            }

            var vadKey = $"vad:{vadPath}";
            if (_vadEngine == null || _loadedVadKey != vadKey)
            {
                (_vadEngine as IDisposable)?.Dispose();
                Console.WriteLine($"[Worker.Asr] Loading Silero VAD model: {vadPath}");
                _vadEngine = new SileroVadEngine(vadPath, VadSampleRate);
                _loadedVadKey = vadKey;
            }

            return _vadEngine;
        }
    }

    private NemotronAsrEngine GetOrCreateNemotronEngine(string modelName, string modelPath, VadOptions? vadOptions = null)
    {
        lock (_engineLock)
        {
            var modelKey = $"nemotron-3.5:{modelName}";

            if (_nemotronEngine == null || _loadedModelKey != modelKey)
            {
                _nemotronEngine?.Dispose();
                _whisperEngine?.Dispose();
                _whisperEngine = null;

                Console.WriteLine($"[Worker.Asr] Loading Nemotron model from directory: {modelPath}");

                if (!Directory.Exists(modelPath))
                {
                    throw new DirectoryNotFoundException($"Model directory not found: {modelPath}");
                }

                _nemotronEngine = new NemotronAsrEngine(modelPath, vadOptions);
                _loadedModelKey = modelKey;
            }

            return _nemotronEngine;
        }
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        var idleDuration = _idleMonitor.GetIdleDuration();

        return Task.FromResult(new HealthCheckResponse
        {
            IsReady = true,
            ModelLoaded = _loadedModelKey ?? "none",
            IdleSeconds = idleDuration.TotalSeconds
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_engineLock)
        {
            _whisperEngine?.Dispose();
            _nemotronEngine?.Dispose();
            (_vadEngine as IDisposable)?.Dispose();
        }

        _vadGate.Dispose();
        _disposed = true;
    }
}
