// wiki: pnc#wiring
using SharpAudio.Core.Models;
using SharpAudio.Core.Services;

namespace SharpAudio.Core.Services.Pnc;

public interface IPncModelCatalog : IModelLookup<PncModelDefinition>
{
}
