// wiki: music#worker-process-and-di-wiring
using SharpAudio.Core.Worker;

namespace SharpAudio.Core.Options;

/// <summary>Worker process settings for the music task. The response carries generated audio, so the
/// send limit is the large one and the request stays small.</summary>
public sealed class MusicWorkerOptions : WorkerTaskOptions
{
    public const string SectionName = "MusicWorkerOptions";

    public MusicWorkerOptions()
    {
        ExecutablePath = "./worker-music/SharpAudio.Worker.Music";
        PortRangeStart = 50251;
        StartupTimeoutSeconds = 60;
        MaxReceiveMessageBytes = 100 * 1024 * 1024;
        MaxSendMessageBytes = 10 * 1024 * 1024;
    }

    /// <summary>Default length of a generation when the request does not ask for one.</summary>
    public int DefaultDurationSeconds { get; set; } = 30;

    /// <summary>Hard cap on requestable length, independent of the model's own limit.</summary>
    public int MaxDurationSeconds { get; set; } = 384;
}
