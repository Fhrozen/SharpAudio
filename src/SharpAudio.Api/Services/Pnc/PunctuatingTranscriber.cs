using SharpAudio.Api.Contracts;
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Decorator that punctuates ASR output when the request asks for it. In worker mode the same
/// step runs inside the worker process, so this decorator is registered only for in-process ASR.
/// Streaming sessions are left unpunctuated because updates are incremental.</summary>
public sealed class PunctuatingTranscriber : IAsrTranscriber, IIdleTrackingTranscriber, IDisposable
{
    private readonly IAsrTranscriber _inner;
    private readonly IPncModelCatalog _catalog;
    private readonly IModelCache _modelCache;
    private readonly IPncEnginePool _pool;

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

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines()
    {
        var engines = new Dictionary<string, DateTime>();

        if (_inner is IIdleTrackingTranscriber tracking)
        {
            foreach (var kvp in tracking.GetLoadedEngines())
            {
                engines[kvp.Key] = kvp.Value;
            }
        }

        foreach (var kvp in _pool.GetLoadedEngines())
        {
            engines[kvp.Key] = kvp.Value;
        }

        return engines;
    }

    public bool TryReleaseEngine(string engineKey) =>
        _pool.TryReleaseEngine(engineKey) || (_inner is IIdleTrackingTranscriber tracking && tracking.TryReleaseEngine(engineKey));

    public void Dispose()
    {
        _pool.Dispose();
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
