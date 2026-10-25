// wiki: diarization#domain-model-and-abstractions
namespace SharpAudio.Core.Models;

/// <summary>One speaker turn in audio time, before any text is attached to it.</summary>
public sealed record SpeakerSegment(int Id, double Start, double End, int Speaker);
