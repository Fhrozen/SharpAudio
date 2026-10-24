using SharpAudio.Api.Options;
using SharpAudio.Api.Services;

namespace SharpAudio.Api.Tests;

public sealed class VadEndpointerTests
{
    private static VadOptions Options(bool enable = true, double stopHistoryEouMs = 800) => new()
    {
        EnableEndpointing = enable,
        StopHistoryEouMs = stopHistoryEouMs,
    };

    [Fact]
    public void Poll_fires_once_when_trailing_silence_first_reaches_the_threshold()
    {
        var pointer = new VadEndpointer(Options());

        Assert.True(pointer.Poll(nowMs: 1000, lastSpeechMs: 200)); // exactly at the threshold
        Assert.False(pointer.Poll(nowMs: 1500, lastSpeechMs: 200)); // latched until speech resumes
    }

    [Fact]
    public void Poll_does_not_fire_before_the_threshold_or_before_any_speech()
    {
        var pointer = new VadEndpointer(Options());

        Assert.False(pointer.Poll(nowMs: 799, lastSpeechMs: 200));
        Assert.False(new VadEndpointer(Options()).Poll(nowMs: 5000, lastSpeechMs: 0));
    }

    [Fact]
    public void Poll_rearms_only_after_speech_advances()
    {
        var pointer = new VadEndpointer(Options());

        Assert.True(pointer.Poll(nowMs: 1000, lastSpeechMs: 200));
        Assert.False(pointer.Poll(nowMs: 1800, lastSpeechMs: 200));

        Assert.True(pointer.Poll(nowMs: 1900, lastSpeechMs: 1100)); // speech resumed, silence runs again
        Assert.False(pointer.Poll(nowMs: 2600, lastSpeechMs: 1100));
    }

    [Fact]
    public void Poll_never_fires_when_endpointing_is_disabled()
    {
        Assert.False(new VadEndpointer(Options(enable: false)).Poll(nowMs: 5000, lastSpeechMs: 100));
    }

    [Fact]
    public void Force_fires_immediately_even_when_endpointing_is_disabled()
    {
        var pointer = new VadEndpointer(Options(enable: false));

        Assert.True(pointer.Force());
        Assert.False(pointer.Poll(nowMs: 5000, lastSpeechMs: 100)); // disabled: the threshold never fires
    }

    [Fact]
    public void Force_disarms_so_the_next_speech_run_can_fire_again()
    {
        var pointer = new VadEndpointer(Options());

        Assert.True(pointer.Poll(nowMs: 1000, lastSpeechMs: 200));
        Assert.True(pointer.Force());
        Assert.False(pointer.Poll(nowMs: 1900, lastSpeechMs: 200)); // disarmed by the force
        Assert.True(pointer.Poll(nowMs: 1100, lastSpeechMs: 300));
    }

    [Fact]
    public void Per_request_override_replaces_the_threshold_until_cleared()
    {
        var pointer = new VadEndpointer(Options());

        pointer.SetStopHistoryEouMs(150);
        Assert.Equal(150, pointer.StopHistoryEouMs);
        Assert.False(pointer.Poll(nowMs: 300, lastSpeechMs: 200)); // below the override
        Assert.True(pointer.Poll(nowMs: 350, lastSpeechMs: 200));

        pointer.SetStopHistoryEouMs(0); // clears the override
        Assert.Equal(800, pointer.StopHistoryEouMs);
    }

    [Fact]
    public void Reset_clears_latched_state_and_keeps_the_per_request_override()
    {
        var pointer = new VadEndpointer(Options());
        pointer.SetStopHistoryEouMs(100);
        Assert.True(pointer.Poll(nowMs: 200, lastSpeechMs: 100));

        pointer.Reset();
        Assert.Equal(100, pointer.StopHistoryEouMs);
        Assert.False(pointer.Poll(nowMs: 200, lastSpeechMs: 0)); // reset disarms; speech must advance first
        Assert.True(pointer.Poll(nowMs: 110, lastSpeechMs: 10));
    }
}
