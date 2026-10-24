// wiki: asr-engines#whisper-engine
namespace SharpAudio.Core.Services;

/// <summary>Minimal WAV (RIFF/PCM) reader/resampler and the repo's single RIFF writer - just enough
/// for ASR input and TTS output, no external audio library.</summary>
public static class WavAudioUtils
{
    private static readonly byte[] RiffMarker = "RIFF"u8.ToArray();
    private static readonly byte[] WaveMarker = "WAVE"u8.ToArray();
    private static readonly byte[] FormatChunkMarker = "fmt "u8.ToArray();
    private static readonly byte[] DataChunkMarker = "data"u8.ToArray();

    public static double GetDurationSeconds(byte[] wavBytes)
    {
        if (!TryReadHeader(wavBytes, out var sampleRate, out var channels, out var bitsPerSample, out _, out var dataSize, out _) ||
            sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0)
        {
            return 0;
        }

        var bytesPerSample = bitsPerSample / 8;
        var sampleCount = dataSize / (channels * bytesPerSample);
        return (double)sampleCount / sampleRate;
    }

    /// <summary>True when the WAV is already the format the ASR engines consume — PCM (audio format 1),
    /// mono, 16-bit, 16 kHz — and the header sizes match the buffer, so nothing needs decoding or
    /// resampling. Lets <see cref="AudioFormatConverter"/> return such uploads unchanged instead of
    /// spawning ffmpeg, which is the dominant cost for short clips.</summary>
    public static bool IsPcm16Mono16kWav(byte[] wavBytes)
    {
        if (!TryReadHeader(wavBytes, out var sampleRate, out var channels, out var bitsPerSample, out var dataOffset, out var dataSize, out var audioFormat))
        {
            return false;
        }

        // The RIFF container size must also match the buffer: ffmpeg writes 0xFFFFFFFF for sizes it
        // cannot know when piping, so a placeholder here means the header was never patched.
        return audioFormat == 1 && channels == 1 && bitsPerSample == 16 && sampleRate == 16000 &&
            dataOffset + dataSize == wavBytes.Length &&
            BitConverter.ToUInt32(wavBytes, 4) == (uint)(wavBytes.Length - 8);
    }

    /// <summary>Decodes 16-bit PCM WAV bytes to mono float samples resampled to <paramref name="targetSampleRate"/>.</summary>
    public static float[] ReadMonoFloat(byte[] wavBytes, int targetSampleRate)
    {
        if (!TryReadHeader(wavBytes, out var sampleRate, out var channels, out var bitsPerSample, out var dataOffset, out var dataSize, out _) ||
            bitsPerSample != 16)
        {
            throw new NotSupportedException("Only 16-bit PCM WAV audio is supported.");
        }

        var bytesPerSample = bitsPerSample / 8;
        var frameCount = dataSize / (channels * bytesPerSample);
        var mono = new float[frameCount];

        for (var i = 0; i < frameCount; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var offset = dataOffset + (i * channels + c) * bytesPerSample;
                sum += BitConverter.ToInt16(wavBytes, offset);
            }

            mono[i] = (sum / (float)channels) / 32768f;
        }

