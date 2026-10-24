using System.Text;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Reads the small subset of YAML the PnC model configs use: scalar keys and one-line-per-item
/// flow lists. Full YAML is not needed and no YAML dependency is added.</summary>
public static class PncConfigReader
{
    public static PncModelConfig LoadFile(string path) => Parse(File.ReadAllText(path));

    public static PncModelConfig Parse(string yaml)
    {
        var preLabels = new List<string>();
        var postLabels = new List<string>();
        var maxLength = 256;
        var overlap = 16;
        var padId = 0;
        var unkId = 0;

        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0 || !line.Contains(':'))
            {
                continue;
            }

            var key = line[..line.IndexOf(':')].Trim();
            var value = line[(line.IndexOf(':') + 1)..].Trim();

            switch (key)
            {
                case "pre_labels":
                    preLabels.AddRange(ReadList(lines, ref i, value));
                    break;
                case "post_labels":
                    postLabels.AddRange(ReadList(lines, ref i, value));
                    break;
                case "max_length":
                    maxLength = int.Parse(value);
                    break;
                case "overlap":
                    overlap = int.Parse(value);
                    break;
                case "pad_id":
                    padId = int.Parse(value);
                    break;
                case "unk_id":
                    unkId = int.Parse(value);
                    break;
            }
        }

        return new PncModelConfig
        {
            PreLabels = preLabels,
            PostLabels = postLabels,
            MaxLength = maxLength,
            Overlap = overlap,
            PadId = padId,
            UnkId = unkId
        };
    }

    private static IEnumerable<string> ReadList(string[] lines, ref int index, string firstValue)
    {
        if (firstValue != "[")
        {
            return ParseItems(firstValue);
        }

        // Multi-line flow list: items live on the lines after the opening bracket. Leaves index on the
        // closing bracket so the caller's increment lands on the next key.
        var items = new List<string>();
        index++;
        while (index < lines.Length)
        {
            var line = StripComment(lines[index]).Trim();
            if (line == "]")
            {
                break;
            }

            if (line.Length > 0)
            {
                items.Add(StripItem(line));
            }

            index++;
        }

        return items;
    }

    // Quote-aware so a label that is itself a comma survives; unquoted empty
    // slots are separators, not labels.
    private static IEnumerable<string> ParseItems(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quoted = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                quoted = true;
                continue;
            }

            if (c == ',' && !inQuote)
            {
                if (quoted || current.Length > 0)
                {
                    items.Add(current.ToString());
                }

                current.Clear();
                quoted = false;
                continue;
            }

            if (c == ']' && !inQuote)
            {
                continue;
            }

            current.Append(c);
        }

        if (quoted || current.Length > 0)
        {
            items.Add(current.ToString());
        }

        return items;
    }

    private static string StripItem(string item)
    {
        var trimmed = item.Trim();
        if (trimmed.Length > 1 && trimmed.EndsWith(','))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Trim('"');
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }
}
