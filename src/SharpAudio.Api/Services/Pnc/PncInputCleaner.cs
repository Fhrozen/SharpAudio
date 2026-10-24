using System.Text;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Input pre-cleaning performed by the engine: the reference pipeline does none, so the engine
/// owns lowercasing, punctuation stripping and whitespace normalisation.</summary>
public static class PncInputCleaner
{
    public static string Clean(string text)
    {
        var lower = text.ToLowerInvariant();
        var cleaned = new StringBuilder(lower.Length);

        var inWhitespace = false;
        foreach (var ch in lower)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace && cleaned.Length > 0)
                {
                    cleaned.Append(' ');
                }

                inWhitespace = true;
                continue;
            }

            if (char.IsPunctuation(ch))
            {
                continue;
            }

            cleaned.Append(ch);
            inWhitespace = false;
        }

        return cleaned.ToString().Trim();
    }
}
