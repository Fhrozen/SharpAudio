// wiki: backend-architecture#model-cache-and-warmup
namespace SharpAudio.Core.Models;

/// <summary>Hook for models that also carry per-speaker voice files alongside their assets, i.e. the
/// TTS definitions. The cache downloads assets for every <see cref="IModelDefinition"/> and voices
/// only for models that implement this.</summary>
public interface IVoiceDownloadable : IModelDefinition
{
    string? VoicesPath { get; }
    string? VoicesBaseUrl { get; }
    string VoiceFileExtension { get; }
    IReadOnlyList<string> Speakers { get; }
}
