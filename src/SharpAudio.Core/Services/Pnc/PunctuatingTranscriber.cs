// wiki: pnc#wiring
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services.Pnc;

/// <summary>Decorator that punctuates ASR output when the request asks for it. In worker mode the same
/// step runs inside the worker process, so this decorator is registered only for in-process ASR.
/// Streaming sessions are left unpunctuated because updates are incremental.</summary>
public sealed class PunctuatingTranscriber : IAsrTranscriber, IIdleTracking, IDisposable
{
    private readonly IAsrTranscriber _inner;
    private readonly IPncModelCatalog _catalog;
    private readonly IModelCache _modelCache;
    private readonly IPncEnginePool _pool;
    private readonly IdleTrackingAggregate _tracking;

    public PunctuatingTranscriber(
        IAsrTranscriber inner,
        IPncModelCatalog catalog,
        IModelCache modelCache,
        IPncEnginePool pool)
    {
        _inner = inner;
        _catalog = catalog;
        _modelCache = modelCache;
        _pool = pool;
        // The PnC pool is tried first, then the inner transcriber when it can be tracked.
        _tracking = inner is IIdleTracking innerTracking
            ? new IdleTrackingAggregate(pool, innerTracking)
            : new IdleTrackingAggregate(pool);
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        var result = await _inner.TranscribeAsync(model, modelDirectory, request, audioBytes, cancellationToken);
        if (!request.Punctuate)
        {
            return result;
        }

        if (!_catalog.TryGetModel(request.PncModel ?? string.Empty, out var pncModel))
        {
            return result;
        }

        var pncDirectory = await _modelCache.EnsureModelAsync(pncModel!, cancellationToken);
        return PncTextApplier.Apply(_pool.GetOrCreateEngine(pncModel!, pncDirectory), result);
    }

    public Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
        AsrModelDefinition model,
        string modelDirectory,
        string? language,
        bool enableVad,
        bool enableEndpointing,
        double segmentSeconds,
        CancellationToken cancellationToken)
    {
        return _inner.CreateStreamingSessionAsync(model, modelDirectory, language, enableVad,
            enableEndpointing, segmentSeconds, cancellationToken);
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _tracking.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _tracking.TryReleaseEngine(engineKey);

    public void Dispose() => _tracking.Dispose();
}
