// wiki: asr-engines#worker-process-di-wiring-and-server-mode
using System.Threading.Channels;
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services.Pnc;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Asr.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SharpAudio.Core.Services;

/// <summary>
/// Proxy transcriber that routes requests to the ASR worker process via gRPC (mirrors
/// <see cref="WorkerProxySynthesizer"/>).
/// </summary>
public sealed class AsrWorkerProxyTranscriber : IAsrTranscriber, IEngineWarmer<AsrModelDefinition>
{
    private readonly WorkerProcessManager _processManager;
    private readonly WorkerChannelCache _channels;
    private readonly ILogger<AsrWorkerProxyTranscriber> _logger;
    private readonly VadOptions _options;
    private readonly IPncModelCatalog _pncCatalog;
    private readonly IModelCache _modelCache;

    public AsrWorkerProxyTranscriber(
        [FromKeyedServices("asr")] WorkerProcessManager processManager,
        [FromKeyedServices("asr")] WorkerChannelCache channels,
        ILogger<AsrWorkerProxyTranscriber> logger,
        IOptions<VadOptions> options,
        IPncModelCatalog pncCatalog,
        IModelCache modelCache)
    {
        _processManager = processManager;
        _channels = channels;
        _logger = logger;
        _options = options.Value;
        _pncCatalog = pncCatalog;
        _modelCache = modelCache;
    }

