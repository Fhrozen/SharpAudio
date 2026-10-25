// wiki: testing#unit-tests
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>One channel and one client per worker, keyed by model key: the channel is rebuilt only
/// when a worker is respawned on a different port, clients are reused across requests, and the
/// retry covers exactly the one status that means "the worker just restarted".</summary>
public sealed class WorkerChannelCacheTests
{
    private sealed class FakeClient
    {
        public GrpcChannel Channel { get; init; } = null!;
    }

    private static WorkerChannelCache NewCache() =>
        new(NullLogger<WorkerChannelCache>.Instance, maxReceiveBytes: 100 * 1024 * 1024, maxSendBytes: 10 * 1024 * 1024);

    [Fact]
    public void The_same_key_and_port_reuse_one_channel()
    {
        using var cache = NewCache();

        var first = cache.GetOrCreateChannel("whisper:large", 50151);
        var second = cache.GetOrCreateChannel("whisper:large", 50151);

        Assert.Same(first, second);
    }

    [Fact]
    public void A_different_key_gets_its_own_channel()
    {
        using var cache = NewCache();

        var tts = cache.GetOrCreateChannel("kokoro:en", 50051);
        var asr = cache.GetOrCreateChannel("whisper:large", 50151);

        Assert.NotSame(tts, asr);
    }

    [Fact]
    public void A_respawn_on_a_new_port_rebuilds_the_channel_for_that_key()
    {
        using var cache = NewCache();

        var before = cache.GetOrCreateChannel("whisper:large", 50151);
        var after = cache.GetOrCreateChannel("whisper:large", 50152);

        Assert.NotSame(before, after);
        // The rebuilt entry is what later calls see, so a respawn does not leave a stale channel behind.
        Assert.Same(after, cache.GetOrCreateChannel("whisper:large", 50152));
    }

    [Fact]
    public void Clients_are_built_once_per_worker_and_reused()
    {
        using var cache = NewCache();
        var builds = 0;

        var first = cache.GetOrCreateClient<FakeClient>("whisper:large", 50151, channel =>
        {
            builds++;
            return new FakeClient { Channel = channel };
        });
        var second = cache.GetOrCreateClient<FakeClient>("whisper:large", 50151, channel =>
        {
            builds++;
            return new FakeClient { Channel = channel };
        });

        Assert.Same(first, second);
        Assert.Equal(1, builds);

        // A respawn drops the client too, since it is bound to the old channel.
        var afterRespawn = cache.GetOrCreateClient<FakeClient>("whisper:large", 50152, channel => new FakeClient { Channel = channel });
        Assert.NotSame(first, afterRespawn);
        Assert.Same(cache.GetOrCreateChannel("whisper:large", 50152), afterRespawn.Channel);
    }

    [Fact]
    public async Task An_unavailable_worker_is_retried_once_and_the_second_call_wins()
    {
        using var cache = NewCache();
        var attempts = 0;

        var result = await cache.InvokeWithRetryAsync<string>(
            "whisper:large",
            50151,
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, "worker restarted"));
                }

                return Task.FromResult("ok");
            },
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Other_status_codes_are_not_retried()
    {
        using var cache = NewCache();
        var attempts = 0;

        await Assert.ThrowsAsync<RpcException>(async () => await cache.InvokeWithRetryAsync<string>(
            "whisper:large",
            50151,
            _ =>
            {
                attempts++;
                throw new RpcException(new Status(StatusCode.InvalidArgument, "bad request"));
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancelling_during_the_retry_delay_stops_the_retry()
    {
        using var cache = NewCache();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var attempts = 0;

        // Task.Delay throws TaskCanceledException when the token is already cancelled, and
        // Assert.ThrowsAsync is exact-type, so the derived type is what has to be asserted.
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await cache.InvokeWithRetryAsync<string>(
            "whisper:large",
            50151,
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, "worker restarted"));
                }

                throw new OperationCanceledException(cts.Token);
            },
            cts.Token));

        // The retry delay is cancellable, so a caller that goes away mid-retry is not followed by a
        // second call into a worker that may already have been reaped.
        Assert.Equal(1, attempts);
    }
}
