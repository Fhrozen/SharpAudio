// wiki: testing#unit-tests
using SharpAudio.Core.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncWindowPlannerTests
{
    private static PncModelConfig Config(int maxLength = 256, int overlap = 32) => new()
    {
        PreLabels = ["<NULL>"],
        PostLabels = ["<NULL>"],
        MaxLength = maxLength,
        Overlap = overlap
    };

    private static string Describe(IReadOnlyList<PncWindow> windows) =>
        string.Join(" ", windows.Select(w => $"{w.InputIndex}:{w.Start}+{w.Length}"));

    [Fact]
    public void Plan_short_input_produces_one_window()
    {
        var windows = PncWindowPlanner.Plan(Enumerable.Range(0, 10).ToList(), Config());

        Assert.Equal("0:0+10", Describe(windows));
    }

    [Fact]
    public void Plan_input_exactly_filling_the_window_produces_one_window()
    {
        // 256 minus the two slots reserved for BOS/EOS.
        var windows = PncWindowPlanner.Plan(Enumerable.Range(0, 254).ToList(), Config());

        Assert.Equal("0:0+254", Describe(windows));
    }

    [Fact]
    public void Plan_long_input_overlaps_every_window_after_the_first()
    {
        var windows = PncWindowPlanner.Plan(Enumerable.Range(0, 600).ToList(), Config());

        // Window 1 starts 32 pieces before window 0 ended; window 2 covers the tail and is short.
        Assert.Equal("0:0+254 1:222+254 2:444+156", Describe(windows));
    }

    [Fact]
    public void Plan_last_window_is_clamped_to_the_input_length()
    {
        var windows = PncWindowPlanner.Plan(Enumerable.Range(0, 300).ToList(), Config());

        Assert.Equal("0:0+254 1:222+78", Describe(windows));
    }

    [Fact]
    public void Plan_windows_cover_every_piece_at_least_once()
    {
        var ids = Enumerable.Range(0, 600).ToList();
        var windows = PncWindowPlanner.Plan(ids, Config());

        var covered = new bool[ids.Count];
        foreach (var window in windows)
        {
            for (var i = window.Start; i < window.Start + window.Length; i++)
            {
                covered[i] = true;
            }
        }

        Assert.True(covered.All(c => c));
    }

    [Fact]
    public void Plan_rejects_overlap_that_is_not_smaller_than_the_usable_window()
    {
        Assert.Throws<ArgumentException>(() => PncWindowPlanner.Plan([1], Config(maxLength: 4, overlap: 2)));
    }
}
