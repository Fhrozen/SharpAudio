// wiki: pnc#wiring
using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>Pool contract so the punctuating transcriber can be tested without loading ONNX.</summary>
public interface IPncEnginePool : IDisposable
{
    IPncEngine GetOrCreateEngine(PncModelDefinition model, string modelDirectory);
    IReadOnlyDictionary<string, DateTime> GetLoadedEngines();
    bool TryReleaseEngine(string engineKey);
}

/// <summary>Pool of <see cref="IPncEngine"/> instances, one per model (mirrors the engine pool in
/// <see cref="SupertonicTtsSynthesizer"/>). Engines are built lazily from the model directory and can be
/// released when idle.</summary>
public sealed class PncEnginePool : IPncEnginePool
{
    private readonly ConcurrentDictionary<string, IPncEngine> _engines = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessTimes = new();
    private readonly object _engineLock = new();
    private bool _disposed;

    public IPncEngine GetOrCreateEngine(PncModelDefinition model, string modelDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = $"{model.Engine}:{model.Name}";
        lock (_engineLock)
        {
            var engine = _engines.GetOrAdd(key, _ => CreateEngine(model, modelDirectory));
            _lastAccessTimes[key] = DateTime.UtcNow;
            return engine;
        }
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines()
    {
        return _lastAccessTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public bool TryReleaseEngine(string engineKey)
    {
        lock (_engineLock)
        {
            if (_engines.TryRemove(engineKey, out var engine))
            {
                try
                {
                    engine.Dispose();
                    _lastAccessTimes.TryRemove(engineKey, out _);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error disposing PnC engine '{engineKey}': {ex.Message}");
                    return false;
                }
            }

            return false;
        }
    }

    private static IPncEngine CreateEngine(PncModelDefinition model, string modelDirectory)
    {
        var config = PncConfigReader.LoadFile(Path.Combine(modelDirectory, model.ConfigPath));
        var tokenizer = UnigramSentencePieceTokenizer.FromFile(Path.Combine(modelDirectory, model.TokenizerPath));
        var session = new InferenceSession(Path.Combine(modelDirectory, model.OnnxPath));
        return new PncEngine(tokenizer, new OnnxPncPredictor(session, tokenizer, config), config);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }

        _engines.Clear();
        _lastAccessTimes.Clear();
    }
}