    // In worker mode the engine lives in the child process, so warming it means pre-spawning that
    // process. The child still builds the engine on its first RPC: a worker has no catalog, only a
    // model key, so it cannot warm anything on its own.
    public async Task<bool> WarmUpAsync(AsrModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        var worker = await _processManager.GetOrSpawnWorkerAsync($"{model.Engine}:{model.Name}", cancellationToken);
        return worker.IsAlive;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        var modelKey = $"{model.Engine}:{model.Name}";

        var worker = await _processManager.GetOrSpawnWorkerAsync(modelKey, cancellationToken);
        var client = _channels.GetOrCreateClient<WorkerTranscription.WorkerTranscriptionClient>(
            modelKey, worker.Port, channel => new WorkerTranscription.WorkerTranscriptionClient(channel));

        var grpcRequest = new TranscribeRequest
        {
            ModelName = model.Name,
            Engine = model.Engine,
            ModelPath = Path.Combine(modelDirectory, model.ModelPath),
            AudioBytes = ByteString.CopyFrom(audioBytes),
            AudioFormat = "wav",
            Language = request.Language ?? "",
            UseVad = request.EnableVad,
            VadOnset = (float)(request.VadOnset ?? _options.Onset),
            VadOffset = (float)(request.VadOffset ?? _options.Offset),
            IncludeSegments = request.IncludeSegments,
            MinSegmentDurationSeconds = request.MinSegmentDurationSeconds
        };

        if (request.Punctuate && _pncCatalog.TryGetModel(request.PncModel ?? string.Empty, out var pncModel))
        {
            grpcRequest.Punctuate = true;
            grpcRequest.PncModelName = pncModel!.Name;
            grpcRequest.PncModelDirectory = await _modelCache.EnsureModelAsync(pncModel!, cancellationToken);
        }

        _logger.LogInformation("Sending transcription request to worker {ModelKey} on port {Port}: audio_bytes={Length}",
            modelKey, worker.Port, audioBytes.Length);

        var response = await _channels.InvokeWithRetryAsync(
            modelKey, worker.Port,
            ct => client.TranscribeAsync(grpcRequest, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        var segments = response.Segments
            .Select(segment => new TranscriptionSegment(segment.Id, segment.Start, segment.End, segment.Text))
            .ToList();

        return new TranscriptionResult(
            response.Text,
            string.IsNullOrEmpty(response.LanguageDetected) ? null : response.LanguageDetected,
            response.ProcessingTimeSeconds,
            response.AudioDurationSeconds,
            response.Text.Length,
            segments.Count > 0 ? segments : null);
    }

    public async Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
        AsrModelDefinition model, string modelDirectory, string? language, bool enableVad, bool enableEndpointing, double segmentSeconds, CancellationToken cancellationToken)
    {
        var modelKey = $"{model.Engine}:{model.Name}";

        var worker = await _processManager.GetOrSpawnWorkerAsync(modelKey, cancellationToken);
        var client = _channels.GetOrCreateClient<WorkerTranscription.WorkerTranscriptionClient>(
            modelKey, worker.Port, channel => new WorkerTranscription.WorkerTranscriptionClient(channel));

        var call = client.TranscribeStream(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new TranscribeStreamChunk
        {
            Config = new StreamConfig
            {
                ModelName = model.Name,
                Engine = model.Engine,
                ModelPath = Path.Combine(modelDirectory, model.ModelPath),
                Language = language ?? string.Empty,
                UseVad = enableVad,
                SegmentSeconds = segmentSeconds,
                EnableEndpointing = enableEndpointing,
                StopHistoryEouMs = _options.StopHistoryEouMs
            }
        }, cancellationToken);

        return new WorkerStreamingSession(call);
    }

    /// <summary>Adapts a duplex TranscribeStream gRPC call to <see cref="IStreamingTranscriptionSession"/> -
    /// a drop-in for the in-process streaming sessions, so Program.cs's WebSocket handler doesn't
    /// need to know whether ASR is running in-process or in worker mode.</summary>
    private sealed class WorkerStreamingSession : IStreamingTranscriptionSession
    {
        private readonly AsyncDuplexStreamingCall<TranscribeStreamChunk, TranscribeStreamUpdate> _call;
        private readonly Channel<StreamingUpdate> _updates = Channel.CreateUnbounded<StreamingUpdate>();
        private readonly Task _readLoopTask;
        private string _lastFinalText = string.Empty;

        // Both engines' streaming sessions operate on 16kHz PCM16 mono audio - see WhisperStreamingSession/NemotronAsrEngine.
        public int SampleRate => 16000;

        public WorkerStreamingSession(AsyncDuplexStreamingCall<TranscribeStreamChunk, TranscribeStreamUpdate> call)
        {
            _call = call;
            _readLoopTask = ReadLoopAsync();
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                await foreach (var update in _call.ResponseStream.ReadAllAsync())
                {
                    if (update.IsFinal)
                    {
                        _lastFinalText = update.Text;
                        continue; // the true end-of-stream update is consumed by FinishAsync, not queued as a segment update
                    }

                    await _updates.Writer.WriteAsync(new StreamingUpdate(update.Text, update.IsSegmentFinal, update.IsEndpointFinal));
                }
            }
            catch (Exception)
            {
                // Connection dropped/cancelled - FinishAsync falls back to the last known text.
            }
            finally
            {
                _updates.Writer.TryComplete();
            }
        }

        public async Task<IReadOnlyList<StreamingUpdate>> ProcessChunkAsync(ReadOnlyMemory<byte> pcm16Chunk, CancellationToken cancellationToken)
        {
            await _call.RequestStream.WriteAsync(new TranscribeStreamChunk { AudioChunk = ByteString.CopyFrom(pcm16Chunk.Span) }, cancellationToken);
            return DrainUpdates();
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            await _call.RequestStream.CompleteAsync();
            await _readLoopTask;
            return _lastFinalText;
        }

        /// <summary>Collapses consecutive in-progress (partial) updates down to the latest one, but
        /// never drops a segment-final commit - each must reach the client exactly once, in order.</summary>
        private List<StreamingUpdate> DrainUpdates()
        {
            var result = new List<StreamingUpdate>();
            while (_updates.Reader.TryRead(out var next))
            {
                if (!next.IsSegmentFinal && result.Count > 0 && !result[^1].IsSegmentFinal)
                {
                    result[^1] = next;
                }
                else
                {
                    result.Add(next);
                }
            }

            return result;
        }

        public void Dispose() => _call.Dispose();
    }
}
