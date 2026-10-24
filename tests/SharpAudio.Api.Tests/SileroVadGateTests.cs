using SharpAudio.Api.Services;

namespace SharpAudio.Api.Tests;

public sealed class SileroVadGateTests
{
    private sealed class FakeVadEngine(Func<float[], bool> hasSpeech) : ISileroVadEngine
    {
        public void Reset()
        {
        }

        public bool ContainsSpeech(float[] samples, float threshold) => hasSpeech(samples);

        public int FrameSamples => 512;

        public double FrameDurationMs => 32;

        public int SampleRate => 16000;

        public float[] GetProbabilities(float[] samples) => [];

        public float[] FlushProbabilities() => [];
    }

    private const int ChunkSamples = 8960;
    private const int SampleRate = 16000; // chunk duration = 560ms

    [Fact]
    public void ShouldDropChunk_never_drops_while_speech_present()
    {
        var gate = new SileroVadGate(new FakeVadEngine(_ => true), threshold: 0.5f, ChunkSamples, SampleRate, silenceDurationMs: 3360, prefixPaddingMs: 560);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(gate.ShouldDropChunk(new float[ChunkSamples]));
        }
    }

    [Fact]
    public void ShouldDropChunk_drops_once_consecutive_silence_reaches_the_configured_duration()
    {
        // silenceDurationChunks = max(1, 560ms / 560ms) = 1 -> drops on the very first silent chunk.
        var gate = new SileroVadGate(new FakeVadEngine(_ => false), threshold: 0.5f, ChunkSamples, SampleRate, silenceDurationMs: 560, prefixPaddingMs: 560);

        Assert.True(gate.ShouldDropChunk(new float[ChunkSamples]));
    }

    [Fact]
    public void ShouldDropChunk_resets_silence_counter_when_speech_returns()
    {
        var hasSpeech = false;
        var gate = new SileroVadGate(new FakeVadEngine(_ => hasSpeech), threshold: 0.5f, ChunkSamples, SampleRate, silenceDurationMs: 3360, prefixPaddingMs: 560);

        // silenceDurationChunks = max(1, 3360/560) = 6, so a single silent chunk never drops alone.
        Assert.False(gate.ShouldDropChunk(new float[ChunkSamples]));

        hasSpeech = true;
        Assert.False(gate.ShouldDropChunk(new float[ChunkSamples]));

        hasSpeech = false;
        Assert.False(gate.ShouldDropChunk(new float[ChunkSamples]));
    }

    [Fact]
    public void LastChunkHadSpeech_tracks_the_decision_of_each_chunk()
    {
        var hasSpeech = true;
        var gate = new SileroVadGate(new FakeVadEngine(_ => hasSpeech), threshold: 0.5f, ChunkSamples, SampleRate, silenceDurationMs: 3360, prefixPaddingMs: 560);

        gate.ShouldDropChunk(new float[ChunkSamples]);
        Assert.True(gate.LastChunkHadSpeech);

        hasSpeech = false;
        gate.ShouldDropChunk(new float[ChunkSamples]);
        Assert.False(gate.LastChunkHadSpeech);

        hasSpeech = true;
        gate.ShouldDropChunk(new float[ChunkSamples]);
        Assert.True(gate.LastChunkHadSpeech);
    }
}
