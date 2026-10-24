// wiki: asr-engines#domain-model-and-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IAsrModelCatalog : IModelLookup<AsrModelDefinition>
{
}
