// wiki: asr-engines#nemotron-engine
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;

namespace SharpAudio.Core.Services;

public sealed class NemotronAsrTranscriber : IAsrTranscriber, IIdleTracking, IEngineWarmer<AsrModelDefinition>, IDisposable
{
    private const string NemotronEngine = "nemotron-3.5";
    private readonly EnginePool<NemotronAsrEngine> _pool = new();
    private readonly VadOptions _options;

    public NemotronAsrTranscriber(VadOptions? vadOptions = null)
    {
        _options = vadOptions ?? new VadOptions();
    }

    public Task<TranscriptionResult> TranscribeAsync(
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(model.Engine, NemotronEngine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"NemotronAsrTranscriber cannot handle engine '{model.Engine}'.");
        }

        var startTime = DateTime.UtcNow;
        var engine = GetOrCreateEngine(model, modelDirectory);

        var (text, detectedLanguage, rawSegments) = engine.Transcribe(audioBytes, request.Language, request.EnableVad);

        var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var audioDuration = WavAudioUtils.GetDurationSeconds(audioBytes);
        var segments = request.IncludeSegments
            ? SegmentMerger.MergeShortSegments(rawSegments, request.MinSegmentDurationSeconds)
            : null;

        return Task.FromResult(new TranscriptionResult(text, detectedLanguage, processingTime, audioDuration, text.Length, segments));
    }

    public IStreamingTranscriptionSession CreateStreamingSession(AsrModelDefinition model, string modelDirectory, string? language, bool enableVad, bool enableEndpointing, double segmentSeconds)
    {
        if (!string.Equals(model.Engine, NemotronEngine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"NemotronAsrTranscriber cannot handle engine '{model.Engine}'.");
        }

        return GetOrCreateEngine(model, modelDirectory).CreateStreamingSession(language, enableVad, segmentSeconds, enableEndpointing);
    }

    public Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
        AsrModelDefinition model, string modelDirectory, string? language, bool enableVad, bool enableEndpointing, double segmentSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(CreateStreamingSession(model, modelDirectory, language, enableVad, enableEndpointing, segmentSeconds));

    public Task<bool> WarmUpAsync(AsrModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        if (!string.Equals(model.Engine, NemotronEngine, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        GetOrCreateEngine(model, modelDirectory);
        return Task.FromResult(true);
    }

    private NemotronAsrEngine GetOrCreateEngine(AsrModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";

        return _pool.GetOrCreate(cacheKey, () =>
        {
            Console.WriteLine($"Creating Nemotron engine for {model.Name} in: {modelDirectory}");
            return new NemotronAsrEngine(modelDirectory, _options);
        });
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _pool.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _pool.TryReleaseEngine(engineKey);

    public void Dispose() => _pool.Dispose();
}
