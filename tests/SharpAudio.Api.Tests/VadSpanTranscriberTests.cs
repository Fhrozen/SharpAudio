// wiki: testing#unit-tests
using SharpAudio.Api.Models;
using SharpAudio.Api.Options;
using SharpAudio.Api.Services;

namespace SharpAudio.Api.Tests;

public class VadSpanTranscriberTests
{
    private const int TestFrameSamples = 512;
    private const int TestSampleRate = 16000;

    private sealed class FakeVadEngine(float[] probabilities) : ISileroVadEngine
    {
        public int ResetCalls { get; private set; }

        public void Reset() => ResetCalls++;

        public bool ContainsSpeech(float[] samples, float threshold) => probabilities.Any(p => p > threshold);

        public int FrameSamples => TestFrameSamples;

        public double FrameDurationMs => 32;

        public int SampleRate => TestSampleRate;

        public float[] GetProbabilities(float[] samples) => probabilities;

        public float[] FlushProbabilities() => [];
    }

    private static float[] Samples(int frameCount) => new float[frameCount * TestFrameSamples];

    [Fact]
    public void Transcribes_each_speech_span_and_shifts_timestamps_to_audio_time()
    {
        // Two speech runs separated by a gap wider than minDurationOffMs after padding.
        var probabilities = new float[34];
        probabilities[0] = probabilities[1] = 0.9f;
        probabilities[32] = probabilities[33] = 0.9f;

        var transcribed = new List<float[]>();
        var engine = new FakeVadEngine(probabilities);
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (spanSamples, _) =>
        {
            transcribed.Add(spanSamples);
            return Task.FromResult(new SpanTranscription($"span{transcribed.Count}", "en", (IReadOnlyList<TranscriptionSegment>)[]));
        });

        var result = transcriber.TranscribeAsync(Samples(34), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(2, transcribed.Count);
        Assert.Equal("span1 span2", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1, engine.ResetCalls);
    }

    [Fact]
    public void Slices_are_the_padded_spans_at_the_vad_sample_rate()
    {
        float[] probabilities = [0.9f, 0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f];
        var engine = new FakeVadEngine(probabilities);
        var slices = new List<int>();
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (spanSamples, _) =>
        {
            slices.Add(spanSamples.Length);
            return Task.FromResult(new SpanTranscription("x", null, (IReadOnlyList<TranscriptionSegment>)[]));
        });

        // Audio is longer than the probability buffer, so the padded span is sliced in full.
        transcriber.TranscribeAsync(Samples(12), CancellationToken.None).GetAwaiter().GetResult();

        // First frame is padded to 0, the span ends at 264 ms (2 frames + padOffsetMs).
        Assert.Single(slices);
        Assert.Equal(4224, slices[0]);
    }

    [Fact]
    public void Padding_past_the_end_of_the_audio_is_clamped()
    {
        float[] probabilities = [0.9f, 0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f];
        var engine = new FakeVadEngine(probabilities);
        var slices = new List<int>();
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (spanSamples, _) =>
        {
            slices.Add(spanSamples.Length);
            return Task.FromResult(new SpanTranscription("x", null, (IReadOnlyList<TranscriptionSegment>)[]));
        });

        transcriber.TranscribeAsync(Samples(probabilities.Length), CancellationToken.None).GetAwaiter().GetResult();

        // The padded span runs to 264 ms, which is past the 256 ms of audio available.
        Assert.Single(slices);
        Assert.Equal(4096, slices[0]);
    }

    [Fact]
    public void Segment_ids_are_sequential_and_times_shifted_across_spans()
    {
        var probabilities = new float[34];
        probabilities[0] = probabilities[1] = 0.9f;
        probabilities[32] = probabilities[33] = 0.9f;

        var engine = new FakeVadEngine(probabilities);
        var spans = VadSegmenter.Segment(probabilities, 32, new VadOptions());
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (spanSamples, _) =>
        {
            var start = spanSamples.Length / (double)TestSampleRate;
            return Task.FromResult(new SpanTranscription(
                "word",
                "en",
                (IReadOnlyList<TranscriptionSegment>)[new TranscriptionSegment(1, 0, start, "word")]));
        });

        // Audio is longer than the probability buffer so padded spans are not clamped.
        var result = transcriber.TranscribeAsync(Samples(41), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(2, spans.Count);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1, result.Segments[0].Id);
        Assert.Equal(0.0, result.Segments[0].Start, 3);
        Assert.Equal(0.264, result.Segments[0].End, 3);
        Assert.Equal(2, result.Segments[1].Id);
        Assert.Equal(0.824, result.Segments[1].Start, 3);
        Assert.Equal(1.288, result.Segments[1].End, 3);
        Assert.Equal("word word", result.Text);
    }

    [Fact]
    public void Silence_produces_no_spans_and_no_transcription_calls()
    {
        var engine = new FakeVadEngine([0.1f, 0.1f, 0.1f]);
        var calls = 0;
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new SpanTranscription("unexpected", null, (IReadOnlyList<TranscriptionSegment>)[]));
        });

        var result = transcriber.TranscribeAsync(Samples(3), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(0, calls);
        Assert.Equal("", result.Text);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void Engine_is_reset_before_each_utterance()
    {
        var engine = new FakeVadEngine([0.9f]);
        var transcriber = new VadSpanTranscriber(engine, new VadOptions(), (_, _) =>
            Task.FromResult(new SpanTranscription("t", null, [])));

        transcriber.TranscribeAsync(Samples(1), CancellationToken.None).GetAwaiter().GetResult();
        transcriber.TranscribeAsync(Samples(1), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(2, engine.ResetCalls);
    }
}
