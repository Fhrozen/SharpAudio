// wiki: worker-core#single-active-engine-pool
namespace SharpAudio.Core.Worker;

/// <summary>
/// Holds at most one model engine at a time (the "single active engine" rule): loading a
/// different model disposes the engines of every other kind. Entries are keyed by engine
/// kind, so a kind that is exempt from eviction (e.g. the Silero VAD engine, whose state is
/// independent of the ASR model) can stay loaded alongside the active model.
///
/// Model loading is the expensive part, so it happens outside the lock: a caller that misses
/// the cache builds the engine first and only then installs it. If another caller installed
/// an engine for the same kind and model in the meantime, the freshly built engine is
/// discarded. The lock therefore protects the map only, not inference.
/// </summary>
public sealed class SingleActiveEnginePool : IDisposable
{
    private sealed record Entry(string ModelKey, object Engine);

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly HashSet<string> _pinnedKinds = new();
    private readonly object _lock = new();
    private string? _loadedModelKey;
    private bool _disposed;

    /// <summary>
    /// Key of the currently active model, as reported by the worker HealthCheck RPC.
    /// </summary>
    public string? LoadedModelKey
    {
        get
        {
            lock (_lock)
            {
                return _loadedModelKey;
            }
        }
    }

    /// <summary>
    /// Return the engine for <paramref name="kind"/>, creating it when it is not already
    /// loaded for <paramref name="modelKey"/>.
    /// </summary>
    /// <param name="kind">Engine kind, e.g. "kokoro", "whisper", "vad".</param>
    /// <param name="modelKey">Cache key identifying the model instance.</param>
    /// <param name="create">Factory that builds the engine. Called outside the lock.</param>
    /// <param name="evictOthers">
    /// When true, every other kind is disposed and this kind becomes the active model.
    /// Pass false for engines that coexist with the active model (VAD).
    /// </param>
    public T GetOrCreate<T>(string kind, string modelKey, Func<T> create, bool evictOthers = true)
        where T : class
    {
        lock (_lock)
        {
            if (TryGet<T>(kind, modelKey, out var cached))
            {
                return cached;
            }
        }

        var created = create();

        lock (_lock)
        {
            if (_disposed)
            {
                (created as IDisposable)?.Dispose();
                throw new ObjectDisposedException(nameof(SingleActiveEnginePool));
            }

            if (TryGet<T>(kind, modelKey, out var raced))
            {
                (created as IDisposable)?.Dispose();
                return raced;
            }

            if (evictOthers)
            {
                foreach (var otherKind in _entries.Keys.ToList())
                {
                    if (otherKind == kind || _pinnedKinds.Contains(otherKind))
                    {
                        continue;
                    }

                    (_entries[otherKind].Engine as IDisposable)?.Dispose();
                    _entries.Remove(otherKind);
                }

                _loadedModelKey = modelKey;
            }
            else
            {
                _pinnedKinds.Add(kind);
            }

            if (_entries.TryGetValue(kind, out var previous))
            {
                (previous.Engine as IDisposable)?.Dispose();
            }

            _entries[kind] = new Entry(modelKey, created);
            return created;
        }
    }

    private bool TryGet<T>(string kind, string modelKey, out T engine) where T : class
    {
        if (_entries.TryGetValue(kind, out var entry) && entry.ModelKey == modelKey && entry.Engine is T typed)
        {
            engine = typed;
            return true;
        }

        engine = null!;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _disposed = true;

            foreach (var entry in _entries.Values)
            {
                (entry.Engine as IDisposable)?.Dispose();
            }

            _entries.Clear();
            _pinnedKinds.Clear();
            _loadedModelKey = null;
        }
    }
}
