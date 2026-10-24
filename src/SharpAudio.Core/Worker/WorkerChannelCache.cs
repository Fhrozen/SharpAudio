// wiki: worker-core#worker-channel-cache
using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;

namespace SharpAudio.Core.Worker;

/// <summary>
/// One gRPC channel (and the clients built on it) per worker, keyed by model key. The channel is
/// rebuilt only when a worker is respawned on a different port. Message-size limits are per
/// direction: TTS returns large audio payloads, ASR receives large audio payloads.
/// </summary>
public sealed class WorkerChannelCache : IDisposable
{
    private sealed record Entry(GrpcChannel Channel, int Port, Dictionary<Type, object> Clients);

    private readonly ConcurrentDictionary<string, Entry> _channels = new();
    private readonly ILogger<WorkerChannelCache> _logger;
    private readonly GrpcChannelOptions _channelOptions;
    private bool _disposed;

    public WorkerChannelCache(ILogger<WorkerChannelCache> logger, int maxReceiveBytes, int maxSendBytes)
    {
        _logger = logger;
        _channelOptions = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = maxReceiveBytes,
            MaxSendMessageSize = maxSendBytes
        };
    }

    public GrpcChannel GetOrCreateChannel(string key, int port) => GetEntry(key, port).Channel;

    private Entry GetEntry(string key, int port)
    {
        if (_channels.TryGetValue(key, out var cached))
        {
            if (cached.Port == port)
            {
                return cached;
            }

            _logger.LogInformation("Worker port changed for {Key}: {OldPort} -> {NewPort}, recreating channel",
                key, cached.Port, port);

            DisposeChannel(cached.Channel);
            _channels.TryRemove(key, out _);
        }

        var address = $"http://localhost:{port}";
        _logger.LogInformation("Creating gRPC channel for {Key}: {Address}", key, address);

        var entry = new Entry(GrpcChannel.ForAddress(address, _channelOptions), port, new Dictionary<Type, object>());
        _channels[key] = entry;
        return entry;
    }

    /// <summary>
    /// Build a client once per worker and reuse it, so each request does not allocate a new one.
    /// </summary>
    public TClient GetOrCreateClient<TClient>(string key, int port, Func<GrpcChannel, TClient> createClient)
        where TClient : class
    {
        var entry = GetEntry(key, port);

        if (entry.Clients.TryGetValue(typeof(TClient), out var existing))
        {
            return (TClient)existing;
        }

        var client = createClient(entry.Channel);
        entry.Clients[typeof(TClient)] = client;
        return client;
    }

    /// <summary>
    /// Run a gRPC call, retrying once after a short delay when the worker is unreachable — the
    /// common case is a worker that has just been respawned.
    /// </summary>
    public async Task<TResponse> InvokeWithRetryAsync<TResponse>(
        string key,
        int port,
        Func<CancellationToken, Task<TResponse>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await call(cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("First gRPC call to {Key} on port {Port} failed ({Status}), retrying in 1 second",
                key, port, ex.StatusCode);

            await Task.Delay(1000, cancellationToken);
            return await call(cancellationToken);
        }
    }

    private void DisposeChannel(GrpcChannel channel)
    {
        try
        {
            channel.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing gRPC channel");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var entry in _channels.Values)
        {
            DisposeChannel(entry.Channel);
        }

        _channels.Clear();
        _disposed = true;
    }
}
