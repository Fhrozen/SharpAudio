// wiki: diarization#worker-scaffold
using Grpc.Core;
using SharpAudio.Core.Health;
using SharpAudio.Core.Services;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Diar.Grpc;

namespace SharpAudio.Worker.Diar.Services;

/// <summary>
/// gRPC service that performs speaker diarization in the diarization worker process. The Sortformer
/// engine is a later phase, so only the health check is real here: the API needs a live worker pool
/// to route to, and diarization requests answer Unimplemented until the engine exists.
/// </summary>
public sealed class WorkerDiarService : WorkerDiarization.WorkerDiarizationBase, IDisposable
{
    private readonly WorkerServiceBase _base;

    public WorkerDiarService(IdleMonitor idleMonitor)
    {
        _base = new WorkerServiceBase(idleMonitor);
    }

    public override Task<DiarizeResponse> Diarize(DiarizeRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            $"Diarization is not implemented for engine '{request.Engine}'."));
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(_base.BuildHealthCheck());
    }

    public void Dispose()
    {
        _base.Dispose();
    }
}
