// wiki: asr-engines#vad-support
using SharpAudio.Api.Models;
using SharpAudio.Api.Options;

namespace SharpAudio.Api.Services;

/// <summary>Pure VAD segmentation policy over per-frame speech probabilities (no ONNX): hysteresis
/// binarization, padding, gap merging, min-duration filtering. Shared by ASR VAD and diarization,
/// which passes its own thresholds. Timestamps are seconds from the start of the audio.</summary>
public static class VadSegmenter
{
    /// <summary>Segment <paramref name="probabilities"/> (one value per <paramref name="frameMs"/>
    /// frame) into speech spans.</summary>
    public static IReadOnlyList<SpeechSpan> Segment(IReadOnlyList<float> probabilities, double frameMs, VadOptions options)
    {
        var padded = Binarize(probabilities, options)
            .Select(span => (StartMs: Math.Max(0.0, span.StartFrame * frameMs - options.PadOnsetMs),
                             EndMs: span.EndFrame * frameMs + options.PadOffsetMs))
            .ToList();

        var merged = new List<(double StartMs, double EndMs)>();
        foreach (var span in padded)
        {
            if (merged.Count > 0 && span.StartMs - merged[^1].EndMs < options.MinDurationOffMs)
            {
                merged[^1] = (merged[^1].StartMs, Math.Max(merged[^1].EndMs, span.EndMs));
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged
            .Where(span => span.EndMs - span.StartMs >= options.MinDurationOnMs)
            .Select(span => new SpeechSpan(span.StartMs / 1000.0, span.EndMs / 1000.0))
            .ToList();
    }

    // Frame ranges, end exclusive: the frame that drops below the offset is not part of the span.
    private static List<(int StartFrame, int EndFrame)> Binarize(IReadOnlyList<float> probabilities, VadOptions options)
    {
        var spans = new List<(int StartFrame, int EndFrame)>();
        var start = -1;

        for (var frame = 0; frame < probabilities.Count; frame++)
        {
            var probability = probabilities[frame];
            if (start < 0)
            {
                if (probability >= options.Onset)
                {
                    start = frame;
                }
            }
            else if (probability < options.Offset)
            {
                spans.Add((start, frame));
                start = -1;
            }
        }

        if (start >= 0)
        {
            spans.Add((start, probabilities.Count));
        }

        return spans;
    }
}
