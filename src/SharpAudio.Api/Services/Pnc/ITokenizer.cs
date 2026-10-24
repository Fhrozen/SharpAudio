namespace SharpAudio.Api.Services.Pnc;

/// <summary>
/// Model-agnostic PnC tokenizer. <see cref="Encode"/> returns subword pieces grouped by word without
/// special tokens - the engine adds BOS/EOS itself - and keeps the piece text because punctuation is
/// predicted per subword while true-casing is predicted per character.
/// </summary>
public interface ITokenizer
{
    IReadOnlyList<TokenWord> Encode(string text);

    int UnkId { get; }

    int BosId { get; }

    int EosId { get; }

    /// <summary>Longest piece in the vocab, special pieces excluded: the per-character heads pad each
    /// token to this length.</summary>
    int MaxPieceLength { get; }

    /// <summary>Whether single-byte pieces may be used for bytes no normal piece covers. False matches
    /// the reference behaviour of the Phase C model.</summary>
    bool ByteFallback { get; }

    /// <summary>Piece prefix that marks the start of a word, used when rebuilding text from pieces.</summary>
    char WordStartMarker { get; }
}

public readonly record struct TokenPiece(int Id, string Piece);

public readonly record struct TokenWord(IReadOnlyList<TokenPiece> Pieces);