        return sampleRate == targetSampleRate ? mono : Resample(mono, sampleRate, targetSampleRate);
    }

    /// <summary>Re-encodes arbitrary PCM16 WAV bytes as mono 16kHz PCM16 WAV - Whisper.net requires
    /// exactly 16kHz input and does not resample internally.</summary>
    public static byte[] ResampleToMono16kWav(byte[] wavBytes)
    {
        return EncodeMono16kWav(ReadMonoFloat(wavBytes, targetSampleRate: 16000));
    }

    /// <summary>Encode already-decoded mono float samples as a 16 kHz PCM16 WAV, e.g. one VAD span.</summary>
    public static byte[] EncodeMono16kWav(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];

        // Little-endian, matching BinaryWriter and every platform this repo runs on; writing the
        // bytes directly avoids allocating a 2-byte array per sample.
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }

        return WrapPcm16MonoAsWav(pcm, sampleRate: 16000);
    }

    /// <summary>Wraps raw 16-bit PCM mono samples in a minimal RIFF/WAV header.</summary>
    public static byte[] WrapPcm16MonoAsWav(byte[] pcmBytes, int sampleRate)
    {
        return WrapPcmAsWav(pcmBytes, sampleRate);
    }

    /// <summary>Wraps raw PCM samples in a minimal RIFF/WAV header. Single RIFF writer for the whole repo.</summary>
    public static byte[] WrapPcmAsWav(ReadOnlySpan<byte> pcmBytes, int sampleRate, int channels = 1, int bitsPerSample = 16)
    {
        using var stream = new MemoryStream(44 + pcmBytes.Length);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var dataSize = pcmBytes.Length;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        writer.Write(RiffMarker);
        writer.Write(36 + dataSize);
        writer.Write(WaveMarker);
        writer.Write(FormatChunkMarker);
        writer.Write(16);
        writer.Write((short)1);
        // numChannels is a WORD in the RIFF fmt chunk; writing it as an int would make the chunk
        // 18 bytes while the declared size stays 16, so readers misalign after the fmt chunk.
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(DataChunkMarker);
        writer.Write(dataSize);
        writer.Write(pcmBytes);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Duration of raw PCM data at the given format, e.g. the audio a TTS engine returned.</summary>
    public static double GetPcmDurationSeconds(int pcmByteCount, int sampleRate, int channels = 1, int bitsPerSample = 16)
    {
        if (sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0)
        {
            return 0;
        }

        var bytesPerSample = bitsPerSample / 8;
        var sampleCount = pcmByteCount / (channels * bytesPerSample);
        return (double)sampleCount / sampleRate;
    }

    /// <summary>Zero-filled WAV for the given duration, used when an engine cannot produce audio.</summary>
    public static byte[] GenerateSilenceWav(double durationSeconds, int sampleRate, int channels = 1, int bitsPerSample = 16)
    {
        var sampleCount = (int)(sampleRate * durationSeconds);
        var pcm = new byte[sampleCount * channels * bitsPerSample / 8];
        return WrapPcmAsWav(pcm, sampleRate, channels, bitsPerSample);
    }

    private static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (samples.Length == 0 || fromRate == toRate)
        {
            return samples;
        }

        var outLength = (int)((long)samples.Length * toRate / fromRate);
        var result = new float[outLength];

        for (var i = 0; i < outLength; i++)
        {
            var srcPos = (double)i * fromRate / toRate;
            var srcIndex = (int)srcPos;
            var frac = (float)(srcPos - srcIndex);

            var a = samples[Math.Min(srcIndex, samples.Length - 1)];
            var b = samples[Math.Min(srcIndex + 1, samples.Length - 1)];
            result[i] = a + (b - a) * frac;
        }

        return result;
    }

    private static bool TryReadHeader(byte[] wav, out int sampleRate, out short channels, out short bitsPerSample, out int dataOffset, out int dataSize, out short audioFormat)
    {
        sampleRate = 0;
        channels = 0;
        bitsPerSample = 0;
        dataOffset = 0;
        dataSize = 0;
        audioFormat = 0;

        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            return false;
        }

        var pos = 12; // skip RIFF header + "WAVE"
        while (pos + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var chunkSize = BitConverter.ToInt32(wav, pos + 4);
            var chunkDataStart = pos + 8;

            if (chunkId == "fmt " && chunkDataStart + 16 <= wav.Length)
            {
                audioFormat = BitConverter.ToInt16(wav, chunkDataStart);
                channels = BitConverter.ToInt16(wav, chunkDataStart + 2);
                sampleRate = BitConverter.ToInt32(wav, chunkDataStart + 4);
                bitsPerSample = BitConverter.ToInt16(wav, chunkDataStart + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataStart;
                dataSize = chunkSize;
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2);
        }

        return sampleRate > 0 && channels > 0 && bitsPerSample > 0 && dataSize > 0;
    }
}
