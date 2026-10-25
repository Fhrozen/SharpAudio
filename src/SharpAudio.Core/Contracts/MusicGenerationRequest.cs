// wiki: music#domain-model-and-abstractions
namespace SharpAudio.Core.Contracts;

/// <summary>Parsed JSON body from a POST /v1/audio/music request.</summary>
public sealed class MusicGenerationRequest
{
    public string Model { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;

    /// <summary>Length of the generation in seconds; clamped by the task options when omitted.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Sampler steps; the pingpong sampler is used when this is left at the default.</summary>
    public int Steps { get; init; }

    public long Seed { get; init; }
}
