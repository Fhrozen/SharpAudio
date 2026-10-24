// wiki: backend-architecture#core-abstractions
namespace SharpAudio.Core.Services;

/// <summary>
/// The shared shape of anything that owns pooled engines: TTS synthesizers, ASR transcribers, the PnC
/// pool and the aggregates that combine them. The idle monitor works against this interface alone, so
/// a new task type needs no new idle-tracking interface.
/// </summary>
public interface IIdleTracking
{
    /// <summary>Currently tracked engine keys and their last access times.</summary>
    IReadOnlyDictionary<string, DateTime> GetLoadedEngines();

    /// <summary>Attempts to release an idle engine by its cache key.</summary>
    bool TryReleaseEngine(string engineKey);
}
