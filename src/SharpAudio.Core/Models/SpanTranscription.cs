// wiki: asr-engines#vad-support
namespace SharpAudio.Api.Models;

/// <summary>Transcription of one VAD speech span, with timestamps relative to the start of that span.</summary>
public sealed record SpanTranscription(string Text, string? DetectedLanguage, IReadOnlyList<TranscriptionSegment> Segments);
