// wiki: pnc#wiring
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Applies a PnC engine to ASR output. The engine returns sentences (SBD flushes), so text is
/// rebuilt by joining them; segments are punctuated individually to keep their timestamps intact.</summary>
public static class PncTextApplier
{
    public static string Apply(IPncEngine engine, string text)
    {
        var sentences = engine.Apply(text);
        if (sentences.Count == 0)
        {
            return text;
        }

        return string.Join(" ", sentences);
    }

    public static TranscriptionResult Apply(IPncEngine engine, TranscriptionResult result)
    {
        var text = Apply(engine, result.Text);
        var segments = result.Segments?.Select(segment =>
            new TranscriptionSegment(segment.Id, segment.Start, segment.End, Apply(engine, segment.Text))).ToList();

        return result with { Text = text, CharacterCount = text.Length, Segments = segments };
    }
}
