using SharpAudio.Api.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncEngineTests
{
    private const char Marker = '\u2581';

    // Fake tokenizer: every vocabulary entry is one piece, so pieces and ids stay in step.
    private sealed class FakeTokenizer : ITokenizer
    {
        private readonly Dictionary<string, int> _vocab = new();

        public FakeTokenizer(params string[] pieces)
        {
            for (var i = 0; i < pieces.Length; i++)
            {
                _vocab[pieces[i]] = i;
            }
        }

        public IReadOnlyList<TokenWord> Encode(string text) => text
            .Split(' ')
            .Where(word => word.Length > 0)
            .Select(word =>
            {
                var piece = word.StartsWith(Marker) ? word : Marker + word;
                var id = _vocab.TryGetValue(piece, out var found) ? found : _vocab[word];
                return new TokenWord([new TokenPiece(id, _vocab.ContainsKey(piece) ? piece : word)]);
            })
            .ToList();

        public int UnkId => 0;

        public int BosId => 1;

        public int EosId => 2;

        public int MaxPieceLength => 16;

        public bool ByteFallback => false;

        public char WordStartMarker => Marker;
    }

    // Fake predictor: predictions keyed by piece id, so tests can drive the collector without ONNX.
    private sealed class FakePredictor : IPncPredictor
    {
        private readonly Dictionary<int, PncTokenPrediction> _predictions;

        public FakePredictor(params PncTokenPrediction[] predictions)
        {
            _predictions = predictions.Select((p, i) => (p, i)).ToDictionary(x => x.i, x => x.p);
        }

        public IReadOnlyList<PncTokenPrediction> Predict(IReadOnlyList<int> segmentIds) =>
            segmentIds.Select(id => _predictions[id]).ToList();
    }

    private static PncModelConfig Config(int maxLength = 256, int overlap = 16) => new()
    {
        PreLabels = ["<NULL>", "¿"],
        PostLabels = ["<NULL>", "<ACRONYM>", ".", ",", "?"],
        MaxLength = maxLength,
        Overlap = overlap
    };

    private static PncTokenPrediction Pred(int pieceId, int post, bool seg, params int[] capitalized)
    {
        var row = new bool[16];
        foreach (var index in capitalized)
        {
            row[index] = true;
        }

        return new PncTokenPrediction(0, post, row, seg);
    }

    [Fact]
    public void Apply_pre_cleans_before_tokenizing()
    {
        var tokenizer = new FakeTokenizer("<unk>", "<s>", "</s>", "▁hello", "▁world");
        var predictor = new FakePredictor(
            Pred(0, 0, false),
            Pred(1, 0, false),
            Pred(2, 0, false),
            Pred(3, 0, false, 1),
            Pred(4, 2, true, 1));

        using var engine = new PncEngine(tokenizer, predictor, Config());

        // Punctuation and case in the input are removed first, so the model sees what it was trained on.
        Assert.Equal("Hello World.", engine.Apply("HELLO, WORLD!")[0]);
    }

    [Fact]
    public void Apply_runs_each_window_through_the_predictor_and_keeps_the_untrimmed_middle()
    {
        var tokenizer = new FakeTokenizer("<unk>", "<s>", "</s>", "▁a", "▁b", "▁c", "▁d", "▁e");
        var predictor = new FakePredictor(
            Pred(0, 0, false),
            Pred(1, 0, false),
            Pred(2, 0, false),
            Pred(3, 0, false),
            Pred(4, 0, false),
            Pred(5, 0, false),
            Pred(6, 0, false),
            Pred(7, 0, false));

        // maxTokens = 5 - 2 = 3, overlap 2: the three windows cover piece positions 0-2, 1-3 and 2-4,
        // and the collector keeps positions 0-1, 2 and 3-4, so every piece appears exactly once.
        var config = Config(maxLength: 5, overlap: 2);
        using var engine = new PncEngine(tokenizer, predictor, config);

        Assert.Equal("a b c d e", engine.Apply("a b c d e")[0]);
    }

    [Fact]
    public void Apply_returns_nothing_for_input_that_cleans_to_empty()
    {
        var tokenizer = new FakeTokenizer("<unk>");
        var predictor = new FakePredictor(Pred(0, 0, false));
        using var engine = new PncEngine(tokenizer, predictor, Config());

        Assert.Empty(engine.Apply("  ...  "));
    }
}
