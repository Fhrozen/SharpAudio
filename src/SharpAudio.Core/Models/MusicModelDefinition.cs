// wiki: music#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

/// <summary>Music task model definition. Music models are multi-graph ONNX bundles, so the definition
/// carries the directory holding them rather than a single file.</summary>
public sealed class MusicModelDefinition : IModelDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Engine { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public IReadOnlyList<ModelAsset> Assets { get; init; } = [];
    public IReadOnlyList<string> SupportedLanguages { get; init; } = [];
}
