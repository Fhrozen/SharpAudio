// wiki: asr-engines#domain-model-and-abstractions
namespace SharpAudio.Api.Contracts;

/// <summary>
/// Parsed form fields from a POST /v1/audio/transcriptions request (the audio file itself is
/// handled separately as a byte array).
/// </summary>
public sealed class AudioTranscriptionRequest
{
    public string Model { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string ResponseFormat { get; init; } = "json";
    public bool EnableVad { get; init; }
    public bool IncludeSegments { get; init; }
    public double MinSegmentDurationSeconds { get; init; } = 0.5;
    public bool Punctuate { get; init; }

    /// <summary>Optional PnC model name; empty uses the catalog default.</summary>
    public string? PncModel { get; init; }

    /// <summary>Optional per-request override of <see cref="Options.VadOptions.Onset"/>.</summary>
    public float? VadOnset { get; init; }

    /// <summary>Optional per-request override of <see cref="Options.VadOptions.Offset"/>.</summary>
    public float? VadOffset { get; init; }
}
