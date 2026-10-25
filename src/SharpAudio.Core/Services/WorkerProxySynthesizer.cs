// wiki: backend-architecture#worker-mode-vs-in-process-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Grpc;

namespace SharpAudio.Core.Services;

/// <summary>
/// Proxy synthesizer that routes requests to worker processes via gRPC
/// </summary>
public sealed class WorkerProxySynthesizer : ITtsSynthesizer, IEngineWarmer<TtsModelDefinition>
{
    private const int FallbackSampleRate = 24000;

    private readonly WorkerProcessManager _processManager;
    private readonly IModelCache _modelCache;
    private readonly WorkerChannelCache _channels;
    private readonly ILogger<WorkerProxySynthesizer> _logger;

    public WorkerProxySynthesizer(
        [FromKeyedServices("tts")] WorkerProcessManager processManager,
        IModelCache modelCache,
        [FromKeyedServices("tts")] WorkerChannelCache channels,
        ILogger<WorkerProxySynthesizer> logger)
    {
        _processManager = processManager;
        _modelCache = modelCache;
        _channels = channels;
        _logger = logger;
    }

    // In worker mode the engine lives in the child process, so warming it means pre-spawning that
    // process. The child still builds the engine on its first RPC: a worker has no catalog, only a
    // model key, so it cannot warm anything on its own.
    public async Task<bool> WarmUpAsync(TtsModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        var worker = await _processManager.GetOrSpawnWorkerAsync($"{model.Engine}:{model.Name}", cancellationToken);
        return worker.IsAlive;
    }

    public async Task<SynthesisResult> SynthesizeAsync(
        TtsModelDefinition model,
        string modelDirectory,
        OpenAiSpeechRequest request,
        CancellationToken cancellationToken)
    {
        var modelKey = $"{model.Engine}:{model.Name}";
        var startTime = DateTime.UtcNow;

        try
        {
            // Get or spawn worker for this model
            var worker = await _processManager.GetOrSpawnWorkerAsync(modelKey, cancellationToken);

            // Cached channel and client, rebuilt only when the worker is respawned on another port
            var client = _channels.GetOrCreateClient<WorkerSynthesis.WorkerSynthesisClient>(
                modelKey, worker.Port, channel => new WorkerSynthesis.WorkerSynthesisClient(channel));

            // Build gRPC request
            var grpcRequest = new SynthesizeRequest
            {
                ModelName = model.Name,
                Engine = model.Engine,
                ModelPath = Path.Combine(modelDirectory, model.ModelPath),
                VoicesPath = !string.IsNullOrWhiteSpace(model.VoicesPath) 
                    ? Path.Combine(modelDirectory, model.VoicesPath) 
                    : string.Empty,
                TokensPath = !string.IsNullOrWhiteSpace(model.TokensPath)
                    ? Path.Combine(modelDirectory, model.TokensPath)
                    : string.Empty,
                VoiceFileExtension = model.VoiceFileExtension,
                Input = request.Input,
                Voice = request.Voice,
                ResponseFormat = request.ResponseFormat,
                Speed = request.Speed,
                Language = request.Language ?? "en-us"
            };

            _logger.LogInformation("Sending synthesis request to worker {ModelKey} on port {Port}: speed={Speed}, voice={Voice}, input_length={Length}", 
                modelKey, worker.Port, request.Speed, request.Voice, request.Input.Length);

            grpcRequest.SupportedLanguages.AddRange(model.SupportedLanguages);
            grpcRequest.Speakers.AddRange(model.Speakers);

            // Call worker via gRPC, retrying once if the worker has just been respawned
            var response = await _channels.InvokeWithRetryAsync(
                modelKey, worker.Port,
            ct => client.SynthesizeAsync(grpcRequest, cancellationToken: ct).ResponseAsync,
            cancellationToken);

            // Convert gRPC response to SynthesisResult
            return new SynthesisResult(
                response.AudioBytes.ToByteArray(),
                response.ContentType,
                response.FileName,
                response.ProcessingTimeSeconds,
                response.AudioDurationSeconds,
                response.CharacterCount
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker synthesis failed for {ModelKey}", modelKey);
            
            // Return fallback silence
            return SynthesisFallback.Silence(model, request, startTime, FallbackSampleRate);
        }
    }
}
