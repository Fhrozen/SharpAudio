// wiki: music#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

/// <summary>Generated audio plus the timing needed for the metrics response.</summary>
public sealed record MusicGenerationResult(
    byte[] AudioBytes,
    string ContentType,
    string FileName,
    int SampleRate,
    int Channels,
    double ProcessingTimeSeconds,
    double AudioDurationSeconds)
{
    public double Rtf => ProcessingTimeSeconds / AudioDurationSeconds;
}
