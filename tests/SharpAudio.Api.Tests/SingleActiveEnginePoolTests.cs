// wiki: testing#unit-tests
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

public sealed class SingleActiveEnginePoolTests
{
    private sealed class FakeEngine : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    [Fact]
    public void GetOrCreate_reuses_the_engine_for_the_same_kind_and_model()
    {
        var pool = new SingleActiveEnginePool();
        var creates = 0;

        var first = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () =>
        {
            creates++;
            return new FakeEngine();
        });

        var second = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () =>
        {
            creates++;
            return new FakeEngine();
        });

        Assert.Same(first, second);
        Assert.Equal(1, creates);
    }

    [Fact]
    public void GetOrCreate_disposes_the_previous_model_when_a_different_model_is_loaded()
    {
        var pool = new SingleActiveEnginePool();

        var whisper = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());
        var nemotron = pool.GetOrCreate<FakeEngine>("nemotron-3.5", "nemotron-3.5:n", () => new FakeEngine());

        Assert.True(whisper.Disposed);
        Assert.False(nemotron.Disposed);
        Assert.Equal("nemotron-3.5:n", pool.LoadedModelKey);
    }

    [Fact]
    public void GetOrCreate_reloads_a_model_that_is_not_the_active_one()
    {
        var pool = new SingleActiveEnginePool();

        var whisper = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());
        pool.GetOrCreate<FakeEngine>("nemotron-3.5", "nemotron-3.5:n", () => new FakeEngine());

        var reloaded = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());

        Assert.NotSame(whisper, reloaded);
        Assert.Equal("whisper:m", pool.LoadedModelKey);
    }

    [Fact]
    public void Pinned_kind_survives_a_model_switch_and_does_not_report_itself_as_active()
    {
        var pool = new SingleActiveEnginePool();

        var vad = pool.GetOrCreate<FakeEngine>("vad", "vad:/models/silero_vad.onnx", () => new FakeEngine(), evictOthers: false);
        var whisper = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());

        Assert.False(vad.Disposed);
        Assert.Same(vad, pool.GetOrCreate<FakeEngine>("vad", "vad:/models/silero_vad.onnx", () => new FakeEngine(), evictOthers: false));
        Assert.Equal("whisper:m", pool.LoadedModelKey);
        Assert.Same(whisper, pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine()));
    }

    [Fact]
    public void Pinned_kind_is_reloaded_and_old_engine_disposed_when_its_model_changes()
    {
        var pool = new SingleActiveEnginePool();

        var first = pool.GetOrCreate<FakeEngine>("vad", "vad:/a/silero_vad.onnx", () => new FakeEngine(), evictOthers: false);
        var second = pool.GetOrCreate<FakeEngine>("vad", "vad:/b/silero_vad.onnx", () => new FakeEngine(), evictOthers: false);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Engine_built_while_another_caller_installed_the_same_model_is_discarded()
    {
        var pool = new SingleActiveEnginePool();
        var loser = new FakeEngine();
        var winner = new FakeEngine();

        var returned = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () =>
        {
            // Stand-in for a second thread that installed the same kind and model first.
            pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => winner);
            return loser;
        });

        Assert.Same(winner, returned);
        Assert.True(loser.Disposed);
    }

    [Fact]
    public void Dispose_disposes_every_loaded_engine_and_rejects_further_loads()
    {
        var pool = new SingleActiveEnginePool();
        var whisper = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());
        var vad = pool.GetOrCreate<FakeEngine>("vad", "vad:/models/silero_vad.onnx", () => new FakeEngine(), evictOthers: false);

        pool.Dispose();

        Assert.True(whisper.Disposed);
        Assert.True(vad.Disposed);
        Assert.Null(pool.LoadedModelKey);

        var exception = Assert.Throws<ObjectDisposedException>(() =>
            pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine()));
        Assert.Contains(nameof(SingleActiveEnginePool), exception.Message);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var pool = new SingleActiveEnginePool();
        var engine = pool.GetOrCreate<FakeEngine>("whisper", "whisper:m", () => new FakeEngine());

        pool.Dispose();
        pool.Dispose();

        Assert.True(engine.Disposed);
    }
}
