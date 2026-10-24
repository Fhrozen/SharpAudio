// wiki: testing#unit-tests
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

public sealed class EnginePoolTests
{
    private sealed class FakeEngine : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class PlainEngine;

    [Fact]
    public void GetOrCreate_builds_the_engine_once_and_reuses_it()
    {
        var pool = new EnginePool<FakeEngine>();
        var builds = 0;

        var first = pool.GetOrCreate("kokoro:a", () =>
        {
            builds++;
            return new FakeEngine();
        });
        var second = pool.GetOrCreate("kokoro:a", () =>
        {
            builds++;
            return new FakeEngine();
        });

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Engines_are_keyed_independently()
    {
        var pool = new EnginePool<FakeEngine>();

        var a = pool.GetOrCreate("kokoro:a", () => new FakeEngine());
        var b = pool.GetOrCreate("kokoro:b", () => new FakeEngine());

        Assert.NotSame(a, b);
    }

    [Fact]
    public void Engine_built_while_another_caller_installed_the_same_key_is_discarded()
    {
        var pool = new EnginePool<FakeEngine>();
        var built = new FakeEngine();

        var winner = pool.GetOrCreate("kokoro:a", () =>
        {
            // simulate a second caller installing the same key while this engine was being built
            pool.GetOrCreate("kokoro:a", () => new FakeEngine());
            return built;
        });

        Assert.NotSame(built, winner);
        Assert.True(built.Disposed);
    }

    [Fact]
    public void GetLoadedEngines_reports_pooled_keys_and_touches_them_on_reuse()
    {
        var pool = new EnginePool<FakeEngine>();
        pool.GetOrCreate("pnc:pnc-en", () => new FakeEngine());

        var loaded = pool.GetLoadedEngines();
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("pnc:pnc-en"));

        var before = loaded["pnc:pnc-en"];
        pool.GetOrCreate("pnc:pnc-en", () => new FakeEngine());
        Assert.True(pool.GetLoadedEngines()["pnc:pnc-en"] >= before);
    }

    [Fact]
    public void TryReleaseEngine_disposes_and_removes_the_engine()
    {
        var pool = new EnginePool<FakeEngine>();
        var engine = pool.GetOrCreate("whisper:large", () => new FakeEngine());

        Assert.True(pool.TryReleaseEngine("whisper:large"));
        Assert.True(engine.Disposed);
        Assert.Empty(pool.GetLoadedEngines());
        Assert.False(pool.TryReleaseEngine("whisper:large"));
    }

    [Fact]
    public void Non_disposable_engine_types_are_supported()
    {
        var pool = new EnginePool<PlainEngine>();

        var engine = pool.GetOrCreate("vad:silero", () => new PlainEngine());

        Assert.NotNull(engine);
        Assert.True(pool.TryReleaseEngine("vad:silero"));
        Assert.Empty(pool.GetLoadedEngines());
    }

    [Fact]
    public void Dispose_disposes_every_engine_and_rejects_further_loads()
    {
        var pool = new EnginePool<FakeEngine>();
        var first = pool.GetOrCreate("kokoro:a", () => new FakeEngine());
        var second = pool.GetOrCreate("supertonic:b", () => new FakeEngine());

        pool.Dispose();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        var exception = Assert.Throws<ObjectDisposedException>(() => pool.GetOrCreate("kokoro:c", () => new FakeEngine()));
        Assert.Contains(nameof(EnginePool<FakeEngine>), exception.Message);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var pool = new EnginePool<FakeEngine>();
        var engine = pool.GetOrCreate("kokoro:a", () => new FakeEngine());

        pool.Dispose();
        pool.Dispose();

        Assert.True(engine.Disposed);
    }
}
