// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>Lookup contract every model catalog exposes.</summary>
public interface IModelLookup<T> where T : IModelDefinition
{
    IReadOnlyCollection<T> GetSupportedModels();
    bool TryGetModel(string modelName, out T? model);
}

/// <summary>Storage shared by every model catalog: a case-insensitive name index built once at
/// construction, and the lookup against it. A catalog supplies its own config section, defaults and
/// extras (such as PnC's default model) and inherits everything else.</summary>
public abstract class ModelCatalogBase<T> : IModelLookup<T> where T : IModelDefinition
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, T> _models;

    protected ModelCatalogBase(IEnumerable<T> models)
    {
        _models = new Dictionary<string, T>(Comparer);
        foreach (var model in models)
        {
            _models[model.Name] = model;
        }
    }

    public IReadOnlyCollection<T> GetSupportedModels() => _models.Values;

    public virtual bool TryGetModel(string modelName, out T? model) => _models.TryGetValue(modelName, out model);
}
