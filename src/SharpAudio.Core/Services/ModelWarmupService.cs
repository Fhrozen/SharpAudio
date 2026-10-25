// wiki: backend-architecture#model-cache-and-warmup
using Microsoft.Extensions.Options;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;

namespace SharpAudio.Core.Services;

/// <summary>Pre-downloads every model a catalog lists on startup. One implementation serves every
/// task — TTS, ASR and the future ones — because a catalog, a cache and a label are all it needs.</summary>
public sealed class ModelWarmupService<T> : BackgroundService where T : IModelDefinition
{
    private readonly IModelLookup<T> _catalog;
    private readonly IModelCache _cache;
    private readonly IEngineWarmer<T>? _warmer;
    private readonly ModelCacheOptions _options;
    private readonly ILogger _logger;
    private readonly string _label;

    public ModelWarmupService(
        IModelLookup<T> catalog,
        IModelCache cache,
        ILoggerFactory loggerFactory,
        string label,
        IEngineWarmer<T>? warmer = null,
        IOptions<ModelCacheOptions>? options = null)
    {
        _catalog = catalog;
        _cache = cache;
        _warmer = warmer;
        _options = options?.Value ?? new ModelCacheOptions();
        _label = label;
        _logger = loggerFactory.CreateLogger($"{label} warmup");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>Run one warmup pass over every supported model. Public so tests can exercise the
    /// service without a host.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(Math.Max(1, _options.WarmupConcurrency));
        var tasks = new List<Task>();

        foreach (var model in _catalog.GetSupportedModels())
        {
            tasks.Add(WarmUpModelAsync(model, gate, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task WarmUpModelAsync(T model, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            acquired = true;
            var modelDirectory = await _cache.EnsureModelAsync(model, cancellationToken);
            _logger.LogInformation("{Label} model {Model} is ready", _label, model.Name);

            // An engine must never be built on top of a partial download: only a model the cache
            // recorded as fully ensured is eligible.
            if (_warmer == null || !_options.WarmEngines || !_cache.IsModelEnsured(model))
            {
                return;
            }

            if (await _warmer.WarmUpAsync(model, modelDirectory, cancellationToken))
            {
                _logger.LogInformation("{Label} engine for {Model} is warm", _label, model.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: the remaining models are skipped rather than logged as failures.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm up {Label} model {Model}", _label, model.Name);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }
}
