// wiki: diarization#worker-process-and-di-wiring
using SharpAudio.Core.Worker;

namespace SharpAudio.Core.Options;

/// <summary>Worker process settings for the diarization task. Requests carry audio, so the send limit
/// is the large one and the response is a list of segments.</summary>
public sealed class DiarWorkerOptions : WorkerTaskOptions
{
    public const string SectionName = "DiarWorkerOptions";

    public DiarWorkerOptions()
    {
        ExecutablePath = "./worker-diar/SharpAudio.Worker.Diar";
        PortRangeStart = 50351;
        StartupTimeoutSeconds = 45;
        MaxReceiveMessageBytes = 10 * 1024 * 1024;
        MaxSendMessageBytes = 100 * 1024 * 1024;
    }

    /// <summary>How long an audio may be before a diarization request is rejected. Sortformer is run
    /// as a single forward pass, so cost grows with length.</summary>
    public double MaxDurationSeconds { get; set; } = 600;
}
