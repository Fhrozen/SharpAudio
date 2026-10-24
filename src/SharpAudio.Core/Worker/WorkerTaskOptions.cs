// wiki: worker-core#worker-task-options
namespace SharpAudio.Core.Worker;

/// <summary>
/// Per-task worker process configuration. Every task (TTS, ASR, music, diarization) has its own
/// config section, its own port range and its own executable, but the process manager works against
/// this base type — so adding a task needs no adapter code.
/// </summary>
public class WorkerTaskOptions
{
    /// <summary>Path to the worker executable.</summary>
    public string ExecutablePath { get; set; } = "./SharpAudio.Worker.Tts";

    /// <summary>Idle timeout in seconds before the worker self-terminates.</summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>Starting port for this task's worker gRPC servers. Ranges must not overlap.</summary>
    public int PortRangeStart { get; set; } = 50051;

    /// <summary>Maximum number of ports to try before giving up.</summary>
    public int MaxPortAttempts { get; set; } = 100;

    /// <summary>Timeout in seconds for worker startup.</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;

    /// <summary>Enable worker process mode (if false, uses in-process implementations).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// gRPC message-size limits, per direction. TTS sends short requests and gets whole audio files
    /// back, so receive is the large one; ASR uploads audio and gets text back, so the two are
    /// swapped in <see cref="Options.AsrWorkerOptions"/>.
    /// </summary>
    public int MaxReceiveMessageBytes { get; set; } = 100 * 1024 * 1024;

    public int MaxSendMessageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Timeout in seconds for a single liveness probe over the HealthCheck RPC.</summary>
    public int HealthProbeTimeoutSeconds { get; set; } = 2;

    /// <summary>
    /// Minimum seconds between liveness probes for the same worker. Probing on every request would
    /// add a gRPC round trip to each call, so a healthy result is reused until this interval passes.
    /// </summary>
    public int HealthProbeIntervalSeconds { get; set; } = 15;
}
