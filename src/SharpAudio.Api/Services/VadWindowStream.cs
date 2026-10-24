// wiki: asr-engines#vad-support
namespace SharpAudio.Api.Services;

/// <summary>Splits audio into fixed-size VAD windows, carrying a partial window across calls so no
/// samples are dropped between chunks. Pure logic - no ONNX, so it is unit-testable without weights.</summary>
public sealed class VadWindowStream
{
    private readonly float[] _pending;
    private readonly int _sampleRate;
    private int _pendingCount;

    public VadWindowStream(int frameSamples, int sampleRate)
    {
        if (frameSamples <= 0)
        {
            throw new ArgumentException("frameSamples must be positive.", nameof(frameSamples));
        }

        _pending = new float[frameSamples];
        _sampleRate = sampleRate;
    }

    public int FrameSamples => _pending.Length;

    public double FrameDurationMs => 1000.0 * FrameSamples / _sampleRate;

    public void Reset()
    {
        _pendingCount = 0;
    }

    /// <summary>Complete windows formed from the carried remainder plus <paramref name="samples"/>.</summary>
    public IEnumerable<float[]> CompleteWindows(float[] samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            var take = Math.Min(FrameSamples - _pendingCount, samples.Length - offset);
            Array.Copy(samples, offset, _pending, _pendingCount, take);
            _pendingCount += take;
            offset += take;

            if (_pendingCount < FrameSamples)
            {
                yield break;
            }

            var window = new float[FrameSamples];
            Array.Copy(_pending, window, FrameSamples);
            _pendingCount = 0;
            yield return window;
        }
    }

    /// <summary>Zero-padded window for the carried remainder, or null when nothing is pending.</summary>
    public float[]? FlushPaddedWindow()
    {
        if (_pendingCount == 0)
        {
            return null;
        }

        var window = new float[FrameSamples];
        Array.Copy(_pending, 0, window, 0, _pendingCount);
        _pendingCount = 0;
        return window;
    }
}
