// wiki: backend-architecture#config-and-env-vars
using SharpAudio.Core.Worker;

namespace SharpAudio.Core.Options;

/// <summary>
/// Configuration options for the TTS worker process. Every property comes from
/// <see cref="WorkerTaskOptions"/>; this class only carries the config section name and the TTS
/// defaults (the executable, port 50051, and the receive-heavy message-size limits).
/// </summary>
public sealed class WorkerOptions : WorkerTaskOptions
{
    public const string SectionName = "WorkerOptions";
}
