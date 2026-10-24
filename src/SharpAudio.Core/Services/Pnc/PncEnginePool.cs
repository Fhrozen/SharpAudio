// wiki: pnc#wiring
using Microsoft.ML.OnnxRuntime;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services.Pnc;

/// <summary>Pool contract so the punctuating transcriber can be tested without loading ONNX.</summary>
public interface IPncEnginePool : IIdleTracking, IDisposable
{
    IPncEngine GetOrCreateEngine(PncModelDefinition model, string modelDirectory);
}

/// <summary>Pool of <see cref="IPncEngine"/> instances, one per model, built lazily from the model
/// directory and released when idle. The storage itself is the shared <see cref="EnginePool{T}"/>
/// used by the TTS and ASR transcribers; this class only supplies the PnC engine factory.</summary>
public sealed class PncEnginePool : IPncEnginePool
{
    private readonly EnginePool<IPncEngine> _pool = new();

    public IPncEngine GetOrCreateEngine(PncModelDefinition model, string modelDirectory)
    {
        return _pool.GetOrCreate($"{model.Engine}:{model.Name}", () => CreateEngine(model, modelDirectory));
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _pool.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _pool.TryReleaseEngine(engineKey);

    private static IPncEngine CreateEngine(PncModelDefinition model, string modelDirectory)
    {
        var config = PncConfigReader.LoadFile(Path.Combine(modelDirectory, model.ConfigPath));
        var tokenizer = UnigramSentencePieceTokenizer.FromFile(Path.Combine(modelDirectory, model.TokenizerPath));
        var session = new InferenceSession(Path.Combine(modelDirectory, model.OnnxPath));
        return new PncEngine(tokenizer, new OnnxPncPredictor(session, tokenizer, config), config);
    }

    public void Dispose() => _pool.Dispose();
}
