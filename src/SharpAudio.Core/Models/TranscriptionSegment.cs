// wiki: asr-engines#vad-and-timestamp-segments
namespace SharpAudio.Core.Models;

/// <summary>One transcribed segment with real-audio timestamps (seconds from the start of the clip).</summary>
public sealed record TranscriptionSegment(int Id, double Start, double End, string Text);
