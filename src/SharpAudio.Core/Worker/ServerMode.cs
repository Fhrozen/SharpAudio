// wiki: worker-core#server-mode-values
namespace SharpAudio.Core.Worker;

/// <summary>Which task families the API process enables, parsed from <c>SERVER_MODE</c>. Each enabled
/// task gets its own worker pool, port range and executable, so the mode decides which ones are
/// registered at all.</summary>
public sealed record ServerMode(bool Tts, bool Asr, bool Music, bool Diar)
{
    /// <summary>Parse a mode name. Null or empty keeps the historical default of TTS only; an
    /// unrecognised name enables nothing, which is how a typo surfaces rather than silently running
    /// the wrong task.</summary>
    public static ServerMode Parse(string? value)
    {
        var name = (value ?? string.Empty).Trim().ToLowerInvariant();
        return name switch
        {
            "" or "tts" => new ServerMode(Tts: true, Asr: false, Music: false, Diar: false),
            "asr" => new ServerMode(Tts: false, Asr: true, Music: false, Diar: false),
            "music" => new ServerMode(Tts: false, Asr: false, Music: true, Diar: false),
            "diar" => new ServerMode(Tts: false, Asr: false, Music: false, Diar: true),
            "both" => new ServerMode(Tts: true, Asr: true, Music: false, Diar: false),
            "all" => new ServerMode(Tts: true, Asr: true, Music: true, Diar: true),
            _ => new ServerMode(Tts: false, Asr: false, Music: false, Diar: false),
        };
    }
}
