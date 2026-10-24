// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IModelCache
{
    Task<string> EnsureModelAsync(IModelDefinition model, CancellationToken cancellationToken);
}
