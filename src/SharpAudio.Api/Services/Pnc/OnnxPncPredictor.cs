using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Prediction for one subword: label ids plus per-character capitalisation and the sentence
/// boundary flag. Lengths come from the model config, so nothing is hardcoded per model.</summary>
public sealed record PncTokenPrediction(
    int PreLabelId,
    int PostLabelId,
    IReadOnlyList<bool> CapPerChar,
    bool Seg);

/// <summary>Predicts one subsegment. Kept separate from the collector so the collector can be tested with
/// fake predictions.</summary>
public interface IPncPredictor
{
    IReadOnlyList<PncTokenPrediction> Predict(IReadOnlyList<int> segmentIds);
}

/// <summary>Runs the four-head ONNX model for a single subsegment. The graph already argmaxes, so the
/// outputs are label ids and booleans rather than logits.</summary>
public sealed class OnnxPncPredictor : IPncPredictor, IDisposable
{
    private readonly InferenceSession _session;
    private readonly ITokenizer _tokenizer;
    private readonly PncModelConfig _config;
    private bool _disposed;

    public OnnxPncPredictor(InferenceSession session, ITokenizer tokenizer, PncModelConfig config)
    {
        _session = session;
        _tokenizer = tokenizer;
        _config = config;
    }

    public IReadOnlyList<PncTokenPrediction> Predict(IReadOnlyList<int> segmentIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // [BOS] + ids + [EOS], padded to the model sequence length with the pad id.
        var inputIds = new long[_config.MaxLength];
        inputIds[0] = _tokenizer.BosId;
        for (var i = 0; i < segmentIds.Count; i++)
        {
            inputIds[1 + i] = segmentIds[i];
        }

        inputIds[1 + segmentIds.Count] = _tokenizer.EosId;
        for (var i = 2 + segmentIds.Count; i < inputIds.Length; i++)
        {
            inputIds[i] = _config.PadId;
        }

        var tensor = new DenseTensor<long>(inputIds, new[] { 1, _config.MaxLength });
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", tensor)]);

        var pre = ReadInts(results, "pre_preds");
        var post = ReadInts(results, "post_preds");
        var seg = ReadBools(results, "seg_preds");
        var cap = ReadCapRows(results);

        var predictions = new List<PncTokenPrediction>(segmentIds.Count);
        for (var i = 0; i < segmentIds.Count; i++)
        {
            // Predictions for the BOS/EOS slots are skipped, matching the reference slice [1:length-1].
            predictions.Add(new PncTokenPrediction(pre[i + 1], post[i + 1], cap[i + 1], seg[i + 1]));
        }

        return predictions;
    }

    private static IReadOnlyList<int> ReadInts(IEnumerable<NamedOnnxValue> results, string name)
    {
        var tensor = results.First(r => r.Name == name).AsTensor<long>();
        return tensor.ToArray().Select(v => (int)v).ToList();
    }

    private static IReadOnlyList<bool> ReadBools(IEnumerable<NamedOnnxValue> results, string name)
    {
        var tensor = results.First(r => r.Name == name).AsTensor<bool>();
        return tensor.ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<bool>> ReadCapRows(IEnumerable<NamedOnnxValue> results)
    {
        var tensor = results.First(r => r.Name == "cap_preds").AsTensor<bool>();
        var rowLength = tensor.Dimensions[2];
        var values = tensor.ToArray();
        return Enumerable.Range(0, tensor.Dimensions[1])
            .Select(row => (IReadOnlyList<bool>)values[(row * rowLength)..((row + 1) * rowLength)])
            .ToList();
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
