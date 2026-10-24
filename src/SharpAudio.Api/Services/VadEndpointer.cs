using SharpAudio.Api.Options;

namespace SharpAudio.Api.Services;

/// <summary>Trailing-silence endpoint (EOU) policy over a millisecond timeline, mirroring NeMo.cpp's
/// VadEndpointer: fires exactly once when the trailing silence first reaches the threshold and
/// re-arms only after speech advances, so one long pause produces one endpoint.</summary>
public sealed class VadEndpointer
{
    private readonly VadOptions _options;
    private double _lastSpeechMs;
    private bool _armed;
    private double _overrideMs = -1;

    public VadEndpointer(VadOptions options)
    {
        _options = options;
    }

    /// <summary>Advance the timeline. <paramref name="nowMs"/> is the decoded audio time so far and
    /// <paramref name="lastSpeechMs"/> the end of the most recent speech at or before it. Returns
    /// true exactly once when trailing silence first reaches the threshold.</summary>
    public bool Poll(double nowMs, double lastSpeechMs)
    {
        if (lastSpeechMs > _lastSpeechMs)
        {
            _lastSpeechMs = lastSpeechMs;
            _armed = true;
        }

        if (!_options.EnableEndpointing || !_armed)
        {
            return false;
        }

        if (nowMs - _lastSpeechMs >= StopHistoryEouMs)
        {
            _armed = false; // latched until speech resumes
            return true;
        }

        return false;
    }

    /// <summary>Fires immediately, regardless of the threshold and of whether endpointing is enabled.</summary>
    public bool Force()
    {
        _armed = false;
        return true;
    }

    /// <summary>New stream: clear latched state. The per-request override survives.</summary>
    public void Reset()
    {
        _lastSpeechMs = 0;
        _armed = false;
    }

    /// <summary>Per-request override of the EOU silence threshold; &lt;= 0 clears it.</summary>
    public void SetStopHistoryEouMs(double ms)
    {
        _overrideMs = ms;
    }

    /// <summary>Per-request override when set, otherwise the configured threshold.</summary>
    public double StopHistoryEouMs => _overrideMs > 0 ? _overrideMs : _options.StopHistoryEouMs;
}
