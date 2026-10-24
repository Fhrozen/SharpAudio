// wiki: testing#unit-tests
using SharpAudio.Core.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class UnigramSentencePieceTokenizerTests
{
    private static UnigramSentencePieceTokenizer Tokenizer(params (string Piece, float Score, SentencePiecePieceType Type)[] pieces) =>
        new(pieces.Select(p => new SentencePiecePiece(p.Piece, p.Score, p.Type)).ToList());

    // Fixture with enough pieces to make the DP choose between a long piece and a better
    // two-piece path, plus byte pieces and characters that only reach the vocab as <unk>.
    private static readonly (string Piece, float Score, SentencePiecePieceType Type)[] FixturePieces =
    [
        ("<unk>", 0f, SentencePiecePieceType.Unknown),
        ("<s>", 0f, SentencePiecePieceType.Control),
        ("</s>", 0f, SentencePiecePieceType.Control),
        ("\u2581", -7f, SentencePiecePieceType.Normal),
        ("\u2581ab", -5f, SentencePiecePieceType.Normal),
        ("\u2581a", -1f, SentencePiecePieceType.Normal),
        ("a", -1f, SentencePiecePieceType.Normal),
        ("b", -2f, SentencePiecePieceType.Normal),
        ("\u2581q", -1f, SentencePiecePieceType.Normal),
        ("q", -1f, SentencePiecePieceType.Normal),
        ("\u2581x", -2f, SentencePiecePieceType.Normal),
        ("<0x78>", -0.1f, SentencePiecePieceType.Byte),
        ("<0x7A>", -0.5f, SentencePiecePieceType.Byte),
        ("\u2581longer", -1f, SentencePiecePieceType.Normal),
    ];

    private static UnigramSentencePieceTokenizer DefaultFixture(bool byteFallback = false) =>
        new(FixturePieces.Select(p => new SentencePiecePiece(p.Piece, p.Score, p.Type)).ToList(), byteFallback);

    private static string Pieces(UnigramSentencePieceTokenizer tokenizer, string text) =>
        string.Join(" ", tokenizer.Encode(text).SelectMany(word => word.Pieces).Select(p => p.Piece));

    private static string Pieces(string text) => Pieces(DefaultFixture(), text);

    private static string Ids(string text) =>
        string.Join(" ", DefaultFixture().Encode(text).SelectMany(word => word.Pieces).Select(p => p.Id));

    [Fact]
    public void Encode_prefers_higher_total_score_over_longer_piece()
    {
        // "▁ab" scores -5, but "▁a" (-1) + "b" (-2) scores -3, so the two-piece path wins.
        Assert.Equal("\u2581a b", Pieces("ab"));
    }

    [Fact]
    public void Encode_ties_go_to_piece_earlier_in_vocab_order()
    {
        // Both paths score -3; vocab order decides, so "▁ab" wins when it comes first.
        var first = Tokenizer(
            ("<unk>", 0f, SentencePiecePieceType.Unknown),
            ("<s>", 0f, SentencePiecePieceType.Control),
            ("</s>", 0f, SentencePiecePieceType.Control),
            ("\u2581ab", -3f, SentencePiecePieceType.Normal),
            ("\u2581a", -1f, SentencePiecePieceType.Normal),
            ("b", -2f, SentencePiecePieceType.Normal));
        Assert.Equal("\u2581ab", Pieces(first, "ab"));

        var second = Tokenizer(
            ("<unk>", 0f, SentencePiecePieceType.Unknown),
            ("<s>", 0f, SentencePiecePieceType.Control),
            ("</s>", 0f, SentencePiecePieceType.Control),
            ("\u2581a", -1f, SentencePiecePieceType.Normal),
            ("\u2581ab", -3f, SentencePiecePieceType.Normal),
            ("b", -2f, SentencePiecePieceType.Normal));
        Assert.Equal("\u2581a b", Pieces(second, "ab"));
    }

    [Fact]
    public void Encode_merges_a_run_of_unknown_characters_into_one_unk()
    {
        // "z" has no piece, so the three unknown characters collapse into one <unk>.
        Assert.Equal("\u2581 <unk>", Pieces("zzz"));
        Assert.Equal("\u2581q <unk>", Pieces("qzzz"));
    }

    [Fact]
    public void Encode_groups_pieces_per_word()
    {
        var words = DefaultFixture().Encode("a q");

        Assert.Equal(2, words.Count);
        Assert.Equal("\u2581a", string.Join(" ", words[0].Pieces.Select(p => p.Piece)));
        Assert.Equal("\u2581q", string.Join(" ", words[1].Pieces.Select(p => p.Piece)));
    }

    [Fact]
    public void Encode_never_matches_a_special_piece_against_input_text()
    {
        // Special pieces are vocab markers, not text, so literal "<s>" can only segment as unknown.
        Assert.Equal("\u2581 <unk>", Pieces("<s>"));
        Assert.Equal("\u2581 <unk>", Pieces("<unk>"));
    }

    [Fact]
    public void Encode_skips_byte_pieces_when_byte_fallback_is_off()
    {
        // "z" has only a byte piece, so without byte fallback it can only reach the vocab as <unk>.
        Assert.Equal("\u2581 <unk>", Pieces("zzz"));

        // "x" is segmented by the normal piece "▁x"; the byte piece for x is never considered.
        Assert.Equal("\u2581x", Pieces("x"));
    }

    [Fact]
    public void Encode_uses_byte_pieces_when_byte_fallback_is_on()
    {
        var tokenizer = DefaultFixture(byteFallback: true);

        Assert.Equal("\u2581 <0x7A> <0x7A> <0x7A>", Pieces(tokenizer, "zzz"));

        // A normal piece still wins when it beats the byte piece on total score.
        Assert.Equal("\u2581x", Pieces(tokenizer, "x"));
    }

    [Fact]
    public void Encode_splits_on_space_and_marks_word_starts()
    {
        Assert.Equal("\u2581a \u2581q", Pieces("a q"));
    }

    [Fact]
    public void Normalize_collapses_space_runs_and_trims_only()
    {
        Assert.Equal("\u2581a \u2581q", Pieces("  a   q  "));
        Assert.Equal("", Pieces(""));

        // Tabs and newlines are not whitespace mapping: they become <unk>, and the word is not
        // re-split, so "q" continues the same word rather than starting a new one.
        Assert.Equal("\u2581a <unk> q", Pieces("a\tq"));
    }

    [Fact]
    public void Normalize_does_not_fold_case_or_accents()
    {
        // Uppercase has no pieces in this lowercase vocab, so it can only be <unk>.
        Assert.Equal("\u2581a <unk>", Pieces("aA"));
    }

    [Fact]
    public void Encode_returns_piece_ids_and_text()
    {
        Assert.Equal("5 7", Ids("ab"));
        Assert.Equal("\u2581a b", Pieces("ab"));
    }

    [Fact]
    public void Special_ids_come_from_the_vocab_and_encode_adds_no_special_tokens()
    {
        var tokenizer = DefaultFixture();

        Assert.Equal(0, tokenizer.UnkId);
        Assert.Equal(1, tokenizer.BosId);
        Assert.Equal(2, tokenizer.EosId);
        Assert.DoesNotContain(tokenizer.Encode("ab").SelectMany(word => word.Pieces).Select(p => p.Id),
            id => id == tokenizer.BosId);
    }

    [Fact]
    public void MaxPieceLength_is_the_longest_piece_excluding_special_pieces()
    {
        Assert.Equal(7, DefaultFixture().MaxPieceLength);

        // "<unk>" is longer than any normal piece here, so it must not be counted.
        var onlySpecialsAreLong = Tokenizer(
            ("<unk>", 0f, SentencePiecePieceType.Unknown),
            ("<s>", 0f, SentencePiecePieceType.Control),
            ("</s>", 0f, SentencePiecePieceType.Control),
            ("\u2581a", -1f, SentencePiecePieceType.Normal));
        Assert.Equal(2, onlySpecialsAreLong.MaxPieceLength);
    }
}
