// wiki: music#domain-model-and-abstractions
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IMusicGenerator
{
    Task<MusicGenerationResult> GenerateAsync(
        MusicModelDefinition model,
        string modelDirectory,
        MusicGenerationRequest request,
        CancellationToken cancellationToken);
}
