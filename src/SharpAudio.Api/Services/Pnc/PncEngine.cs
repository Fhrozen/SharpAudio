// wiki: pnc#engine
namespace SharpAudio.Api.Services.Pnc;

/// <summary>Adds punctuation, capitalisation and sentence boundaries to ASR output. Returns sentences.
/// Disposable because an engine owns its tokenizer and inference session.</summary>
public interface IPncEngine : IDisposable
{
    PncModelConfig Config { get; }

    IReadOnlyList<string> Apply(string text);
}

/// <summary>Engine wiring: clean the input, encode, run the model over subsegments, collect the text.
/// Byte-fallback pieces would be emitted literally by the collector; the shipped vocab has no byte pieces,
/// so this is a documented limitation rather than a behaviour.</summary>
public sealed class PncEngine : IPncEngine, IDisposable
{
    private readonly ITokenizer _tokenizer;
    private readonly IPncPredictor _predictor;
    private readonly object _gate = new();
    private bool _disposed;

    public PncModelConfig Config { get; }

    public PncEngine(ITokenizer tokenizer, IPncPredictor predictor, PncModelConfig config)
    {
        _tokenizer = tokenizer;
        _predictor = predictor;
        Config = config;
    }

    public IReadOnlyList<string> Apply(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The tokenizer reuses its DP buffers, so calls are serialized per engine.
        lock (_gate)
        {
            var cleaned = PncInputCleaner.Clean(text);
            if (cleaned.Length == 0)
            {
                return [];
            }

            var ids = new List<int>();
            var pieces = new List<string>();
            foreach (var word in _tokenizer.Encode(cleaned))
            {
                foreach (var piece in word.Pieces)
                {
                    ids.Add(piece.Id);
                    pieces.Add(piece.Piece);
                }
            }

            if (ids.Count == 0)
            {
                return [];
            }

            var windows = PncWindowPlanner.Plan(ids, Config);
            var predictions = windows.Select(window => _predictor.Predict(ids.GetRange(window.Start, window.Length))).ToList();

            return PncCollector.Collect(windows, predictions, pieces, Config, _tokenizer.WordStartMarker);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_predictor is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
