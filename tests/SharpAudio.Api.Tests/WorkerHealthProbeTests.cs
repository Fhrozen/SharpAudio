// wiki: testing#unit-tests
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAudio.Core.Health;
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>Liveness over the existing HealthCheck RPC: a worker that is running but cannot serve must
/// be reported as unhealthy rather than trusted because the OS process exists, and a probe that cannot
/// be reached must not stall the request path.</summary>
public sealed class WorkerHealthProbeTests
{
    private static WorkerTaskOptions NewOptions(int timeoutSeconds) =>
        new() { HealthProbeTimeoutSeconds = timeoutSeconds };

    [Fact]
    public async Task A_successful_probe_returns_the_response()
    {
        using var cache = new WorkerChannelCache(NullLogger<WorkerChannelCache>.Instance, 10 * 1024 * 1024, 10 * 1024 * 1024);
        var probe = new WorkerHealthProbe(cache, NullLogger<WorkerHealthProbe>.Instance, NewOptions(2),
            (channel, request, ct) => Task.FromResult(new HealthCheckResponse
            {
                IsReady = true,
                ModelLoaded = "whisper:large",
                IdleSeconds = 3.5,
            }));

        var response = await probe.ProbeAsync("whisper:large", 50151, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.IsReady);
        Assert.Equal("whisper:large", response.ModelLoaded);
    }

    [Fact]
    public async Task The_probe_reuses_the_cached_channel_for_the_worker()
    {
        using var cache = new WorkerChannelCache(NullLogger<WorkerChannelCache>.Instance, 10 * 1024 * 1024, 10 * 1024 * 1024);
        GrpcChannel? seen = null;

        var probe = new WorkerHealthProbe(cache, NullLogger<WorkerHealthProbe>.Instance, NewOptions(2),
            (channel, request, ct) =>
            {
                seen = channel;
                return Task.FromResult(new HealthCheckResponse { IsReady = true });
            });

        await probe.ProbeAsync("whisper:large", 50151, CancellationToken.None);

        Assert.Same(cache.GetOrCreateChannel("whisper:large", 50151), seen);
    }

    [Fact]
    public async Task A_failed_probe_returns_null_instead_of_throwing()
    {
        using var cache = new WorkerChannelCache(NullLogger<WorkerChannelCache>.Instance, 10 * 1024 * 1024, 10 * 1024 * 1024);
        var probe = new WorkerHealthProbe(cache, NullLogger<WorkerHealthProbe>.Instance, NewOptions(2),
            (_, _, _) => throw new InvalidOperationException("worker unreachable"));

        Assert.Null(await probe.ProbeAsync("whisper:large", 50151, CancellationToken.None));
    }

    [Fact]
    public async Task A_probe_that_hangs_is_cut_off_by_the_timeout()
    {
        using var cache = new WorkerChannelCache(NullLogger<WorkerChannelCache>.Instance, 10 * 1024 * 1024, 10 * 1024 * 1024);
        var sawCancellation = false;

        var probe = new WorkerHealthProbe(cache, NullLogger<WorkerHealthProbe>.Instance, NewOptions(1),
            async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    sawCancellation = true;
                    throw;
                }

                return new HealthCheckResponse { IsReady = true };
            });

        var response = await probe.ProbeAsync("whisper:large", 50151, CancellationToken.None);

        Assert.Null(response);
        Assert.True(sawCancellation, "the probe did not pass a cancellable token to the RPC");
    }

    [Fact]
    public async Task A_probe_that_is_not_ready_is_reported_as_not_ready()
    {
        using var cache = new WorkerChannelCache(NullLogger<WorkerChannelCache>.Instance, 10 * 1024 * 1024, 10 * 1024 * 1024);
        var probe = new WorkerHealthProbe(cache, NullLogger<WorkerHealthProbe>.Instance, NewOptions(2),
            (_, _, _) => Task.FromResult(new HealthCheckResponse { IsReady = false, ModelLoaded = "none" }));

        var response = await probe.ProbeAsync("whisper:large", 50151, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.IsReady);
    }
}
