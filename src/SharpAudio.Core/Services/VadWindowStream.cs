// wiki: asr-engines#vad-support
namespace SharpAudio.Core.Services;

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

    /// <summary>Forms the next complete window from the carried remainder plus as much of
    /// <paramref name="samples"/> as it needs. <paramref name="consumed"/> reports how many samples
    /// were taken, so the caller must advance past them before asking for the next window: passing
    /// the same span again would replay the same audio. Returns false when only a partial window is
    /// left, which stays carried to the next call. The span aliases this stream's own buffer, so the
    /// caller must consume it before asking for the next window — that is what keeps windowing
    /// allocation-free.</summary>
    public bool TryNextWindow(ReadOnlySpan<float> samples, out ReadOnlySpan<float> window, out int consumed)
    {
        window = default;

        if (_pendingCount + samples.Length < FrameSamples)
        {
            samples.CopyTo(_pending.AsSpan(_pendingCount));
            consumed = samples.Length;
            _pendingCount += consumed;
            return false;
        }

        var needed = FrameSamples - _pendingCount;
        samples.Slice(0, needed).CopyTo(_pending.AsSpan(_pendingCount));
        consumed = needed;
        window = _pending;
        _pendingCount = 0;
        return true;
    }

    /// <summary>Complete windows formed from the carried remainder plus <paramref name="samples"/>.
    /// Materialises each window, so callers that can collect them pay an allocation per window;
    /// <see cref="TryNextWindow"/> is the allocation-free form.</summary>
    public IEnumerable<float[]> CompleteWindows(float[] samples)
    {
        for (var offset = 0; offset < samples.Length;)
        {
            if (!TryNextWindow(samples.AsSpan(offset), out var window, out var consumed))
            {
                break;
            }

            offset += consumed;
            yield return window.ToArray();
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
