// wiki: testing#unit-tests
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

public class ModelWarmupServiceTests
{
    private static TtsModelDefinition Model(string name, string engine) =>
        new() { Name = name, Engine = engine, DisplayName = name, Description = string.Empty };

    private static ModelWarmupService<TtsModelDefinition> NewService(
        IModelLookup<TtsModelDefinition> catalog,
        IModelCache cache,
        IEngineWarmer<TtsModelDefinition> warmer,
        ModelCacheOptions options) =>
        new(catalog, cache, new LoggerFactory(), "TTS", warmer, new OptionsWrapper<ModelCacheOptions>(options));

    [Fact]
    public async Task Warms_Every_Ensured_Model_Through_The_Warmer()
    {
        var catalog = new FakeCatalog(Model("kokoro-q4", "kokoro"), Model("af", "supertonic-3"));
        var cache = new FakeCache();
        var warmer = new FakeWarmer("kokoro");

        await NewService(catalog, cache, warmer, new ModelCacheOptions()).RunAsync(CancellationToken.None);

        // The warmer reports false for engines it does not handle, so only the model it owns is warm.
        Assert.Equal(["kokoro-q4"], warmer.Warmed);
    }

    [Fact]
    public async Task Skips_Engine_Warming_When_WarmEngines_Is_Off()
    {
        var catalog = new FakeCatalog(Model("kokoro-q4", "kokoro"));
        var cache = new FakeCache();
        var warmer = new FakeWarmer("kokoro");

        await NewService(catalog, cache, warmer, new ModelCacheOptions { WarmEngines = false }).RunAsync(CancellationToken.None);

        Assert.Empty(warmer.Warmed);
        Assert.True(cache.IsModelEnsured(catalog.GetSupportedModels().First()));
    }

    [Fact]
    public async Task Continues_After_A_Failure_And_Never_Warms_A_Model_That_Was_Not_Ensured()
    {
        var catalog = new FakeCatalog(Model("broken", "kokoro"), Model("kokoro-q4", "kokoro"));
        var cache = new FakeCache(failFor: "broken");
        var warmer = new FakeWarmer("kokoro");

        await NewService(catalog, cache, warmer, new ModelCacheOptions()).RunAsync(CancellationToken.None);

        // The failed model is skipped and the pass keeps going; an engine is never built on top of a
        // partial download.
        Assert.Equal(["kokoro-q4"], warmer.Warmed);
    }

    [Fact]
    public async Task Runs_At_Most_WarmupConcurrency_Models_At_Once()
    {
        var models = Enumerable.Range(0, 6).Select(i => Model($"model-{i}", "kokoro")).ToArray();
        var cache = new FakeCache(delayMs: 60);
        var warmer = new FakeWarmer("kokoro");

        await NewService(new FakeCatalog(models), cache, warmer, new ModelCacheOptions { WarmupConcurrency = 3 }).RunAsync(CancellationToken.None);

        Assert.Equal(3, cache.MaxActive);
        Assert.Equal(6, warmer.Warmed.Count);
    }

    [Fact]
    public async Task Stops_On_Cancellation_Without_Throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cache = new FakeCache(delayMs: 30);
        var warmer = new FakeWarmer("kokoro");

        await NewService(new FakeCatalog(Model("kokoro-q4", "kokoro")), cache, warmer, new ModelCacheOptions()).RunAsync(cts.Token);

        Assert.Empty(warmer.Warmed);
    }

    private sealed class FakeCatalog : IModelLookup<TtsModelDefinition>
    {
        private readonly List<TtsModelDefinition> _models;

        public FakeCatalog(params TtsModelDefinition[] models) => _models = models.ToList();

        public IReadOnlyCollection<TtsModelDefinition> GetSupportedModels() => _models;

        public bool TryGetModel(string modelName, out TtsModelDefinition? model)
        {
            model = _models.FirstOrDefault(m => m.Name == modelName);
            return model is not null;
        }
    }

    private sealed class FakeCache : IModelCache
    {
        private readonly HashSet<string> _ensured = new();
        private readonly HashSet<string> _failFor;
        private readonly int _delayMs;
        private int _active;
        private int _maxActive;

        public FakeCache(string failFor = "", int delayMs = 0)
        {
            _failFor = failFor.Split(',').ToHashSet();
            _delayMs = delayMs;
        }

        public int MaxActive => _maxActive;

        public bool IsModelEnsured(IModelDefinition model)
        {
            lock (this)
            {
                return _ensured.Contains(model.Name);
            }
        }

        public async Task<string> EnsureModelAsync(IModelDefinition model, CancellationToken cancellationToken)
        {
            lock (this)
            {
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
            }

            try
            {
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, cancellationToken);
                }

                if (_failFor.Contains(model.Name))
                {
                    throw new IOException("asset download failed");
                }

                lock (this)
                {
                    _ensured.Add(model.Name);
                }

                return Path.Combine("/tmp/warmup-cache", model.Name);
            }
            finally
            {
                lock (this)
                {
                    _active--;
                }
            }
        }
    }

    private sealed class FakeWarmer : IEngineWarmer<TtsModelDefinition>
    {
        private readonly HashSet<string> _handles;
        private readonly HashSet<string> _throwFor;

        public FakeWarmer(string handles, string throwFor = "")
        {
            _handles = handles.Split(',').ToHashSet();
            _throwFor = throwFor.Split(',').ToHashSet();
        }

        public List<string> Warmed { get; } = new();

        public Task<bool> WarmUpAsync(TtsModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
        {
            if (!_handles.Contains(model.Engine))
            {
                return Task.FromResult(false);
            }

            if (_throwFor.Contains(model.Name))
            {
                throw new InvalidOperationException($"engine build failed for {model.Name}");
            }

            // Warmup calls the warmer from several models at the same time.
            lock (Warmed)
            {
                Warmed.Add(model.Name);
            }

            return Task.FromResult(true);
        }
    }
}
