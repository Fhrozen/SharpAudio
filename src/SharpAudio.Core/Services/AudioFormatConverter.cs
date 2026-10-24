// wiki: asr-engines#universal-audio-input-format-support
using System.Buffers;
using System.Diagnostics;

namespace SharpAudio.Core.Services;

/// <summary>
/// Normalizes arbitrary uploaded audio (FLAC, MP3, OGG, WEBM, M4A, WAV, ...) to PCM16 mono WAV by
/// shelling out to `ffmpeg`, mirroring the ProcessStartInfo pattern already used by
/// <see cref="WorkerProcessManager"/>. Both ASR engines only ever need to understand one input
/// format this way - evaluated against NAudio/pure-managed alternatives first: NAudio's real
/// codecs are Windows-only (ACM/Media Foundation) and won't run in this Linux container, and
/// managed decoders like NLayer only cover MP3, not FLAC/OGG/WEBM/M4A.
/// </summary>
public static class AudioFormatConverter
{
    private const int InitialBufferSize = 64 * 1024;

    /// <summary>Decodes any ffmpeg-supported input format and re-encodes it as mono 16-bit PCM WAV.</summary>
    public static async Task<byte[]> ToPcm16WavAsync(byte[] inputBytes, CancellationToken cancellationToken)
    {
        // Already-normalised uploads are returned unchanged. Process startup costs more than the
        // decode itself for a short clip, and the frontend records in exactly this format.
        if (WavAudioUtils.IsPcm16Mono16kWav(inputBytes))
        {
            return inputBytes;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-vn", "-ac", "1", "-acodec", "pcm_s16le", "-f", "wav", "pipe:1" },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg for audio format conversion.");
        }

        // Start draining stdout/stderr before writing stdin, so a large input can't deadlock
        // against ffmpeg blocking on a full, unread output pipe.
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(inputBytes, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            process.StandardInput.Close();
        }

        var output = await stdoutTask;
        var stderr = await stderrTask;
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException(
                $"Audio format conversion failed (ffmpeg exit code {process.ExitCode}): {stderr}");
        }

        FixWavHeaderSizes(output);
        return output;
    }

    /// <summary>ffmpeg can't seek on a stdout pipe, so it writes placeholder `0xFFFFFFFF` RIFF/data
    /// chunk sizes instead of the real ones - patch them now that the true length is known,
    /// otherwise WavAudioUtils' strict parser rejects the file as having no data.</summary>
    private static void FixWavHeaderSizes(byte[] wav)
    {
        if (wav.Length < 12)
        {
            return;
        }

        BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var chunkDataStart = pos + 8;

            if (chunkId == "data")
            {
                BitConverter.GetBytes(wav.Length - chunkDataStart).CopyTo(wav, pos + 4);
                return;
            }

            var declaredSize = BitConverter.ToUInt32(wav, pos + 4);
            if (declaredSize == uint.MaxValue || declaredSize == 0)
            {
                return; // can't safely walk past another chunk with an unknown/zero size
            }

            pos = chunkDataStart + (int)declaredSize + (int)(declaredSize % 2);
        }
    }

    /// <summary>Reads a stream into a pooled buffer that grows by doubling, so a large upload costs
    /// one final array instead of a MemoryStream buffer plus its <see cref="MemoryStream.ToArray"/> copy.</summary>
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var length = 0;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
                if (length == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }
            }

            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
