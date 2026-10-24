// wiki: asr-engines#whisper-engine
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;

namespace SharpAudio.Core.Services;

public sealed class WhisperAsrTranscriber : IAsrTranscriber, IIdleTracking, IDisposable
{
    private const string WhisperEngine = "whisper";

    private readonly EnginePool<WhisperAsrEngine> _engines = new();
    private readonly VadPipeline _vadPipeline;
    private readonly IdleTrackingAggregate _tracking;

    public WhisperAsrTranscriber(VadOptions? vadOptions = null)
    {
        _vadPipeline = new VadPipeline(vadOptions, "Whisper VAD");
        _tracking = new IdleTrackingAggregate(_engines, _vadPipeline);
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(model.Engine, WhisperEngine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"WhisperAsrTranscriber cannot handle engine '{model.Engine}'.");
        }

        var startTime = DateTime.UtcNow;
        var engine = GetOrCreateEngine(model, modelDirectory);

        var result = request.EnableVad
            ? await TranscribeWithVadAsync(engine, model, modelDirectory, request, audioBytes, cancellationToken)
            : await TranscribeAsync(engine, audioBytes, request.Language, cancellationToken);

        var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var audioDuration = WavAudioUtils.GetDurationSeconds(audioBytes);
        var segments = request.IncludeSegments
            ? SegmentMerger.MergeShortSegments(result.Segments, request.MinSegmentDurationSeconds)
            : null;

        return new TranscriptionResult(result.Text, result.DetectedLanguage, processingTime, audioDuration, result.Text.Length, segments);
    }

    public IStreamingTranscriptionSession CreateStreamingSession(AsrModelDefinition model, string modelDirectory, string? language, double segmentSeconds)
    {
        if (!string.Equals(model.Engine, WhisperEngine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"WhisperAsrTranscriber cannot handle engine '{model.Engine}'.");
        }

        return new WhisperStreamingSession(GetOrCreateEngine(model, modelDirectory), language, segmentSeconds);
    }

    // Whisper has no native VAD - enableVad is accepted only to satisfy IAsrTranscriber's uniform
    // signature; the REST path segments with Silero instead (see TranscribeWithVadAsync).
    // Endpointing is likewise accepted and ignored here: Whisper's streaming path has no
    // per-chunk speech decision to endpoint on, so it keeps the time/punctuation policy alone.
    public Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
        AsrModelDefinition model, string modelDirectory, string? language, bool enableVad, bool enableEndpointing, double segmentSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(CreateStreamingSession(model, modelDirectory, language, segmentSeconds));

    private Task<SpanTranscription> TranscribeWithVadAsync(
        WhisperAsrEngine engine,
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        return _vadPipeline.TranscribeAsync(
            modelDirectory,
            audioBytes,
            request.VadOnset,
            request.VadOffset,
            (spanSamples, token) => TranscribeAsync(engine, WavAudioUtils.EncodeMono16kWav(spanSamples), request.Language, token),
            cancellationToken);
    }

    private static async Task<SpanTranscription> TranscribeAsync(WhisperAsrEngine engine, byte[] wavBytes, string? language, CancellationToken cancellationToken)
    {
        var (text, detectedLanguage, segments) = await engine.TranscribeAsync(wavBytes, language, cancellationToken);
        return new SpanTranscription(text, detectedLanguage, segments);
    }

    private WhisperAsrEngine GetOrCreateEngine(AsrModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";

        return _engines.GetOrCreate(cacheKey, () =>
        {
            var modelPath = Path.Combine(modelDirectory, model.ModelPath);

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Model file not found: {modelPath}");
            }

            Console.WriteLine($"Creating Whisper engine for {model.Name} with model: {modelPath}");
            return new WhisperAsrEngine(modelPath);
        });
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _tracking.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _tracking.TryReleaseEngine(engineKey);

    public void Dispose()
    {
        _tracking.Dispose();
    }
}
