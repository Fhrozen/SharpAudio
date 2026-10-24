// wiki: worker-core#worker-service-base
using SharpAudio.Core.Health;
using SharpAudio.Core.Services;

namespace SharpAudio.Core.Worker;

/// <summary>
/// Shared state for every worker gRPC service: idle tracking, the single-active engine pool,
/// the HealthCheck response, and disposal.
///
/// gRPC services must inherit their generated base class, so this is used by composition
/// rather than inheritance: each per-task service holds one of these and implements only the
/// RPC dispatch.
/// </summary>
public sealed class WorkerServiceBase : IDisposable
{
    private readonly IdleMonitor _idleMonitor;
    private readonly SingleActiveEnginePool _engines;
    private bool _disposed;

    public WorkerServiceBase(IdleMonitor idleMonitor)
    {
        _idleMonitor = idleMonitor;
        _engines = new SingleActiveEnginePool();
    }

    /// <summary>
    /// The single-active engine pool shared by the service's engines.
    /// </summary>
    public SingleActiveEnginePool Engines => _engines;

    /// <summary>
    /// Reset the idle timer. Called at the top of every RPC.
    /// </summary>
    public void RecordActivity()
    {
        _idleMonitor.RecordActivity();
    }

    /// <summary>
    /// Build the HealthCheck response from the idle monitor and the active model key.
    /// </summary>
    public HealthCheckResponse BuildHealthCheck()
    {
        return new HealthCheckResponse
        {
            IsReady = true,
            ModelLoaded = _engines.LoadedModelKey ?? "none",
            IdleSeconds = _idleMonitor.GetIdleDuration().TotalSeconds
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engines.Dispose();
    }
}
