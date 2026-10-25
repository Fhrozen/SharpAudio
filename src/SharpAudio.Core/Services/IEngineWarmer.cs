// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>Builds a model's engine up front so the first request does not pay for the model load.
/// Every implementation already knows how to build its own engine — a pool entry for the in-process
/// path, a worker process for the worker path — so warmup stays a capability of the existing services
/// instead of a new dependency graph. Callers must ensure the model's files first.</summary>
public interface IEngineWarmer<T> where T : IModelDefinition
{
    /// <summary>Build (or reuse) the engine for <paramref name="model"/>. Returns false when this
    /// implementation does not handle the model's engine. An engine build that fails is allowed to
    /// throw: the warmup service catches it, logs it and continues with the next model.</summary>
    Task<bool> WarmUpAsync(T model, string modelDirectory, CancellationToken cancellationToken);
}
