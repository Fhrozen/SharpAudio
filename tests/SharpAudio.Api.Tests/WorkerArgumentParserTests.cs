// wiki: testing#unit-tests
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>The argument contract between the API and every task worker executable. The names and the
/// defaults are part of the byte-stable <c>READY:{port}</c> handshake, so parsing must never invent a
/// port or an idle timeout when a flag is missing.</summary>
public sealed class WorkerArgumentParserTests
{
    [Fact]
    public void No_arguments_falls_back_to_the_task_defaults()
    {
        var arguments = WorkerArguments.Parse([], defaultPort: 50251, defaultIdleSeconds: 60);

        Assert.Equal(50251, arguments.Port);
        Assert.Equal(string.Empty, arguments.ModelKey);
        Assert.Equal(TimeSpan.FromSeconds(60), arguments.IdleTimeout);
    }

    [Fact]
    public void The_full_command_line_the_manager_builds_is_parsed_back()
    {
        // Exactly the shape WorkerProcessManager produces, including the quoted model key.
        var arguments = WorkerArguments.Parse(
            ["--port", "50351", "--model-key", "sortformer:4spk-v2", "--idle-timeout", "45"],
            defaultPort: 50251,
            defaultIdleSeconds: 60);

        Assert.Equal(50351, arguments.Port);
        Assert.Equal("sortformer:4spk-v2", arguments.ModelKey);
        Assert.Equal(TimeSpan.FromSeconds(45), arguments.IdleTimeout);
    }

    [Fact]
    public void Flags_may_appear_in_any_order_and_any_subset()
    {
        var arguments = WorkerArguments.Parse(["--idle-timeout", "30", "--port", "50051"], defaultPort: 50251, defaultIdleSeconds: 60);

        Assert.Equal(50051, arguments.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), arguments.IdleTimeout);
        Assert.Equal(string.Empty, arguments.ModelKey);
    }

    [Fact]
    public void A_flag_without_a_value_keeps_the_default()
    {
        var arguments = WorkerArguments.Parse(["--port"], defaultPort: 50251, defaultIdleSeconds: 60);

        Assert.Equal(50251, arguments.Port);
    }

    [Fact]
    public void Unknown_arguments_are_ignored()
    {
        var arguments = WorkerArguments.Parse(
            ["--verbose", "--port", "50151", "--unknown", "x"],
            defaultPort: 50251,
            defaultIdleSeconds: 60);

        Assert.Equal(50151, arguments.Port);
    }
}
