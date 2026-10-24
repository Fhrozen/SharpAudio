// wiki: worker-core#worker-health-probe
using Grpc.Net.Client;
using SharpAudio.Core.Health;

namespace SharpAudio.Core.Worker;

/// <summary>
/// Liveness check for a running worker over the existing <c>HealthCheck</c> RPC, so a worker that is
/// running but unable to serve is detected instead of being trusted because the OS process exists.
/// The call is supplied per task because each task service declares its own <c>HealthCheck</c> method;
/// <c>Process.HasExited</c> stays as the fallback when the probe itself cannot be reached.
/// </summary>
public sealed class WorkerHealthProbe
{
    private readonly WorkerChannelCache _channels;
    private readonly ILogger<WorkerHealthProbe> _logger;
    private readonly TimeSpan _timeout;
    private readonly Func<GrpcChannel, HealthCheckRequest, CancellationToken, Task<HealthCheckResponse>> _call;

    public WorkerHealthProbe(
        WorkerChannelCache channels,
        ILogger<WorkerHealthProbe> logger,
        WorkerTaskOptions options,
        Func<GrpcChannel, HealthCheckRequest, CancellationToken, Task<HealthCheckResponse>> call)
    {
        _channels = channels;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(options.HealthProbeTimeoutSeconds);
        _call = call;
    }

    public async Task<HealthCheckResponse?> ProbeAsync(string key, int port, CancellationToken cancellationToken)
    {
        var channel = _channels.GetOrCreateChannel(key, port);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            return await _call(channel, new HealthCheckRequest(), timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health probe failed for {Key} on port {Port}", key, port);
            return null;
        }
    }
}
