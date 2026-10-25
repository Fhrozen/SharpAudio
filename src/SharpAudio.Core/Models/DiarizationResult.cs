// wiki: diarization#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

/// <summary>Speaker turns for one audio, plus the timing needed for the metrics response.</summary>
public sealed record DiarizationResult(
    IReadOnlyList<SpeakerSegment> Segments,
    int SpeakerCount,
    double ProcessingTimeSeconds,
    double AudioDurationSeconds)
{
    public double Rtf => ProcessingTimeSeconds / AudioDurationSeconds;
}
