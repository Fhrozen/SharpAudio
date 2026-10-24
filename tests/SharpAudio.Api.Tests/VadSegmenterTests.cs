// wiki: testing#unit-tests
using SharpAudio.Api.Models;
using SharpAudio.Api.Options;
using SharpAudio.Api.Services;

namespace SharpAudio.Api.Tests;

public sealed class VadSegmenterTests
{
    private const double FrameMs = 32;

    private static VadOptions Options(
        float onset = 0.5f,
        float offset = 0.3f,
        double padOnsetMs = 0,
        double padOffsetMs = 0,
        double minOffMs = 0,
        double minOnMs = 0) => new()
        {
            Onset = onset,
            Offset = offset,
            PadOnsetMs = padOnsetMs,
            PadOffsetMs = padOffsetMs,
            MinDurationOffMs = minOffMs,
            MinDurationOnMs = minOnMs,
        };

    private static double Seconds(double ms) => ms / 1000.0;

    [Fact]
    public void Segment_keeps_a_span_open_between_offset_and_onset_and_closes_below_offset()
    {
        var spans = VadSegmenter.Segment([0.2f, 0.6f, 0.4f, 0.2f, 0.55f, 0.2f], FrameMs, Options());

        Assert.Equal(2, spans.Count);
        Assert.Equal(Seconds(32), spans[0].Start);
        Assert.Equal(Seconds(96), spans[0].End); // frame 3 drops below offset, so it is not part of the span
        Assert.Equal(Seconds(128), spans[1].Start);
        Assert.Equal(Seconds(160), spans[1].End);
    }

    [Fact]
    public void Segment_never_opens_a_span_below_onset()
    {
        Assert.Empty(VadSegmenter.Segment([0.4f, 0.4f, 0.4f], FrameMs, Options()));
    }

    [Fact]
    public void Segment_pads_both_ends_and_clamps_the_start_to_the_start_of_audio()
    {
        var spans = VadSegmenter.Segment([0.6f, 0.2f], FrameMs, Options(padOnsetMs: 200, padOffsetMs: 200));

        Assert.Single(spans);
        Assert.Equal(0, spans[0].Start);
        Assert.Equal(Seconds(232), spans[0].End);
    }

    [Fact]
    public void Segment_merges_gaps_shorter_than_min_off_and_keeps_longer_ones()
    {
        IReadOnlyList<float> probabilities = [0.6f, 0.2f, 0.2f, 0.2f, 0.6f, 0.2f]; // gap = 96 ms

        var merged = VadSegmenter.Segment(probabilities, FrameMs, Options(minOffMs: 200));
        Assert.Single(merged);
        Assert.Equal(0, merged[0].Start);
        Assert.Equal(Seconds(160), merged[0].End);

        var separate = VadSegmenter.Segment(probabilities, FrameMs, Options(minOffMs: 50));
        Assert.Equal(2, separate.Count);
        Assert.Equal(Seconds(32), separate[0].End);
        Assert.Equal(Seconds(128), separate[1].Start);
    }

    [Fact]
    public void Segment_drops_spans_shorter_than_min_on()
    {
        IReadOnlyList<float> probabilities = [0.6f, 0.6f, 0.6f, 0.2f, 0.2f, 0.2f, 0.6f, 0.2f]; // 96 ms and 32 ms spans

        Assert.Empty(VadSegmenter.Segment(probabilities, FrameMs, Options(minOnMs: 100)));

        var kept = VadSegmenter.Segment(probabilities, FrameMs, Options(minOnMs: 50));
        Assert.Single(kept);
        Assert.Equal(0, kept[0].Start);
        Assert.Equal(Seconds(96), kept[0].End);
    }

    [Fact]
    public void Segment_closes_a_span_that_is_still_open_at_the_end_of_the_buffer()
    {
        var spans = VadSegmenter.Segment([0.6f, 0.4f], FrameMs, Options());

        Assert.Single(spans);
        Assert.Equal(Seconds(64), spans[0].End);
    }

    [Fact]
    public void Segment_accepts_diarization_thresholds_as_per_call_options()
    {
        // Sortformer's thresholds: 0.6 and 0.6 never reach onset 0.641, so nothing is segmented.
        Assert.Empty(VadSegmenter.Segment([0.6f, 0.6f, 0.5f], 80, Options(onset: 0.641f, offset: 0.561f)));
    }

    [Fact]
    public void Segment_returns_nothing_for_an_empty_probability_list()
    {
        Assert.Empty(VadSegmenter.Segment([], FrameMs, Options()));
    }
}
