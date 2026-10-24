namespace SharpAudio.Api.Options;

/// <summary>
/// VAD segmentation policy (mirrors <see cref="AsrStreamingOptions"/>'s standalone-section pattern),
/// applied by <see cref="Services.VadSegmenter"/>. Per-request overrides layer on top of these
/// defaults; diarization passes its own thresholds instead of these.
/// </summary>
public sealed class VadOptions
{
    public const string SectionName = "VadOptions";

    /// <summary>Speech probability that opens a span.</summary>
    public float Onset { get; init; } = 0.5f;

    /// <summary>Speech probability that closes an open span (hysteresis).</summary>
    public float Offset { get; init; } = 0.3f;

    /// <summary>Extend each span backwards by this many ms, clamped to the start of the audio.</summary>
    public double PadOnsetMs { get; init; } = 200;

    /// <summary>Extend each span forwards by this many ms, clamped to the end of the audio.</summary>
    public double PadOffsetMs { get; init; } = 200;

    /// <summary>A gap shorter than this is merged with the neighbouring spans.</summary>
    public double MinDurationOffMs { get; init; } = 500;

    /// <summary>A span shorter than this is dropped. 0 for ASR VAD; diarization uses 511 ms.</summary>
    public double MinDurationOnMs { get; init; } = 0;

    /// <summary>Whether trailing-silence endpointing (see <see cref="Services.VadEndpointer"/>) is on.</summary>
    public bool EnableEndpointing { get; init; }

    /// <summary>Trailing silence (ms) that ends an utterance. Mirrors riva's stop_history_eou.</summary>
    public double StopHistoryEouMs { get; init; } = 800;

    /// <summary>Copy with per-request onset/offset applied; null values keep these defaults.</summary>
    public VadOptions WithOverrides(float? onset, float? offset) => new VadOptions
    {
        Onset = onset ?? Onset,
        Offset = offset ?? Offset,
        PadOnsetMs = PadOnsetMs,
        PadOffsetMs = PadOffsetMs,
        MinDurationOffMs = MinDurationOffMs,
        MinDurationOnMs = MinDurationOnMs,
        EnableEndpointing = EnableEndpointing,
        StopHistoryEouMs = StopHistoryEouMs
    };
}
