// wiki: asr-engines#vad-support
using System.Text;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;

namespace SharpAudio.Core.Services;

/// <summary>VAD path for engines with no native VAD: segment with Silero and <see cref="VadSegmenter"/>,
/// transcribe each speech span on its own, and shift span-local timestamps back onto the whole-audio
/// timeline. The span callback is injected so the policy is testable without model weights.
/// The optional gate serialises segmentation only, because the LSTM state is carried across windowed
/// calls; spans are transcribed outside it, so inference for one request can overlap with the next
/// request's VAD run.</summary>
public sealed class VadSpanTranscriber(
    ISileroVadEngine vadEngine,
    VadOptions options,
    Func<float[], CancellationToken, Task<SpanTranscription>> transcribeSpan,
    SemaphoreSlim? gate = null)
{
    public async Task<SpanTranscription> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeechSpan> spans;
        if (gate is null)
        {
            spans = FindSpans(samples);
        }
        else
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                spans = FindSpans(samples);
            }
            finally
            {
                gate.Release();
            }
        }

        var text = new StringBuilder();
        var segments = new List<TranscriptionSegment>();
        string? detectedLanguage = null;
        var segmentId = 1;

        foreach (var span in spans)
        {
            var spanResult = await transcribeSpan(Slice(samples, span), cancellationToken);
            detectedLanguage ??= spanResult.DetectedLanguage;

            if (spanResult.Text.Length > 0)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(spanResult.Text);
            }

            foreach (var segment in spanResult.Segments)
            {
                segments.Add(segment with { Id = segmentId++, Start = span.Start + segment.Start, End = span.Start + segment.End });
            }
        }

        return new SpanTranscription(text.ToString(), detectedLanguage, segments);
    }

    private IReadOnlyList<SpeechSpan> FindSpans(float[] samples)
    {
        vadEngine.Reset();
        var probabilities = vadEngine.GetProbabilities(samples);
        var flushed = vadEngine.FlushProbabilities();
        if (flushed.Length > 0)
        {
            probabilities = [.. probabilities, .. flushed];
        }

        return VadSegmenter.Segment(probabilities, vadEngine.FrameDurationMs, options);
    }

    private float[] Slice(float[] samples, SpeechSpan span)
    {
        var start = Math.Clamp((int)Math.Round(span.Start * vadEngine.SampleRate), 0, samples.Length);
        var end = Math.Clamp((int)Math.Round(span.End * vadEngine.SampleRate), start, samples.Length);
        return samples[start..end];
    }
}
