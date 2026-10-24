// wiki: pnc#engine
namespace SharpAudio.Api.Services.Pnc;

/// <summary>Per-model PnC config (label lists and windowing) read from the model's config.yaml, so a
/// model swap needs no code change.</summary>
public sealed record PncModelConfig
{
    public required IReadOnlyList<string> PreLabels { get; init; }

    public required IReadOnlyList<string> PostLabels { get; init; }

    /// <summary>Sequence length the model was trained on; two slots are reserved for BOS/EOS.</summary>
    public int MaxLength { get; init; } = 256;

    /// <summary>Token overlap between subsegments. The reference default is 16, the model config 32.</summary>
    public int Overlap { get; init; } = 16;

    public string NullToken { get; init; } = "<NULL>";

    public string AcronymLabel { get; init; } = "<ACRONYM>";

    /// <summary>Padding id used to fill the sequence up to MaxLength.</summary>
    public int PadId { get; init; }

    public int UnkId { get; init; }

    /// <summary>Pre-label for a predicted id, null when the model predicted the null label.</summary>
    public string? PreLabel(int id) => PreLabels[id] == NullToken ? null : PreLabels[id];

    /// <summary>Post-label for a predicted id, null when the model predicted the null label.</summary>
    public string? PostLabel(int id) => PostLabels[id] == NullToken ? null : PostLabels[id];
}
