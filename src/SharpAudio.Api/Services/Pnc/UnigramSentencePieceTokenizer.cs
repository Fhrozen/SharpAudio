// wiki: pnc#tokenizer
using System.Text;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>
/// Unigram SentencePiece segmentation as implemented by the reference Python tokenizer for
/// <c>1-800-BAD-CODE/punctuation_fullstop_truecase_english</c> (see <c>tools/pnc/spike_tokenizer.py</c>,
/// which asserts parity case by case). The rules this class reproduces:
/// <list type="bullet">
/// <item>normalization is only collapsing runs of ASCII spaces and trimming - no NFKC, no case folding,
/// no accent stripping; pre-lowercasing and punctuation stripping belong to the engine, because the
/// model was trained on lowercase unpunctuated text;</item>
/// <item>words are split on ASCII space and each word is segmented with the <c>▁</c> word-start marker
/// prepended, so pieces are returned grouped per word;</item>
/// <item>segmentation maximizes total piece score, ties going to the piece earlier in vocab order;</item>
/// <item>special pieces never match input text, and byte pieces are only used when byte fallback is
/// enabled;</item>
/// <item>a run of bytes with no matching piece yields a single <c>&lt;unk&gt;</c>.</item>
/// </list>
/// </summary>
public sealed class UnigramSentencePieceTokenizer : ITokenizer
{
    private const char Marker = '\u2581';

    public char WordStartMarker => Marker;

    // Special pieces are identified by name because the type field is sparse in the model file. They are
    // vocabulary markers rather than text, so they are kept out of the trie and can never match input.
    private static readonly string[] SpecialPieces = ["<unk>", "<s>", "</s>", "<pad>"];

    private readonly IReadOnlyList<SentencePiecePiece> _pieces;
    private readonly List<TrieNode> _nodes = new();
    private readonly int[] _bytePieceIds = new int[256];
    private readonly int[] _pieceByteLengths;

    public int UnkId { get; }

    public int BosId { get; }

    public int EosId { get; }

    public int MaxPieceLength { get; }

    public bool ByteFallback { get; }

    public UnigramSentencePieceTokenizer(IReadOnlyList<SentencePiecePiece> pieces, bool byteFallback = false)
    {
        _pieces = pieces;
        ByteFallback = byteFallback;
        _pieceByteLengths = new int[pieces.Count];
        Array.Fill(_bytePieceIds, -1);
        _nodes.Add(new TrieNode());

        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];

            // A byte piece consumes one byte even though its name is written as <0xNN>.
            _pieceByteLengths[i] = piece.Type == SentencePiecePieceType.Byte ? 1 : Encoding.UTF8.GetByteCount(piece.Piece);

            if (IsSpecial(piece.Piece))
            {
                continue;
            }

            // Byte pieces stay out of the trie so they cannot collide with a normal piece that starts
            // with the same byte; they are single-byte candidates only when byte fallback is enabled.
            if (piece.Type == SentencePiecePieceType.Byte)
            {
                if (byteFallback)
                {
                    _bytePieceIds[GetByteValue(piece.Piece)] = i;
                }

                continue;
            }

