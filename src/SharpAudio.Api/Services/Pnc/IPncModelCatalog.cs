// wiki: pnc#wiring
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services.Pnc;

public interface IPncModelCatalog
{
    IReadOnlyCollection<PncModelDefinition> GetSupportedModels();

    /// <summary>Resolves a model by name; an empty name falls back to the default model.</summary>
    bool TryGetModel(string modelName, out PncModelDefinition? model);
}
