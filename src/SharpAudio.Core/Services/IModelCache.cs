// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IModelCache
{
    Task<string> EnsureModelAsync(IModelDefinition model, CancellationToken cancellationToken);

    /// <summary>True when a model has been fully ensured at least once. Warmup uses this so an engine
    /// is never built on top of a partial download.</summary>
    bool IsModelEnsured(IModelDefinition model);
}
