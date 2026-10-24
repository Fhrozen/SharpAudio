// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IModelCatalog : IModelLookup<TtsModelDefinition>
{
}
