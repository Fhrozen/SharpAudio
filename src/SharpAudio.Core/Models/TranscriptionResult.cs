// wiki: asr-engines#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

public sealed record TranscriptionResult(
    string Text,
    string? DetectedLanguage,
    double ProcessingTimeSeconds,
    double AudioDurationSeconds,
    int CharacterCount,
    IReadOnlyList<TranscriptionSegment>? Segments = null)
{
    public double Rtf => ProcessingTimeSeconds / AudioDurationSeconds;
}
