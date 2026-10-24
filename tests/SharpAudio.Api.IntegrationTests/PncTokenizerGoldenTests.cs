// wiki: testing#pnc-tokenizer-golden-test
using SharpAudio.Api.IntegrationTests.Support;
using SharpAudio.Api.Services.Pnc;
using Xunit;

namespace SharpAudio.Api.IntegrationTests;

/// <summary>
/// Golden parity test for <see cref="UnigramSentencePieceTokenizer"/> against the Python
/// <c>sentencepiece</c> reference. Expectations come from <c>tools/pnc/spike_tokenizer.py</c>, which
/// asserts the same cases on every run; pieces are joined with spaces so failures read as the reference
/// segmentation.
/// </summary>
public sealed class PncTokenizerGoldenTests
{
    private static readonly (string Text, string Pieces)[] Golden =
    [
        ("hello, world.", "\u2581hello , \u2581world ."),
        ("the quick brown fox jumps over the lazy dog.", "\u2581the \u2581quick \u2581brown \u2581fox \u2581jumps \u2581over \u2581the \u2581lazy \u2581dog ."),
        ("i saw mr smith at the store he was shopping for a new lawn mower.", "\u2581i \u2581saw \u2581mr \u2581smith \u2581at \u2581the \u2581store \u2581he \u2581was \u2581shopping \u2581for \u2581a \u2581new \u2581lawn \u2581mower ."),
        ("U.S.A. funding, and itma was a radio comedy programme.", "\u2581 <unk> . <unk> . <unk> . \u2581funding , \u2581and \u2581it ma \u2581was \u2581a \u2581radio \u2581comedy \u2581programme ."),
        ("café naïve, despite being mid March, it snowed.", "\u2581café \u2581na <unk> ve , \u2581despite \u2581being \u2581mid \u2581 <unk> arch , \u2581it \u2581snow ed ."),
        ("one two three four five six seven eight nine ten", "\u2581one \u2581two \u2581three \u2581four \u2581five \u2581six \u2581seven \u2581eight \u2581nine \u2581ten"),
        ("they're here, they're there, they're everywhere.", "\u2581they ' re \u2581here , \u2581they ' re \u2581there , \u2581they ' re \u2581everywhere ."),
        ("hello\tworld", "\u2581hello <unk> world"),
        ("a—b", "\u2581a <unk> b"),
        ("the £5 note, the ½ cup, the ﬁne ﬂow", "\u2581the \u2581£5 \u2581note , \u2581the \u2581 <unk> \u2581cup , \u2581the \u2581 <unk> ne \u2581 <unk> ow"),
        ("emoji 😀 test", "\u2581emoji \u2581 <unk> \u2581test"),
        ("mixed CASE Words here", "\u2581mixed \u2581 <unk> \u2581 <unk> or d s \u2581here"),
        ("<s>", "\u2581 <unk> s <unk>"),
    ];

    [SkippableFact]
    public void Encode_matches_the_python_reference()
    {
        Skip.IfNot(PncModelTestGate.IsEnabled, PncModelTestGate.SkipReason);

        var tokenizer = UnigramSentencePieceTokenizer.FromFile(PncModelTestGate.ModelPath!);

        foreach (var (text, expected) in Golden)
        {
            var pieces = string.Join(" ", tokenizer.Encode(text).SelectMany(word => word.Pieces).Select(p => p.Piece));
            Assert.Equal(expected, pieces);
        }
    }

    [SkippableFact]
    public void Encode_groups_pieces_per_word()
    {
        Skip.IfNot(PncModelTestGate.IsEnabled, PncModelTestGate.SkipReason);

        var tokenizer = UnigramSentencePieceTokenizer.FromFile(PncModelTestGate.ModelPath!);
        var words = tokenizer.Encode("hello, world.");

        Assert.Equal(2, words.Count);
        Assert.Equal("\u2581hello ,", string.Join(" ", words[0].Pieces.Select(p => p.Piece)));
        Assert.Equal("\u2581world .", string.Join(" ", words[1].Pieces.Select(p => p.Piece)));
    }
}
