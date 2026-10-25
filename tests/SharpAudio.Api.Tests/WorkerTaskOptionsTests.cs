// wiki: testing#unit-tests
using SharpAudio.Core.Options;
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>Per-task worker settings: the port ranges must stay disjoint, and each task's message-size
/// limits must favour whichever direction actually carries the bulk of the payload.</summary>
public class WorkerTaskOptionsTests
{
    private static WorkerTaskOptions[] AllTasks() =>
    [
        new WorkerOptions(),
        new AsrWorkerOptions(),
        new MusicWorkerOptions(),
        new DiarWorkerOptions(),
    ];

    [Fact]
    public void Port_ranges_do_not_overlap()
    {
        var tasks = AllTasks();

        for (var i = 0; i < tasks.Length; i++)
        {
            for (var j = i + 1; j < tasks.Length; j++)
            {
                var a = tasks[i];
                var b = tasks[j];
                var aEnd = a.PortRangeStart + a.MaxPortAttempts - 1;
                var bEnd = b.PortRangeStart + b.MaxPortAttempts - 1;

                Assert.False(a.PortRangeStart <= bEnd && b.PortRangeStart <= aEnd,
                    $"{a.GetType().Name} ({a.PortRangeStart}-{aEnd}) overlaps {b.GetType().Name} ({b.PortRangeStart}-{bEnd})");
            }
        }
    }

    [Fact]
    public void Each_task_has_its_own_executable_and_config_section()
    {
        var tasks = AllTasks();
        Assert.Equal(4, tasks.Select(t => t.ExecutablePath).Distinct().Count());
        Assert.Equal(4, tasks.Select(t => t.PortRangeStart).Distinct().Count());
    }

    [Fact]
    public void Music_task_favours_the_response_because_it_carries_generated_audio()
    {
        var options = new MusicWorkerOptions();
        Assert.True(options.MaxReceiveMessageBytes > options.MaxSendMessageBytes);
        Assert.Equal(30, options.DefaultDurationSeconds);
        Assert.Equal(384, options.MaxDurationSeconds);
    }

    [Fact]
    public void Diar_task_favours_the_request_because_it_carries_the_audio()
    {
        var options = new DiarWorkerOptions();
        Assert.True(options.MaxSendMessageBytes > options.MaxReceiveMessageBytes);
        Assert.Equal(600, options.MaxDurationSeconds);
    }
}
