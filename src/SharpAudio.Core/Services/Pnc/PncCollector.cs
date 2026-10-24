// wiki: pnc#engine
using System.Text;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Turns per-subword predictions back into text, split into sentences. Pure logic so it can be
/// tested with fake predictions.</summary>
public static class PncCollector
{
    public static IReadOnlyList<string> Collect(
        IReadOnlyList<PncWindow> windows,
        IReadOnlyList<IReadOnlyList<PncTokenPrediction>> predictions,
        IReadOnlyList<string> pieces,
        PncModelConfig config,
        char wordStartMarker,
        bool applySbd = true)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];

            // Only the middle half of an overlapped window contributes, so overlapping windows do not
            // duplicate text.
            var start = 0;
            var stop = window.Length;
            if (i > 0)
            {
                start += config.Overlap / 2;
            }

            if (i < windows.Count - 1)
            {
                stop -= config.Overlap / 2;
            }

            for (var j = start; j < stop; j++)
            {
                var piece = pieces[window.Start + j];
                var prediction = predictions[i][j];

                var isWordStart = piece.Length > 0 && piece[0] == wordStartMarker;
                if (isWordStart && current.Length > 0)
                {
                    current.Append(' ');
                }

                var charStart = isWordStart ? 1 : 0;
                var pre = config.PreLabel(prediction.PreLabelId);
                var post = config.PostLabel(prediction.PostLabelId);

                for (var c = charStart; c < piece.Length; c++)
                {
                    // Pre-labels go before the first character of the piece.
                    if (c == charStart && pre != null)
                    {
                        current.Append(pre);
                    }

                    // Cap rows are indexed by the absolute character index inside the piece string, so the
                    // word-start marker keeps its own slot.
                    var ch = c < prediction.CapPerChar.Count && prediction.CapPerChar[c]
                        ? char.ToUpperInvariant(piece[c])
                        : piece[c];
                    current.Append(ch);

                    if (post == config.AcronymLabel)
                    {
                        current.Append('.');
                    }
                    else if (c == piece.Length - 1 && post != null)
                    {
                        current.Append(post);
                    }

                    // SBD fires at the last character of the subword, after post punctuation. This is the
                    // verified rule for this model; the "shifted right by one" rule belongs to NeMo.cpp's
                    // BERT model.
                    if (applySbd && prediction.Seg && c == piece.Length - 1)
                    {
                        sentences.Add(current.ToString());
                        current.Clear();
                    }
                }
            }
        }

        if (current.Length > 0)
        {
            sentences.Add(current.ToString());
        }

        return sentences;
    }
}
