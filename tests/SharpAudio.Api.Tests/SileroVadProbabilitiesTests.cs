// wiki: testing#unit-tests
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

public sealed class SileroVadProbabilitiesTests
{
    // Contract test for the per-frame probability path: a fake engine built on the
    // same window stream must agree between whole-buffer and streaming input.
    private sealed class FakeProbabilitiesEngine(int frameSamples, int sampleRate) : ISileroVadEngine
    {
        private readonly VadWindowStream _stream = new(frameSamples, sampleRate);

        public void Reset() => _stream.Reset();

        public bool ContainsSpeech(float[] samples, float threshold) => GetProbabilities(samples).Any(p => p > threshold);

        public int FrameSamples => frameSamples;

        public double FrameDurationMs => _stream.FrameDurationMs;

        public int SampleRate => sampleRate;

        public float[] GetProbabilities(float[] samples) => [.. _stream.CompleteWindows(samples).Select(Probability)];

        public float[] FlushProbabilities()
        {
            var window = _stream.FlushPaddedWindow();
            return window is null ? [] : [Probability(window)];
        }

        private static float Probability(float[] window) => window[0] > 0 ? 0.9f : 0.1f;
    }

    [Fact]
    public void Whole_buffer_and_streaming_paths_agree()
    {
        const int SampleRate = 16000;
        var samples = Enumerable.Range(0, 1184).Select(i => (float)(i % 300)).ToArray();

        var wholeEngine = new FakeProbabilitiesEngine(512, SampleRate);
        var whole = wholeEngine.GetProbabilities(samples);
        whole = [.. whole, .. wholeEngine.FlushProbabilities()];

        var streaming = new FakeProbabilitiesEngine(512, SampleRate);
        var probs = new List<float>();
        for (var offset = 0; offset < samples.Length; offset += 300)
        {
            probs.AddRange(streaming.GetProbabilities(samples.Skip(offset).Take(300).ToArray()));
        }
        probs.AddRange(streaming.FlushProbabilities());

        Assert.Equal(3, whole.Length);
        Assert.Equal(whole, probs);
    }

    [Fact]
    public void One_probability_is_returned_per_frame_and_frame_duration_is_32ms_at_16khz()
    {
        var engine = new FakeProbabilitiesEngine(512, 16000);
        var oneSecond = Enumerable.Range(0, 16000).Select(i => (float)(i % 300)).ToArray();

        var probs = engine.GetProbabilities(oneSecond);

        Assert.Equal(512, engine.FrameSamples);
        Assert.Equal(32.0, engine.FrameDurationMs);
        Assert.Equal(31, probs.Length); // 16000 / 512 whole frames, 128 samples carried
        Assert.Single(engine.FlushProbabilities()); // the carried remainder is zero-padded
    }
}
