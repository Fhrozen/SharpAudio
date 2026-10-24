// wiki: pnc#engine
namespace SharpAudio.Core.Services.Pnc;

/// <summary>A subsegment sent to the model: absolute start in the piece list, and how many pieces it holds.</summary>
public sealed record PncWindow(int InputIndex, int Start, int Length);

/// <summary>Splits a piece list into model-length subsegments. The first window has no overlap; each later
/// window starts `overlap` pieces before the end of the previous one, matching the reference dataset.</summary>
public static class PncWindowPlanner
{
    public static IReadOnlyList<PncWindow> Plan(IReadOnlyList<int> ids, PncModelConfig config)
    {
        var maxTokens = config.MaxLength - 2; // BOS and EOS slots
        if (maxTokens <= config.Overlap)
        {
            throw new ArgumentException("Overlap must be smaller than the usable window length.", nameof(config));
        }

        var windows = new List<PncWindow>();
        var start = 0;
        var inputIndex = 0;

        while (start < ids.Count)
        {
            var adjusted = start - (inputIndex == 0 ? 0 : config.Overlap);
            var stop = Math.Min(adjusted + maxTokens, ids.Count);

            windows.Add(new PncWindow(inputIndex, adjusted, stop - adjusted));
            inputIndex++;
            start = adjusted + maxTokens;
        }

        return windows;
    }
}
