// wiki: backend-architecture#model-cache-and-warmup
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>Pre-downloads every model a catalog lists on startup. One implementation serves every
/// task — TTS, ASR and the future ones — because a catalog, a cache and a label are all it needs.</summary>
public sealed class ModelWarmupService<T> : BackgroundService where T : IModelDefinition
{
    private readonly IModelLookup<T> _catalog;
    private readonly IModelCache _cache;
    private readonly ILogger _logger;
    private readonly string _label;

    public ModelWarmupService(IModelLookup<T> catalog, IModelCache cache, ILoggerFactory loggerFactory, string label)
    {
        _catalog = catalog;
        _cache = cache;
        _label = label;
        _logger = loggerFactory.CreateLogger($"{label} warmup");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var model in _catalog.GetSupportedModels())
        {
            try
            {
                await _cache.EnsureModelAsync(model, stoppingToken);
                _logger.LogInformation("{Label} model {Model} is ready", _label, model.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm up {Label} model {Model}", _label, model.Name);
            }
        }
    }
}
