// wiki: testing#unit-tests
using SharpAudio.Api.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncCollectorTests
{
    private const char Marker = '\u2581';

    private static PncModelConfig Config(int overlap = 16) => new()
    {
        PreLabels = ["<NULL>", "¿"],
        PostLabels = ["<NULL>", "<ACRONYM>", ".", ",", "?"],
        MaxLength = 256,
        Overlap = overlap
    };

    // Cap rows are as long as MaxPieceLength, and are indexed by the absolute character index inside the
    // piece string, so index 0 is the word-start marker slot.
    private static PncTokenPrediction Pred(int pre, int post, bool seg, params int[] capitalized)
    {
        var row = new bool[16];
        foreach (var index in capitalized)
        {
            row[index] = true;
        }

        return new PncTokenPrediction(pre, post, row, seg);
    }

    private static IReadOnlyList<string> Collect(
        IReadOnlyList<PncWindow> windows,
        IReadOnlyList<IReadOnlyList<PncTokenPrediction>> predictions,
        IReadOnlyList<string> pieces,
        PncModelConfig config,
        bool applySbd = true) => PncCollector.Collect(windows, predictions, pieces, config, Marker, applySbd);

    [Fact]
    public void Collect_applies_casing_pre_and_post_labels()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 2)],
            [[Pred(0, 3, false, 1), Pred(0, 2, false, 1)]],
            ["▁hello", "▁world"],
            Config());

        Assert.Equal("Hello, World.", sentences[0]);
    }

    [Fact]
    public void Collect_splits_sentences_at_a_sentence_boundary_prediction()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 3)],
            [[Pred(0, 0, false, 1), Pred(0, 0, true), Pred(0, 2, false)]],
            ["▁hello", "▁world", "▁ok"],
            Config());

        Assert.Equal(2, sentences.Count);
        Assert.Equal("Hello world", sentences[0]);
        Assert.Equal("ok.", sentences[1]);
    }

    [Fact]
    public void Collect_post_punctuation_is_written_before_the_sentence_boundary_flush()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 1)],
            [[Pred(0, 2, true, 1)]],
            ["▁fine"],
            Config());

        Assert.Equal("Fine.", sentences[0]);
    }

    [Fact]
    public void Collect_acronym_label_inserts_a_period_after_every_character()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 1)],
            [[Pred(0, 1, false, 1)]],
            ["▁usa"],
            Config());

        Assert.Equal("U.s.a.", sentences[0]);
    }

    [Fact]
    public void Collect_keeps_only_the_middle_half_of_each_overlapped_window()
    {
        IReadOnlyList<string> pieces = ["▁a", "▁b", "▁c", "▁d", "▁e", "▁f", "▁g", "▁h", "▁i", "▁j", "▁k", "▁l"];
        var predictions = new List<IReadOnlyList<PncTokenPrediction>>
        {
            Enumerable.Range(0, 6).Select(_ => Pred(0, 0, false)).ToList(),
            Enumerable.Range(0, 6).Select(_ => Pred(0, 0, false)).ToList()
        };

        // Overlap 4 keeps pieces 0-3 from window 0 and 6-9 from window 1, so e, f and k, l are dropped.
        var sentences = Collect(
            [new PncWindow(0, 0, 6), new PncWindow(1, 4, 6)],
            predictions,
            pieces,
            Config(overlap: 4));

        Assert.Equal("a b c d g h i j", sentences[0]);
    }

    [Fact]
    public void Collect_does_not_start_a_sentence_with_a_space()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 2)],
            [[Pred(0, 0, true), Pred(0, 0, false)]],
            ["▁hello", "▁world"],
            Config());

        Assert.Equal("hello|world", string.Join("|", sentences));
    }

    [Fact]
    public void Collect_inserts_pre_labels_at_the_first_character_of_a_continuation_piece()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 2)],
            [[Pred(0, 0, false), Pred(1, 0, false)]],
            ["▁hello", "world"],
            Config());

        Assert.Equal("hello¿world", sentences[0]);
    }

    [Fact]
    public void Collect_ignores_the_word_start_marker_slot_when_capitalizing()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 1)],
            [[Pred(0, 0, false, 0)]],
            ["▁hi"],
            Config());

        Assert.Equal("hi", sentences[0]);
    }

    [Fact]
    public void Collect_returns_one_sentence_when_sentence_boundary_detection_is_off()
    {
        var sentences = Collect(
            [new PncWindow(0, 0, 2)],
            [[Pred(0, 0, true), Pred(0, 2, false)]],
            ["▁hello", "▁world"],
            Config(),
            applySbd: false);

        Assert.Equal("hello world.", sentences[0]);
    }
}
