// wiki: testing#unit-tests
using Microsoft.Extensions.Logging.Abstractions;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>Worker process lifecycle against a stand-in executable that performs the <c>READY:{port}</c>
/// handshake without loading a model: handshake, reuse, respawn after death, and port release. Skipped
/// when the environment has no shell to run the stand-in.</summary>
public sealed class WorkerProcessManagerTests : IDisposable
{
    private const string SkipReason = "Skipped: /bin/sh is not available, so the stand-in worker executable cannot be run.";

    private static bool ShellAvailable => File.Exists("/bin/sh");

    private readonly string _scriptPath;

    public WorkerProcessManagerTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sharpaudio-fake-worker-{Guid.NewGuid():N}.sh");
        File.WriteAllText(_scriptPath, """
            #!/bin/sh
            port=""
            key=""
            while [ $# -gt 0 ]; do
                if [ "$1" = "--port" ]; then port="$2"; fi
                if [ "$1" = "--model-key" ]; then key="$2"; fi
                shift
            done
            case "$key" in
                silent:*) sleep 5; exit 0 ;;
            esac
            echo "READY:$port"
            sleep 60
            """);
        MakeExecutable(_scriptPath);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_scriptPath);
        }
        catch
        {
            // The temp file is best-effort cleanup.
        }
    }

    private WorkerTaskOptions NewOptions(int portRangeStart, int startupTimeoutSeconds = 10, string? executablePath = null) =>
        new()
        {
            ExecutablePath = executablePath ?? _scriptPath,
            PortRangeStart = portRangeStart,
            MaxPortAttempts = 5,
            StartupTimeoutSeconds = startupTimeoutSeconds,
        };

    private WorkerProcessManager NewManager(WorkerTaskOptions options) =>
        new(options, NullLogger<WorkerProcessManager>.Instance);

    [SkippableFact]
    public async Task Spawn_completes_the_READY_handshake()
    {
        Skip.IfNot(ShellAvailable, SkipReason);

        using var manager = NewManager(NewOptions(51001));
        var worker = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);

        Assert.True(worker.IsAlive);
        Assert.Equal(51001, worker.Port);
        Assert.Equal("whisper:large", worker.ModelKey);
    }

    [SkippableFact]
    public async Task The_same_model_key_reuses_the_running_worker()
    {
        Skip.IfNot(ShellAvailable, SkipReason);

        using var manager = NewManager(NewOptions(51002));

        var first = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);
        var second = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);

        Assert.Same(first, second);
        Assert.True(second.IsAlive);
    }

    [SkippableFact]
    public async Task A_dead_worker_is_respawned_on_the_next_request()
    {
        Skip.IfNot(ShellAvailable, SkipReason);

        using var manager = NewManager(NewOptions(51003));

        var first = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);
        first.Process.Kill(entireProcessTree: true);
        await first.Process.WaitForExitAsync();

        var respawned = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);

        Assert.NotSame(first, respawned);
        Assert.True(respawned.IsAlive);
    }

    [SkippableFact]
    public async Task Each_model_key_gets_its_own_port_and_a_released_port_is_reused()
    {
        Skip.IfNot(ShellAvailable, SkipReason);

        using var manager = NewManager(NewOptions(51010));

        var first = await manager.GetOrSpawnWorkerAsync("whisper:large", CancellationToken.None);
        var second = await manager.GetOrSpawnWorkerAsync("kokoro:en", CancellationToken.None);

        Assert.NotEqual(first.Port, second.Port);

        // Stopping releases the port back to the pool, so the next worker starts at the range start
        // again rather than walking forward through the range.
        await manager.StopAsync(CancellationToken.None);

        var third = await manager.GetOrSpawnWorkerAsync("nemotron:en", CancellationToken.None);
        Assert.Equal(first.Port, third.Port);
    }

    [SkippableFact]
    public async Task A_worker_that_never_signals_READY_times_out_and_releases_its_port()
    {
        Skip.IfNot(ShellAvailable, SkipReason);

        using var manager = NewManager(NewOptions(51020, startupTimeoutSeconds: 1));

        // A "silent:" model key makes the stand-in script stay quiet, so the READY handshake times out.
        await Assert.ThrowsAsync<TimeoutException>(
            () => manager.GetOrSpawnWorkerAsync("silent:whisper", CancellationToken.None));

        // The port is released on failure, so the next worker is allocated at the range start again
        // rather than walking forward to the next attempt.
        var worker = await manager.GetOrSpawnWorkerAsync("kokoro:en", CancellationToken.None);
        Assert.Equal(51020, worker.Port);
    }
}
