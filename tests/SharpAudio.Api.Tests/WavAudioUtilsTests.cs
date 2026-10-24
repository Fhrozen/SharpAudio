// wiki: testing#unit-tests
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

/// <summary>Covers the already-normalised fast path that lets <see cref="AudioFormatConverter"/>
/// return an upload unchanged instead of spawning ffmpeg.</summary>
public sealed class WavAudioUtilsTests
{
    private static byte[] Pcm(int sampleCount) => new byte[sampleCount * 2];

    [Fact]
    public void Already_normalised_wav_is_recognised()
    {
        var wav = WavAudioUtils.WrapPcmAsWav(Pcm(160), sampleRate: 16000);
        Assert.True(WavAudioUtils.IsPcm16Mono16kWav(wav));
    }

    [Fact]
    public void Stereo_or_other_sample_rate_is_not_the_normalised_format()
    {
        Assert.False(WavAudioUtils.IsPcm16Mono16kWav(WavAudioUtils.WrapPcmAsWav(Pcm(160), 16000, channels: 2)));
        Assert.False(WavAudioUtils.IsPcm16Mono16kWav(WavAudioUtils.WrapPcmAsWav(Pcm(160), 44100)));
    }

    [Fact]
    public void Non_wav_and_truncated_headers_are_rejected()
    {
        Assert.False(WavAudioUtils.IsPcm16Mono16kWav("not a wav"u8.ToArray()));
        Assert.False(WavAudioUtils.IsPcm16Mono16kWav(WavAudioUtils.WrapPcmAsWav(Pcm(160), 16000)[..40]));
    }

    [Fact]
    public void Placeholder_chunk_sizes_are_not_a_normalised_wav()
    {
        var wav = WavAudioUtils.WrapPcmAsWav(Pcm(160), sampleRate: 16000);

        // ffmpeg writes 0xFFFFFFFF for chunk sizes it cannot know when piping to stdout; such a
        // file is only valid once the sizes are patched, so the fast path must not accept it.
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 4);

        Assert.False(WavAudioUtils.IsPcm16Mono16kWav(wav));
    }
}
