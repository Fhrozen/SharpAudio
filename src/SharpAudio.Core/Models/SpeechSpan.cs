// wiki: asr-engines#vad-support
namespace SharpAudio.Core.Models;

/// <summary>A span of speech in seconds from the start of the audio.</summary>
public sealed record SpeechSpan(double Start, double End);
