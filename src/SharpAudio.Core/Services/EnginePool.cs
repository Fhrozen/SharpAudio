// wiki: backend-architecture#core-abstractions
namespace SharpAudio.Core.Services;

/// <summary>
/// The repo's single lazily-built engine pool: one engine per cache key, created on first use,
/// timestamped for idle monitoring, individually releasable, and disposed on <see cref="Dispose"/>.
/// Used by the TTS/ASR transcribers, the PnC pool and the ASR worker service.
/// Engines are built outside the pool lock so a slow model load does not block other callers; if two
/// callers race for the same key, the first engine installed wins and the extra one is discarded.
/// Implements <see cref="IIdleTracking"/> so a pool can be handed straight to the idle monitor or to an
/// <see cref="IdleTrackingAggregate"/>.
/// </summary>
/// <typeparam name="T">The engine type. Engines are disposed on release when they are
/// <see cref="IDisposable"/>, so non-disposable engine contracts are supported too.</typeparam>
public sealed class EnginePool<T> : IIdleTracking, IDisposable where T : class
{
    private sealed class Entry
    {
        public required T Engine { get; init; }
        public DateTime LastAccessAt { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Returns the engine for <paramref name="key"/>, building it with
    /// <paramref name="create"/> on first use.</summary>
    public T GetOrCreate(string key, Func<T> create)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastAccessAt = DateTime.UtcNow;
                return existing.Engine;
            }
        }

        var created = create();

        lock (_lock)
        {
            if (_disposed)
            {
                (created as IDisposable)?.Dispose();
                throw new ObjectDisposedException(nameof(EnginePool<T>));
            }

            if (_entries.TryGetValue(key, out var raced))
            {
                (created as IDisposable)?.Dispose();
                raced.LastAccessAt = DateTime.UtcNow;
                return raced.Engine;
            }

            _entries[key] = new Entry { Engine = created, LastAccessAt = DateTime.UtcNow };
            return created;
        }
    }

    /// <summary>Snapshot of the currently pooled engine keys and their last access times, used by the
    /// idle monitor services.</summary>
    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines()
    {
        lock (_lock)
        {
            return _entries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.LastAccessAt);
        }
    }

    /// <summary>Removes and disposes the engine for <paramref name="key"/>. Returns false when the key
    /// is not pooled or the engine could not be disposed.</summary>
    public bool TryReleaseEngine(string key)
    {
        lock (_lock)
        {
            if (!_entries.Remove(key, out var entry))
            {
                return false;
            }

            try
            {
                (entry.Engine as IDisposable)?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error disposing engine '{key}': {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var (key, entry) in _entries)
            {
                try
                {
                    (entry.Engine as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error disposing engine '{key}': {ex.Message}");
                }
            }

            _entries.Clear();
        }
    }
}
