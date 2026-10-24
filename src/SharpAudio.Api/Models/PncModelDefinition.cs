// wiki: pnc#model
namespace SharpAudio.Api.Models;

/// <summary>PnC model definition: the ONNX graph, the SentencePiece vocab and the label config, all
/// read from the model directory. Paths are relative so a model swap is a config change only.</summary>
public sealed class PncModelDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Engine { get; init; } = "pnc";
    public IReadOnlyList<ModelAsset> Assets { get; init; } = [];
    public string OnnxPath { get; init; } = string.Empty;
    public string TokenizerPath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedLanguages { get; init; } = [];
}
