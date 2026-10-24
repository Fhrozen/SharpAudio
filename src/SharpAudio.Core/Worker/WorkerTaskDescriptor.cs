// wiki: worker-core#worker-task-descriptor
using Grpc.Net.Client;
using SharpAudio.Core.Health;

namespace SharpAudio.Core.Worker;

/// <summary>
/// Describes one worker task (TTS, ASR, and later music or diarization): the keyed DI name it
/// is registered under, the options that drive its process, and the health-check call delegate
/// for its gRPC service. The API loops over the descriptors to register the channel cache, the
/// health probe and the process manager, so a new worker adds a descriptor instead of a copy of
/// the registration block.
/// </summary>
public sealed record WorkerTaskDescriptor(
    string Key,
    WorkerTaskOptions Options,
    Func<GrpcChannel, HealthCheckRequest, CancellationToken, Task<HealthCheckResponse>> HealthCheckCall);
