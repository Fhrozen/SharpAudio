// wiki: backend-architecture#core-abstractions
namespace SharpAudio.Core.Services;

/// <summary>
/// Combines several <see cref="IIdleTracking"/> sources into one view so the idle monitor sees a single
/// map regardless of how many engines a router or decorator owns. Release is attempted on each source in
/// order, and disposal forwards to whichever sources are disposable.
/// </summary>
public sealed class IdleTrackingAggregate : IIdleTracking, IDisposable
{
    private readonly IIdleTracking[] _sources;

    public IdleTrackingAggregate(params IIdleTracking[] sources) => _sources = sources;

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines()
    {
        var engines = new Dictionary<string, DateTime>();
        foreach (var source in _sources)
        {
            foreach (var (key, lastAccessAt) in source.GetLoadedEngines())
            {
                engines[key] = lastAccessAt;
            }
        }

        return engines;
    }

    public bool TryReleaseEngine(string engineKey)
    {
        foreach (var source in _sources)
        {
            if (source.TryReleaseEngine(engineKey))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
