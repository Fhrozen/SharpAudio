using SharpAudio.Api.Services;

namespace SharpAudio.Api.Tests;

public sealed class VadWindowStreamTests
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 512; // 32 ms at 16 kHz

    [Fact]
    public void CompleteWindows_yields_one_window_per_frame_and_carries_the_remainder()
    {
        var stream = new VadWindowStream(FrameSamples, SampleRate);
        var samples = Samples(1184);

        var windows = stream.CompleteWindows(samples).ToList();

        Assert.Equal(2, windows.Count);
        Assert.Equal(samples.Take(512), windows[0]);
        Assert.Equal(samples.Skip(512).Take(512), windows[1]);

        Assert.Equal(samples.Skip(1024).Take(160).Concat(new float[352]), stream.FlushPaddedWindow());
        Assert.Null(stream.FlushPaddedWindow());
    }

    [Fact]
    public void CompleteWindows_carries_a_partial_window_across_calls_without_dropping_samples()
    {
        var stream = new VadWindowStream(FrameSamples, SampleRate);
        var samples = Samples(1184);

        var windows = new List<float[]>();
        foreach (var chunk in Split(samples, 300))
        {
            windows.AddRange(stream.CompleteWindows(chunk));
        }

        var last = stream.FlushPaddedWindow();
        Assert.NotNull(last);
        windows.Add(last);

        Assert.Equal(3, windows.Count);
        Assert.Equal(samples.Take(1024), windows[0].Concat(windows[1]));
        Assert.Equal(samples.Skip(1024).Take(160), last.Take(160));
        Assert.All(last.Skip(160), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void CompleteWindows_emits_nothing_until_a_window_is_full()
    {
        var stream = new VadWindowStream(FrameSamples, SampleRate);

        Assert.Empty(stream.CompleteWindows(Samples(511)));
        Assert.Single(stream.CompleteWindows(Samples(1)));
    }

    [Fact]
    public void Reset_clears_the_carried_remainder()
    {
        var stream = new VadWindowStream(FrameSamples, SampleRate);
        stream.CompleteWindows(Samples(200));
        stream.Reset();

        Assert.Null(stream.FlushPaddedWindow());
    }

    [Theory]
    [InlineData(16000, 512, 32)]
    [InlineData(8000, 256, 32)]
    public void FrameDurationMs_is_the_same_at_both_supported_rates(int sampleRate, int frameSamples, double expectedMs)
    {
        Assert.Equal(expectedMs, new VadWindowStream(frameSamples, sampleRate).FrameDurationMs);
    }

    private static float[] Samples(int count) => Enumerable.Range(0, count).Select(i => (float)i).ToArray();

    private static IEnumerable<float[]> Split(float[] samples, int size)
    {
        for (var offset = 0; offset < samples.Length; offset += size)
        {
            yield return samples.Skip(offset).Take(size).ToArray();
        }
    }
}
