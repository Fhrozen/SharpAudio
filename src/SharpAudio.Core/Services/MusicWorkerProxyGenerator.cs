// wiki: music#worker-process-and-di-wiring
using Microsoft.Extensions.DependencyInjection;
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Music.Grpc;

namespace SharpAudio.Core.Services;

/// <summary>Routes music requests to the music worker process over gRPC. Only the worker path exists
/// today: the music engine itself is a separate phase, so there is no in-process implementation to
/// fall back to.</summary>
public sealed class MusicWorkerProxyGenerator : IMusicGenerator, IEngineWarmer<MusicModelDefinition>
{
    private const string WorkerKey = "music";

    private readonly WorkerProcessManager _processManager;
    private readonly WorkerChannelCache _channels;
    private readonly ILogger<MusicWorkerProxyGenerator> _logger;

    public MusicWorkerProxyGenerator(
        [FromKeyedServices(WorkerKey)] WorkerProcessManager processManager,
        [FromKeyedServices(WorkerKey)] WorkerChannelCache channels,
        ILogger<MusicWorkerProxyGenerator> logger)
    {
        _processManager = processManager;
        _channels = channels;
        _logger = logger;
    }

    public async Task<bool> WarmUpAsync(MusicModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        var worker = await _processManager.GetOrSpawnWorkerAsync(ModelKey(model), cancellationToken);
        return worker.IsAlive;
    }

    public async Task<MusicGenerationResult> GenerateAsync(
        MusicModelDefinition model,
        string modelDirectory,
        MusicGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var modelKey = ModelKey(model);
        var worker = await _processManager.GetOrSpawnWorkerAsync(modelKey, cancellationToken);

        var client = _channels.GetOrCreateClient<WorkerMusic.WorkerMusicClient>(
            modelKey, worker.Port, channel => new WorkerMusic.WorkerMusicClient(channel));

        var grpcRequest = new GenerateRequest
        {
            ModelName = model.Name,
            Engine = model.Engine,
            ModelPath = Path.Combine(modelDirectory, model.ModelPath),
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            DurationSeconds = request.DurationSeconds,
            Steps = request.Steps,
            Seed = request.Seed
        };

        _logger.LogInformation("Sending music request to worker {ModelKey} on port {Port}: prompt_len={PromptLength}, duration={Duration}s",
            modelKey, worker.Port, request.Prompt.Length, request.DurationSeconds);

        var response = await _channels.InvokeWithRetryAsync(
            modelKey, worker.Port,
            ct => client.GenerateAsync(grpcRequest, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        return new MusicGenerationResult(
            response.AudioBytes.ToByteArray(),
            response.ContentType,
            response.FileName,
            response.SampleRate,
            response.Channels,
            response.ProcessingTimeSeconds,
            response.AudioDurationSeconds);
    }

    private static string ModelKey(MusicModelDefinition model) => $"{model.Engine}:{model.Name}";
}
