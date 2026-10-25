// wiki: diarization#worker-process-and-di-wiring
using Microsoft.Extensions.DependencyInjection;
using SharpAudio.Core.Models;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Diar.Grpc;

namespace SharpAudio.Core.Services;

/// <summary>Routes diarization requests to the diarization worker process over gRPC. Only the worker
/// path exists today: the Sortformer engine itself is a separate phase, so there is no in-process
/// implementation to fall back to.</summary>
public sealed class DiarWorkerProxyDiarizer : IDiarizer, IEngineWarmer<DiarModelDefinition>
{
    private const string WorkerKey = "diar";

    private readonly WorkerProcessManager _processManager;
    private readonly WorkerChannelCache _channels;
    private readonly ILogger<DiarWorkerProxyDiarizer> _logger;

    public DiarWorkerProxyDiarizer(
        [FromKeyedServices(WorkerKey)] WorkerProcessManager processManager,
        [FromKeyedServices(WorkerKey)] WorkerChannelCache channels,
        ILogger<DiarWorkerProxyDiarizer> logger)
    {
        _processManager = processManager;
        _channels = channels;
        _logger = logger;
    }

    public async Task<bool> WarmUpAsync(DiarModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        var worker = await _processManager.GetOrSpawnWorkerAsync(ModelKey(model), cancellationToken);
        return worker.IsAlive;
    }

    public async Task<DiarizationResult> DiarizeAsync(
        DiarModelDefinition model,
        string modelDirectory,
        byte[] audioBytes,
        int speakerCount,
        CancellationToken cancellationToken)
    {
        var modelKey = ModelKey(model);
        var worker = await _processManager.GetOrSpawnWorkerAsync(modelKey, cancellationToken);

        var client = _channels.GetOrCreateClient<WorkerDiarization.WorkerDiarizationClient>(
            modelKey, worker.Port, channel => new WorkerDiarization.WorkerDiarizationClient(channel));

        var grpcRequest = new DiarizeRequest
        {
            ModelName = model.Name,
            Engine = model.Engine,
            ModelPath = Path.Combine(modelDirectory, model.ModelPath),
            AudioBytes = Google.Protobuf.ByteString.CopyFrom(audioBytes),
            SpeakerCount = speakerCount
        };

        _logger.LogInformation("Sending diarization request to worker {ModelKey} on port {Port}: audio_bytes={Length}, speakers={SpeakerCount}",
            modelKey, worker.Port, audioBytes.Length, speakerCount);

        var response = await _channels.InvokeWithRetryAsync(
            modelKey, worker.Port,
            ct => client.DiarizeAsync(grpcRequest, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        var segments = response.Segments
            .Select(segment => new SpeakerSegment(segment.Id, segment.Start, segment.End, segment.Speaker))
            .ToList();

        return new DiarizationResult(
            segments,
            segments.Count == 0 ? 0 : segments.Max(segment => segment.Speaker) + 1,
            response.ProcessingTimeSeconds,
            response.AudioDurationSeconds);
    }

    private static string ModelKey(DiarModelDefinition model) => $"{model.Engine}:{model.Name}";
}
