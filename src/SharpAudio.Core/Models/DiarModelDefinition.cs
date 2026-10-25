// wiki: diarization#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

/// <summary>Diarization task model definition. Diarization models are fixed to a maximum number of
/// speakers, which is a property of the model rather than of the request.</summary>
public sealed class DiarModelDefinition : IModelDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Engine { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public IReadOnlyList<ModelAsset> Assets { get; init; } = [];
    public IReadOnlyList<string> SupportedLanguages { get; init; } = [];

    /// <summary>How many speakers the model can separate at once.</summary>
    public int MaxSpeakers { get; init; }
}
