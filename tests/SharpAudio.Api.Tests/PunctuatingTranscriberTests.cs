// wiki: testing#unit-tests
using SharpAudio.Api.Contracts;
using SharpAudio.Api.Models;
using SharpAudio.Api.Services;
using SharpAudio.Api.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PunctuatingTranscriberTests
{
    [Fact]
    public async Task TranscribeAsync_punctuates_when_requested()
    {
        var inner = new FakeInner(new TranscriptionResult("hi there", null, 1.0, 2.0, 8, null));
        var pool = new FakePool(text => [char.ToUpper(text[0]) + text[1..] + "."]);
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), pool);

        var result = await transcriber.TranscribeAsync(
            new AsrModelDefinition { Name = "whisper-base" },
            "/tmp/models",
            new AudioTranscriptionRequest { Punctuate = true },
            [],
            CancellationToken.None);

        Assert.Equal("Hi there.", result.Text);
        Assert.Equal(9, result.CharacterCount);
        Assert.True(inner.WasCalled);
        Assert.Equal("pnc-en", pool.CreatedModel?.Name);
    }

    [Fact]
    public async Task TranscribeAsync_returns_the_inner_result_when_not_requested()
    {
        var inner = new FakeInner(new TranscriptionResult("hi there", null, 1.0, 2.0, 8, null));
        var pool = new FakePool(text => [text]);
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), pool);

        var result = await transcriber.TranscribeAsync(
            new AsrModelDefinition { Name = "whisper-base" },
            "/tmp/models",
            new AudioTranscriptionRequest(),
            [],
            CancellationToken.None);

        Assert.Equal("hi there", result.Text);
        Assert.Null(pool.CreatedModel);
    }

    [Fact]
    public async Task Streaming_sessions_pass_through_unchanged()
    {
        var inner = new FakeInner(new TranscriptionResult("hi", null, 1.0, 1.0, 2, null));
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), new FakePool(_ => []));

        var session = await transcriber.CreateStreamingSessionAsync(
            new AsrModelDefinition { Name = "whisper-base" },
            "/tmp/models",
            null,
            false,
            false,
            2.0,
            CancellationToken.None);

        Assert.Same(inner.LastSession, session);
    }

    [Fact]
    public void GetLoaded_engines_merges_inner_and_pool()
    {
        var inner = new FakeInner(new TranscriptionResult("hi", null, 1.0, 1.0, 2, null));
        var pool = new FakePool(_ => []);
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), pool);

        var engines = transcriber.GetLoadedEngines();

        Assert.True(engines.ContainsKey("inner"));
        Assert.True(engines.ContainsKey("pnc:pnc-en"));
    }

    [Fact]
    public void TryRelease_engine_prefers_the_pool_then_falls_back_to_the_inner_transcriber()
    {
        var inner = new FakeInner(new TranscriptionResult("hi", null, 1.0, 1.0, 2, null));
        var pool = new FakePool(_ => []);
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), pool);

        Assert.True(transcriber.TryReleaseEngine("pnc:pnc-en"));
        Assert.True(transcriber.TryReleaseEngine("inner"));
        Assert.False(transcriber.TryReleaseEngine("other"));
    }

    [Fact]
    public void Dispose_disposes_the_pool_and_the_inner_transcriber()
    {
        var inner = new FakeInner(new TranscriptionResult("hi", null, 1.0, 1.0, 2, null));
        var pool = new FakePool(_ => []);
        var transcriber = new PunctuatingTranscriber(inner, new FakeCatalog(), new FakeCache(), pool);

        transcriber.Dispose();

        Assert.True(inner.WasDisposed);
        Assert.True(pool.WasDisposed);
    }

    private static PncModelDefinition PncModel() => new() { Name = "pnc-en", Engine = "pnc" };

    private sealed class FakeCatalog : IPncModelCatalog
    {
        public IReadOnlyCollection<PncModelDefinition> GetSupportedModels() => [PncModel()];

        public bool TryGetModel(string modelName, out PncModelDefinition? model)
        {
            model = string.IsNullOrEmpty(modelName) ? PncModel() : null;
            return model is not null;
        }
    }

    private sealed class FakeCache : IModelCache
    {
        public Task<string> EnsureModelAsync(TtsModelDefinition model, CancellationToken cancellationToken) => Task.FromResult("/tmp/models");

        public Task<string> EnsureModelAsync(AsrModelDefinition model, CancellationToken cancellationToken) => Task.FromResult("/tmp/models");

        public Task<string> EnsureModelAsync(PncModelDefinition model, CancellationToken cancellationToken) => Task.FromResult("/tmp/models");
    }

    private sealed class FakePool(Func<string, IReadOnlyList<string>> apply) : IPncEnginePool
    {
        public PncModelDefinition? CreatedModel { get; private set; }

        public bool WasDisposed { get; private set; }

        public IPncEngine GetOrCreateEngine(PncModelDefinition model, string modelDirectory)
        {
            CreatedModel = model;
            return new FakeEngine(apply);
        }

        public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => new Dictionary<string, DateTime> { ["pnc:pnc-en"] = DateTime.UtcNow };

        public bool TryReleaseEngine(string engineKey) => engineKey.StartsWith("pnc:");

        public void Dispose() => WasDisposed = true;
    }

    private sealed class FakeInner(TranscriptionResult result) : IAsrTranscriber, IIdleTrackingTranscriber, IDisposable
    {
        public bool WasCalled { get; private set; }

        public bool WasDisposed { get; private set; }

        public IStreamingTranscriptionSession LastSession { get; } = new FakeSession();

        public Task<TranscriptionResult> TranscribeAsync(
            AsrModelDefinition model,
            string modelDirectory,
            AudioTranscriptionRequest request,
            byte[] audioBytes,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }

        public Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
            AsrModelDefinition model,
            string modelDirectory,
            string? language,
            bool enableVad,
            bool enableEndpointing,
            double segmentSeconds,
            CancellationToken cancellationToken) => Task.FromResult(LastSession);

        public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => new Dictionary<string, DateTime> { ["inner"] = DateTime.UtcNow };

        public bool TryReleaseEngine(string engineKey) => engineKey == "inner";

        public void Dispose() => WasDisposed = true;
    }

    private sealed class FakeSession : IStreamingTranscriptionSession
    {
        public int SampleRate => 16000;

        public Task<IReadOnlyList<StreamingUpdate>> ProcessChunkAsync(byte[] pcm16Chunk, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<StreamingUpdate>>([]);

        public Task<string> FinishAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public void Dispose() { }
    }

    private sealed class FakeEngine(Func<string, IReadOnlyList<string>> apply) : IPncEngine
    {
        public PncModelConfig Config { get; } = new() { PreLabels = ["<NULL>"], PostLabels = ["<NULL>"] };

        public IReadOnlyList<string> Apply(string text) => apply(text);

        public void Dispose() { }
    }
}
