// wiki: backend-architecture#model-cache-and-warmup
using System.Collections.Concurrent;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using Microsoft.Extensions.Options;

namespace SharpAudio.Core.Services;

/// <summary>Downloads model assets into the cache directory on first use. A model that has been
/// fully ensured once is answered from memory, so the per-request path does not stat every asset
/// again; an <see cref="IHttpClientFactory"/> client is only created when something is actually
/// missing. Only complete ensures are recorded, so a partial failure is retried on the next call.</summary>
public sealed class ModelCache : IModelCache
{
    private const string KokoroEngine = "kokoro";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheDirectory;
    private readonly ILogger<ModelCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new();
    private readonly ConcurrentDictionary<string, string> _ensuredDirectories = new();

    public ModelCache(IHttpClientFactory httpClientFactory, IOptions<ModelCacheOptions> options, ILogger<ModelCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheDirectory = options.Value.CacheDirectory;
        _logger = logger;
    }

    public bool IsModelEnsured(IModelDefinition model) => _ensuredDirectories.ContainsKey(model.Name);

    public async Task<string> EnsureModelAsync(IModelDefinition model, CancellationToken cancellationToken)
    {
        // Fast path: a model that has already been fully ensured is answered from memory. The
        // trade-off is that files deleted from the cache after the first ensure are not noticed
        // again for the life of the process.
        if (_ensuredDirectories.TryGetValue(model.Name, out var ensured))
        {
            return ensured;
        }

        var targetDirectory = Path.Combine(_cacheDirectory, model.Name);

        // Serialize concurrent ensure-calls for the same model (e.g. background warmup racing an
        // on-demand request) so they don't write the same asset files at the same time.
        var modelLock = _modelLocks.GetOrAdd(model.Name, _ => new SemaphoreSlim(1, 1));
        await modelLock.WaitAsync(cancellationToken);
        try
        {
            if (_ensuredDirectories.TryGetValue(model.Name, out var raced))
            {
                return raced;
            }

            Directory.CreateDirectory(targetDirectory);

            var missingAssets = model.Assets.Where(asset => !File.Exists(Path.Combine(targetDirectory, asset.RelativePath))).ToList();
            var voice = model as IVoiceDownloadable;
            var voicePlan = voice is not null && IsVoiceDownloadEngine(model.Engine)
                ? MissingVoiceFiles(voice, targetDirectory)
                : null;

            if (missingAssets.Count == 0 && voicePlan is null)
            {
                _ensuredDirectories[model.Name] = targetDirectory;
                return targetDirectory;
            }

            // Only reach for an HTTP client when there is something to fetch.
            var client = _httpClientFactory.CreateClient();

            var complete = missingAssets.Count == 0
                || await DownloadAssetsAsync(client, targetDirectory, model.Name, missingAssets, cancellationToken);

            if (voicePlan is not null)
            {
                complete &= await DownloadVoiceFiles(client, model.Name, voicePlan, cancellationToken);
            }

            if (complete)
            {
                _ensuredDirectories[model.Name] = targetDirectory;
            }

            return targetDirectory;
        }
        finally
        {
            modelLock.Release();
        }
    }

    private static bool IsVoiceDownloadEngine(string engine) =>
        string.Equals(engine, KokoroEngine, StringComparison.OrdinalIgnoreCase)
        || SupertonicMetadata.IsSupertonic3Engine(engine);

    private async Task<bool> DownloadAssetsAsync(
        HttpClient client, string targetDirectory, string modelName,
        IReadOnlyList<ModelAsset> missingAssets, CancellationToken cancellationToken)
    {
        var complete = true;

        foreach (var asset in missingAssets)
        {
            var outputPath = Path.Combine(targetDirectory, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            try
            {
                var tempPath = $"{outputPath}.tmp";
                await using var stream = await client.GetStreamAsync(asset.Url, cancellationToken);
                await using (var output = File.Create(tempPath))
                {
                    await stream.CopyToAsync(output, cancellationToken);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                var tempPath = $"{outputPath}.tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                _logger.LogWarning(ex, "Failed to download model file {File} for {Model}", asset.RelativePath, modelName);
                complete = false;
            }
        }

        return complete;
    }

    /// <summary>Voice files that are not on disk yet, or null when there is nothing to fetch, so a
    /// warm cache does not need an HTTP client. The paths are validated here once, so the download
    /// loop works with non-null values.</summary>
    private static VoiceDownloadPlan? MissingVoiceFiles(IVoiceDownloadable model, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(model.VoicesPath) || string.IsNullOrWhiteSpace(model.VoicesBaseUrl))
        {
            return null;
        }

        var voicesDirectory = Path.Combine(targetDirectory, model.VoicesPath);
        var extension = string.IsNullOrWhiteSpace(model.VoiceFileExtension) ? ".bin" : model.VoiceFileExtension;

        var files = model.Speakers
            .Select(speaker => $"{speaker}{extension}")
            .Where(fileName => !File.Exists(Path.Combine(voicesDirectory, fileName)))
            .ToList();

        return files.Count == 0 ? null : new VoiceDownloadPlan(voicesDirectory, model.VoicesBaseUrl, files);
    }

    private sealed record VoiceDownloadPlan(string VoicesDirectory, string VoicesBaseUrl, IReadOnlyList<string> Files);

    private async Task<bool> DownloadVoiceFiles(HttpClient client, string modelName, VoiceDownloadPlan plan, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(plan.VoicesDirectory);
        var complete = true;

        foreach (var voiceFileName in plan.Files)
        {
            var outputPath = Path.Combine(plan.VoicesDirectory, voiceFileName);

            var voiceUrl = $"{plan.VoicesBaseUrl.TrimEnd('/')}/{voiceFileName}";
            var tempPath = $"{outputPath}.tmp";

            try
            {
                await using var stream = await client.GetStreamAsync(voiceUrl, cancellationToken);
                await using (var output = File.Create(tempPath))
                {
                    await stream.CopyToAsync(output, cancellationToken);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                _logger.LogWarning(ex, "Failed to download voice file {Voice} for {Model}", voiceFileName, modelName);
                complete = false;
            }
        }

        return complete;
    }
}
