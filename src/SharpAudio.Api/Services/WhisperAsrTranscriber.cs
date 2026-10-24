using System.Collections.Concurrent;
using SharpAudio.Api.Contracts;
using SharpAudio.Api.Models;
using SharpAudio.Api.Options;

namespace SharpAudio.Api.Services;

public sealed class WhisperAsrTranscriber : IAsrTranscriber, IIdleTrackingTranscriber, IDisposable
{
    private const string WhisperEngine = "whisper";
    private const int VadSampleRate = 16000;
    private const string SileroVadAsset = "silero_vad.onnx";

    private readonly ConcurrentDictionary<string, WhisperAsrEngine> _engines = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessTimes = new();
    private readonly ConcurrentDictionary<string, ISileroVadEngine> _vadEngines = new();
    private readonly SemaphoreSlim _vadGate = new(1, 1);
    private readonly VadOptions _options;
    private bool _disposed;

    public WhisperAsrTranscriber(VadOptions? vadOptions = null)
    {
        _options = vadOptions ?? new VadOptions();
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

    private async Task<SpanTranscription> TranscribeWithVadAsync(
        WhisperAsrEngine engine,
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        var vadEngine = GetOrCreateVadEngine(model, modelDirectory);
        var samples = WavAudioUtils.ReadMonoFloat(audioBytes, VadSampleRate);
        var options = _options.WithOverrides(request.VadOnset, request.VadOffset);
        var spanTranscriber = new VadSpanTranscriber(
            vadEngine,
            options,
            (spanSamples, token) => TranscribeAsync(engine, WavAudioUtils.EncodeMono16kWav(spanSamples), request.Language, token));

        // One utterance at a time per VAD engine, since the LSTM state is carried across calls.
        await _vadGate.WaitAsync(cancellationToken);
        try
        {
            return await spanTranscriber.TranscribeAsync(samples, cancellationToken);
        }
        finally
        {
            _vadGate.Release();
        }
    }

    private static async Task<SpanTranscription> TranscribeAsync(WhisperAsrEngine engine, byte[] wavBytes, string? language, CancellationToken cancellationToken)
    {
        var (text, detectedLanguage, segments) = await engine.TranscribeAsync(wavBytes, language, cancellationToken);
        return new SpanTranscription(text, detectedLanguage, segments);
    }

    private ISileroVadEngine GetOrCreateVadEngine(AsrModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";
        var vadPath = Path.Combine(modelDirectory, SileroVadAsset);

        if (!File.Exists(vadPath))
        {
            throw new FileNotFoundException($"Model file not found: {vadPath}");
        }

        var engine = _vadEngines.GetOrAdd(cacheKey, _ =>
        {
            Console.WriteLine($"Creating Silero VAD engine for {model.Name} with model: {vadPath}");
            return new SileroVadEngine(vadPath, VadSampleRate);
        });

        _lastAccessTimes[cacheKey] = DateTime.UtcNow;
        return engine;
    }

    private WhisperAsrEngine GetOrCreateEngine(AsrModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";

        var engine = _engines.GetOrAdd(cacheKey, _ =>
        {
            var modelPath = Path.Combine(modelDirectory, model.ModelPath);

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Model file not found: {modelPath}");
            }

            Console.WriteLine($"Creating Whisper engine for {model.Name} with model: {modelPath}");
            return new WhisperAsrEngine(modelPath);
        });

        _lastAccessTimes[cacheKey] = DateTime.UtcNow;
        return engine;
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _lastAccessTimes;

    public bool TryReleaseEngine(string engineKey)
    {
        if (_engines.TryRemove(engineKey, out var engine))
        {
            engine.Dispose();
            _lastAccessTimes.TryRemove(engineKey, out _);
            if (_vadEngines.TryRemove(engineKey, out var vadEngine))
            {
                (vadEngine as IDisposable)?.Dispose();
            }

            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }

        foreach (var vadEngine in _vadEngines.Values)
        {
            (vadEngine as IDisposable)?.Dispose();
        }

        _vadGate.Dispose();
        _disposed = true;
    }
}
