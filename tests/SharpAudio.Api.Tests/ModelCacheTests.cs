// wiki: testing#unit-tests
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

public sealed class ModelCacheTests
{
    private sealed class TestModel : IVoiceDownloadable
    {
        public string Name { get; init; } = "test-model";
        public string DisplayName { get; init; } = "Test model";
        public string Description { get; init; } = "Test model";
        public string Engine { get; init; } = "whisper";
        public IReadOnlyList<ModelAsset> Assets { get; init; } = [];
        public IReadOnlyList<string> SupportedLanguages { get; init; } = [];
        public string? VoicesPath { get; init; }
        public string? VoicesBaseUrl { get; init; }
        public string VoiceFileExtension { get; init; } = ".bin";
        public IReadOnlyList<string> Speakers { get; init; } = [];
    }

    /// <summary>Fails for any URL containing <see cref="FailFor"/>, so a partial download can be
    /// produced on demand. Successful responses carry a few bytes of body.</summary>
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly string _failFor;

        public FakeHttpClientFactory(string failFor = "") => _failFor = failFor;

        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient(new FakeHandler(_failFor));
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly string _failFor;

            public FakeHandler(string failFor) => _failFor = failFor;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_failFor.Length > 0 && request.RequestUri?.ToString().Contains(_failFor) == true)
                {
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                });
            }
        }
    }

    private static (ModelCache Cache, string Root, FakeHttpClientFactory Factory) NewCache(string failFor = "")
    {
        var root = Path.Combine(Path.GetTempPath(), "sharp-audio-model-cache-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var factory = new FakeHttpClientFactory(failFor);
        var cache = new ModelCache(factory, Options.Create(new ModelCacheOptions { CacheDirectory = root }), NullLogger<ModelCache>.Instance);
        return (cache, root, factory);
    }

    private static ModelAsset Asset(string name) => new() { RelativePath = name, Url = $"https://example.invalid/{name}" };

    [Fact]
    public async Task EnsureModelAsync_does_not_create_an_http_client_when_every_asset_is_already_cached()
    {
        var (cache, root, factory) = NewCache();
        var model = new TestModel { Assets = [Asset("a.onnx"), Asset("b.onnx")] };
        Directory.CreateDirectory(Path.Combine(root, model.Name));
        foreach (var asset in model.Assets)
        {
            File.WriteAllText(Path.Combine(root, model.Name, asset.RelativePath), "cached");
        }

        var directory = await cache.EnsureModelAsync(model, CancellationToken.None);

        Assert.Equal(Path.Combine(root, model.Name), directory);
        Assert.Equal(0, factory.CreateClientCalls);
    }

    [Fact]
    public async Task A_model_that_was_ensured_once_is_answered_from_memory_without_rechecking_the_disk()
    {
        var (cache, root, factory) = NewCache();
        var model = new TestModel { Assets = [Asset("a.onnx")] };

        var first = await cache.EnsureModelAsync(model, CancellationToken.None);
        Assert.Equal(1, factory.CreateClientCalls);

        // The memoized answer is returned even though the file is gone, which is the documented
        // trade-off of the fast path.
        File.Delete(Path.Combine(root, model.Name, "a.onnx"));

        var second = await cache.EnsureModelAsync(model, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, factory.CreateClientCalls);
    }

    [Fact]
    public async Task A_partial_download_is_not_recorded_as_ensured_and_is_retried_on_the_next_call()
    {
        var (cache, root, factory) = NewCache(failFor: "bad.onnx");
        var model = new TestModel { Assets = [Asset("good.onnx"), Asset("bad.onnx")] };

        await cache.EnsureModelAsync(model, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root, model.Name, "good.onnx")));
        Assert.False(File.Exists(Path.Combine(root, model.Name, "bad.onnx")));
        Assert.Equal(1, factory.CreateClientCalls);

        // Nothing was recorded as ensured, so the next call goes back to the network for the
        // single missing asset.
        await cache.EnsureModelAsync(model, CancellationToken.None);

        Assert.Equal(2, factory.CreateClientCalls);
        Assert.False(File.Exists(Path.Combine(root, model.Name, "bad.onnx")));
    }

    [Fact]
    public async Task Voice_files_are_not_requested_for_a_model_that_has_no_voice_hook()
    {
        var (cache, root, factory) = NewCache();
        var model = new TestModel
        {
            Engine = "whisper",
            Assets = [Asset("a.onnx")],
            VoicesPath = "voices",
            VoicesBaseUrl = "https://example.invalid/voices",
            Speakers = ["af"],
        };

        await cache.EnsureModelAsync(model, CancellationToken.None);

        // The asset download needs a client, but no voice file is fetched because whisper is not a
        // voice-download engine.
        Assert.Equal(1, factory.CreateClientCalls);
        Assert.False(Directory.Exists(Path.Combine(root, model.Name, "voices")));
    }
}
