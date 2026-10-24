// wiki: backend-architecture#model-definition-and-config
namespace SharpAudio.Core.Models;

/// <summary>The shape every task's model definition has in common: identity, engine, assets and
/// languages. Task-specific definitions add their own paths on top of this.</summary>
public interface IModelDefinition
{
    string Name { get; }
    string DisplayName { get; }
    string Description { get; }
    string Engine { get; }
    IReadOnlyList<ModelAsset> Assets { get; }
    IReadOnlyList<string> SupportedLanguages { get; }
}
