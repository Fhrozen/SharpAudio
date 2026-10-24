// wiki: testing#unit-tests
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

public class IdleTrackingAggregateTests
{
    private class FakeSource(string key, bool canRelease) : IIdleTracking
    {
        public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() =>
            new Dictionary<string, DateTime> { [key] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        public bool TryReleaseEngine(string engineKey) => canRelease && engineKey == key;
    }

    private sealed class DisposableSource(string key) : IIdleTracking, IDisposable
    {
        public bool Disposed { get; private set; }

        public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() =>
            new Dictionary<string, DateTime> { [key] = DateTime.UtcNow };

        public bool TryReleaseEngine(string engineKey) => engineKey == key;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void GetLoadedEngines_merges_every_source()
    {
        var aggregate = new IdleTrackingAggregate(new FakeSource("inner", true), new FakeSource("pnc:pnc-en", true));

        var engines = aggregate.GetLoadedEngines();

        Assert.Equal(2, engines.Count);
        Assert.True(engines.ContainsKey("inner"));
        Assert.True(engines.ContainsKey("pnc:pnc-en"));
    }

    [Fact]
    public void Duplicate_keys_are_resolved_by_source_order()
    {
        var first = new FakeSource("shared", true);
        var second = new FakeSource("shared", true);
        var aggregate = new IdleTrackingAggregate(first, second);

        Assert.Equal(second.GetLoadedEngines()["shared"], aggregate.GetLoadedEngines()["shared"]);
    }

    [Fact]
    public void TryReleaseEngine_tries_sources_in_order()
    {
        var aggregate = new IdleTrackingAggregate(new FakeSource("inner", false), new FakeSource("pnc:pnc-en", true));

        Assert.True(aggregate.TryReleaseEngine("pnc:pnc-en"));
        Assert.False(aggregate.TryReleaseEngine("inner"));
        Assert.False(aggregate.TryReleaseEngine("other"));
    }

    [Fact]
    public void Dispose_forwards_only_to_disposable_sources()
    {
        var disposable = new DisposableSource("pnc:pnc-en");
        var aggregate = new IdleTrackingAggregate(new FakeSource("inner", true), disposable);

        aggregate.Dispose();

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public void An_empty_aggregate_is_harmless()
    {
        var aggregate = new IdleTrackingAggregate();

        Assert.Empty(aggregate.GetLoadedEngines());
        Assert.False(aggregate.TryReleaseEngine("anything"));
    }
}
