// wiki: diarization#domain-model-and-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public interface IDiarizer
{
    Task<DiarizationResult> DiarizeAsync(
        DiarModelDefinition model,
        string modelDirectory,
        byte[] audioBytes,
        int speakerCount,
        CancellationToken cancellationToken);
}
