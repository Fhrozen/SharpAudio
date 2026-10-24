using Microsoft.Extensions.Options;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;using SharpAudio.Core.Health;using SharpAudio.Core.Services.Pnc;
using SharpAudio.Core.Worker;
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
    private const string WhisperKind = "whisper";
    private const string NemotronKind = "nemotron-3.5";

    private readonly WorkerServiceBase _base;
    private readonly IPncModelCatalog _pncCatalog;
    private readonly IPncEnginePool _pncPool;
    private readonly VadPipeline _vadPipeline;

    public WorkerTranscriptionService(
        IdleMonitor idleMonitor,
        IPncModelCatalog pncCatalog,
        IPncEnginePool pncPool,
        IOptions<VadOptions> vadOptions)
    {
        _base = new WorkerServiceBase(idleMonitor);
        _pncCatalog = pncCatalog;
        _pncPool = pncPool;
        _vadPipeline = new VadPipeline(vadOptions.Value, "Worker.Asr");
    }

    public override async Task<TranscribeResponse> Transcribe(TranscribeRequest request, ServerCallContext context)
    {
        _base.RecordActivity();
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

        var text = result.Text;
        if (request.Punctuate && _pncCatalog.TryGetModel(request.PncModelName, out var pncModel))
        {
            var engine = _pncPool.GetOrCreateEngine(pncModel!, request.PncModelDirectory);
            text = PncTextApplier.Apply(engine, text);
            segments = segments.Select(segment => new TranscriptionSegment(
                segment.Id, segment.Start, segment.End, PncTextApplier.Apply(engine, segment.Text))).ToList();
        }

        var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var audioDuration = WavAudioUtils.GetDurationSeconds(audioBytes);

        Console.WriteLine($"[Worker.Asr] Transcription complete: {processingTime:F2}s processing, {audioDuration:F2}s audio");

        var response = new TranscribeResponse
        {
            Text = text,
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

    private Task<SpanTranscription> TranscribeWithVadAsync(WhisperAsrEngine engine, TranscribeRequest request, byte[] audioBytes, string? language, CancellationToken cancellationToken)
    {
        return _vadPipeline.TranscribeAsync(
            Path.GetDirectoryName(request.ModelPath) ?? "",
            audioBytes,
            request.VadOnset,
            request.VadOffset,
            async (spanSamples, token) => ToSpan(await engine.TranscribeAsync(WavAudioUtils.EncodeMono16kWav(spanSamples), language, token)),
            cancellationToken);
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
        _base.RecordActivity();

        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.PayloadCase != TranscribeStreamChunk.PayloadOneofCase.Config)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first TranscribeStream message must set 'config'."));
        }

        var config = requestStream.Current.Config;
        Console.WriteLine($"[Worker.Asr] TranscribeStream started: engine={config.Engine}, model={config.ModelName}");
        var language = string.IsNullOrEmpty(config.Language) ? null : config.Language;
        var segmentSeconds = config.SegmentSeconds > 0 ? config.SegmentSeconds : new SharpAudio.Core.Options.AsrStreamingOptions().DefaultSegmentSeconds;
        // Endpoint thresholds come from the API's VadOptions, forwarded on the stream config.
        var vadOptions = new VadOptions { EnableEndpointing = config.EnableEndpointing, StopHistoryEouMs = config.StopHistoryEouMs };

        using IStreamingTranscriptionSession session = string.Equals(config.Engine, "nemotron-3.5", StringComparison.OrdinalIgnoreCase)
            ? GetOrCreateNemotronEngine(config.ModelName, config.ModelPath, vadOptions).CreateStreamingSession(language, config.UseVad, segmentSeconds, config.EnableEndpointing)
            : new WhisperStreamingSession(GetOrCreateWhisperEngine(config.ModelName, config.ModelPath), language, segmentSeconds);

        while (await requestStream.MoveNext(context.CancellationToken))
        {
            _base.RecordActivity();
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
        return _base.Engines.GetOrCreate(WhisperKind, $"{WhisperKind}:{modelName}", () =>
        {
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Model file not found: {modelPath}");
            }

            Console.WriteLine($"[Worker.Asr] Loading Whisper model: {modelPath}");
            return new WhisperAsrEngine(modelPath);
        });
    }

    private NemotronAsrEngine GetOrCreateNemotronEngine(string modelName, string modelPath, VadOptions? vadOptions = null)
    {
        return _base.Engines.GetOrCreate(NemotronKind, $"{NemotronKind}:{modelName}", () =>
        {
            if (!Directory.Exists(modelPath))
            {
                throw new DirectoryNotFoundException($"Model directory not found: {modelPath}");
            }

            Console.WriteLine($"[Worker.Asr] Loading Nemotron model from directory: {modelPath}");
            return new NemotronAsrEngine(modelPath, vadOptions);
        });
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(_base.BuildHealthCheck());
    }

    public void Dispose()
    {
        _base.Dispose();
        _vadPipeline.Dispose();
        _pncPool.Dispose();
    }
}
