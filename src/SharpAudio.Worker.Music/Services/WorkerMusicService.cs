// wiki: music#worker-scaffold
using Grpc.Core;
using SharpAudio.Core.Health;
using SharpAudio.Core.Services;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Music.Grpc;

namespace SharpAudio.Worker.Music.Services;

/// <summary>
/// gRPC service that performs music generation in the music worker process. The engine itself is a
/// later phase, so only the health check is real here: the API needs a live worker pool to route to,
/// and generation requests answer Unimplemented until the sampler exists.
/// </summary>
public sealed class WorkerMusicService : WorkerMusic.WorkerMusicBase, IDisposable
{
    private readonly WorkerServiceBase _base;

    public WorkerMusicService(IdleMonitor idleMonitor)
    {
        _base = new WorkerServiceBase(idleMonitor);
    }

    public override Task<GenerateResponse> Generate(GenerateRequest request, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            $"Music generation is not implemented for engine '{request.Engine}'."));
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