            Insert(piece.Piece, i);
        }

        UnkId = FindId("<unk>");
        BosId = FindId("<s>");
        EosId = FindId("</s>");
        MaxPieceLength = pieces.Where(p => !IsSpecial(p.Piece)).Max(p => p.Type == SentencePiecePieceType.Byte ? 1 : p.Piece.Length);
    }

    public static UnigramSentencePieceTokenizer FromFile(string modelPath) => new(SentencePieceModelReader.Read(modelPath));

    public IReadOnlyList<TokenWord> Encode(string text)
    {
        var words = new List<TokenWord>();
        var best = new float[64];
        var choice = new int[64];

        foreach (var word in Normalize(text).Split(' '))
        {
            if (word.Length == 0)
            {
                continue;
            }

            var segment = Encoding.UTF8.GetBytes(Marker + word);

            if (segment.Length > choice.Length)
            {
                best = new float[segment.Length + 1];
                choice = new int[segment.Length];
            }

            BestSegmentation(segment, best, choice);

            var pieces = new List<TokenPiece>();
            int i = 0;

            while (i < segment.Length)
            {
                int id = choice[i];

                if (id == UnkId)
                {
                    while (i < segment.Length && choice[i] == UnkId)
                    {
                        i++;
                    }
                }
                else
                {
                    i += _pieceByteLengths[id];
                }

                pieces.Add(new TokenPiece(id, _pieces[id].Piece));
            }

            words.Add(new TokenWord(pieces));
        }

        return words;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool inSpaceRun = false;

        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (!inSpaceRun)
                {
                    builder.Append(c);
                    inSpaceRun = true;
                }

                continue;
            }

            builder.Append(c);
            inSpaceRun = false;
        }

        return builder.ToString().Trim(' ');
    }

    /// <summary>Viterbi over the piece trie for one word (including its word-start marker), writing the
    /// piece id chosen at each position into <paramref name="choice"/>. The buffers are reused across
    /// words within one call.</summary>
    private void BestSegmentation(byte[] segment, float[] best, int[] choice)
    {
        int length = segment.Length;
        best[length] = 0f;

        for (int i = length - 1; i >= 0; i--)
        {
            float bestScore = float.MinValue;
            int bestId = -1;

            int node = 0;

            for (int j = i; j < length; j++)
            {
                var current = _nodes[node];

                if (current.Children is null || !current.Children.TryGetValue(segment[j], out node))
                {
                    break;
                }

                int id = _nodes[node].PieceId;

                if (id < 0)
                {
                    continue;
                }

                float total = _pieces[id].Score + best[j + 1];

                // Ties go to the piece earlier in vocab order, matching the reference, which scans the
                // vocab in order and keeps the first strict maximum.
                if (total > bestScore || (total == bestScore && bestId >= 0 && id < bestId))
                {
                    bestScore = total;
                    bestId = id;
                }
            }

            if (ByteFallback)
            {
                int byteId = _bytePieceIds[segment[i]];

                if (byteId >= 0)
                {
                    float total = _pieces[byteId].Score + best[i + 1];

                    if (total > bestScore || (total == bestScore && bestId >= 0 && byteId < bestId))
                    {
                        bestScore = total;
                        bestId = byteId;
                    }
                }
            }

            if (bestId < 0)
            {
                // <unk> scores 0 and consumes one byte, so the rest of the word still counts.
                best[i] = best[i + 1];
                choice[i] = UnkId;
                continue;
            }

            best[i] = bestScore;
            choice[i] = bestId;
        }
    }

    private static bool IsSpecial(string piece) => SpecialPieces.Contains(piece);

    private static int GetByteValue(string piece)
    {
        if (piece.Length != 6 || !piece.StartsWith("<0x") || !piece.EndsWith(">"))
        {
            throw new InvalidDataException($"SentencePiece model has a malformed byte piece '{piece}'.");
        }

        return Convert.ToInt32(piece[3..5], 16);
    }

    private void Insert(string piece, int id)
    {
        int node = 0;

        foreach (byte b in Encoding.UTF8.GetBytes(piece))
        {
            var current = _nodes[node];
            current.Children ??= new Dictionary<byte, int>();

            if (!current.Children.TryGetValue(b, out int next))
            {
                next = _nodes.Count;
                _nodes.Add(new TrieNode());
                current.Children[b] = next;
            }

            node = next;
        }

        _nodes[node].PieceId = id;
    }

    // The type field is sparse in this file (only four entries carry one), so special pieces are
    // located by name.
    private int FindId(string piece)
    {
        for (int i = 0; i < _pieces.Count; i++)
        {
            if (_pieces[i].Piece == piece)
            {
                return i;
            }
        }

        throw new InvalidDataException($"SentencePiece model has no piece '{piece}'.");
    }

    private sealed class TrieNode
    {
        // Children are allocated lazily: a 32k vocab builds far more nodes than pieces, and most of them
        // are leaves that never need a dictionary.
        public Dictionary<byte, int>? Children { get; set; }

        public int PieceId { get; set; } = -1;
    }
}
