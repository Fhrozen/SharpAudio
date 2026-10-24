// wiki: asr-engines#worker-process-di-wiring-and-server-mode
using SharpAudio.Core.Worker;

namespace SharpAudio.Core.Options;

/// <summary>
/// Configuration options for the ASR worker process. Shares <see cref="WorkerTaskOptions"/> with the
/// TTS worker and only overrides the values that genuinely differ: the executable, the port range
/// (so ASR and TTS workers can run side by side), the startup timeout, and the message-size limits,
/// which are send-heavy for ASR because requests carry audio.
/// </summary>
public sealed class AsrWorkerOptions : WorkerTaskOptions
{
    public const string SectionName = "AsrWorkerOptions";

    public AsrWorkerOptions()
    {
        ExecutablePath = "./worker-asr/SharpAudio.Worker.Asr";
        PortRangeStart = 50151;
        StartupTimeoutSeconds = 45;
        MaxReceiveMessageBytes = 10 * 1024 * 1024;
        MaxSendMessageBytes = 100 * 1024 * 1024;
    }
}
