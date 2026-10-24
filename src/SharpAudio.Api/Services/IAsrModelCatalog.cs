// wiki: asr-engines#domain-model-and-abstractions
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services;

public interface IAsrModelCatalog
{
    IReadOnlyCollection<AsrModelDefinition> GetSupportedModels();
    bool TryGetModel(string modelName, out AsrModelDefinition? model);
}
