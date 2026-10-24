// wiki: pnc#engine
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharpAudio.Core.Services.Pnc;

/// <summary>Prediction for one subword: label ids plus per-character capitalisation and the sentence
/// boundary flag. Lengths come from the model config, so nothing is hardcoded per model.</summary>
public sealed record PncTokenPrediction(
    int PreLabelId,
    int PostLabelId,
    IReadOnlyList<bool> CapPerChar,
    bool Seg);

/// <summary>Predicts one subsegment. Kept separate from the collector so the collector can be tested with
/// fake predictions. Takes a span so a window is a slice of the caller's id list rather than a copy.</summary>
public interface IPncPredictor
{
    IReadOnlyList<PncTokenPrediction> Predict(ReadOnlySpan<int> segmentIds);
}

/// <summary>Runs the four-head ONNX model for a single subsegment. The graph already argmaxes, so the
/// outputs are label ids and booleans rather than logits. Output positions are resolved from the
/// session metadata once so each call indexes the results instead of searching them by name; the
/// input array stays per-call because <see cref="Predict"/> runs outside <see cref="PncEngine"/>'s
/// lock, so a shared buffer would need serialisation.</summary>
public sealed class OnnxPncPredictor : IPncPredictor, IDisposable
{
    private static readonly string[] OutputNames = ["pre_preds", "post_preds", "seg_preds", "cap_preds"];

    private readonly InferenceSession _session;
    private readonly ITokenizer _tokenizer;
    private readonly PncModelConfig _config;
    private readonly Dictionary<string, int> _outputPositions;
    private bool _disposed;

    public OnnxPncPredictor(InferenceSession session, ITokenizer tokenizer, PncModelConfig config)
    {
        _session = session;
        _tokenizer = tokenizer;
        _config = config;

        // OutputMetadata lists outputs in the order Run returns them, so the position is what the
        // results index needs - resolving it once keeps the hot path free of name lookups.
        var names = new List<string>();
        foreach (var output in session.OutputMetadata)
        {
            names.Add(output.Key);
        }

        _outputPositions = new Dictionary<string, int>();
        foreach (var name in OutputNames)
        {
            _outputPositions[name] = names.IndexOf(name);
        }
    }

    public IReadOnlyList<PncTokenPrediction> Predict(ReadOnlySpan<int> segmentIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // [BOS] + ids + [EOS], padded to the model sequence length with the pad id.
        var inputIds = new long[_config.MaxLength];
        inputIds[0] = _tokenizer.BosId;
        for (var i = 0; i < segmentIds.Length; i++)
        {
            inputIds[1 + i] = segmentIds[i];
        }

        inputIds[1 + segmentIds.Length] = _tokenizer.EosId;
        for (var i = 2 + segmentIds.Length; i < inputIds.Length; i++)
        {
            inputIds[i] = _config.PadId;
        }

        var tensor = new DenseTensor<long>(inputIds, new[] { 1, _config.MaxLength });
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", tensor)]);

        var pre = results[_outputPositions["pre_preds"]].AsTensor<long>();
        var post = results[_outputPositions["post_preds"]].AsTensor<long>();
        var seg = results[_outputPositions["seg_preds"]].AsTensor<bool>();
        var cap = results[_outputPositions["cap_preds"]].AsTensor<bool>();
        var capRowLength = cap.Dimensions[2];

        var predictions = new List<PncTokenPrediction>(segmentIds.Length);
        for (var i = 0; i < segmentIds.Length; i++)
        {
            // Predictions for the BOS/EOS slots are skipped, matching the reference slice [1:length-1].
            predictions.Add(new PncTokenPrediction((int)pre[i + 1], (int)post[i + 1], CapRow(cap, i + 1, capRowLength), seg[i + 1]));
        }

        return predictions;
    }

    /// <summary>One subword's capitalisation row. Only the rows the caller actually consumes are
    /// materialised, so padding rows never become lists.</summary>
    private static IReadOnlyList<bool> CapRow(Tensor<bool> cap, int row, int rowLength)
    {
        var values = new bool[rowLength];
        for (var c = 0; c < rowLength; c++)
        {
            values[c] = cap[0, row, c];
        }

        return values;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
