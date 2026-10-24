// wiki: asr-engines#vad-support
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;

namespace SharpAudio.Core.Services;

/// <summary>
/// The single VAD path for engines with no native VAD, shared by the in-process Whisper
/// transcriber and the ASR worker: resolve the Silero engine for the model directory, decode
/// 16 kHz mono samples, and run <see cref="VadSpanTranscriber"/>. Only segmentation is serialised,
/// since the Silero LSTM state is carried across calls; spans are transcribed outside the gate, so
/// inference overlaps with the next request's VAD run.
///
/// Base options come from DI (or the defaults in a worker process); per-request onset/offset
/// overrides are applied here, so both callers read the same policy. The pipeline owns its own
/// engine pool, so a VAD engine is not evicted when the active ASR model changes.
/// </summary>
public sealed class VadPipeline : IIdleTracking, IDisposable
{
    private const int VadSampleRate = 16000;
    private const string SileroVadAsset = "silero_vad.onnx";

    private readonly EnginePool<ISileroVadEngine> _engines = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VadOptions _options;
    private readonly string _logPrefix;

    public VadPipeline(VadOptions? options = null, string logPrefix = "VAD")
    {
        _options = options ?? new VadOptions();
        _logPrefix = logPrefix;
    }

    public async Task<SpanTranscription> TranscribeAsync(
        string modelDirectory,
        byte[] audioBytes,
        float? onset,
        float? offset,
        Func<float[], CancellationToken, Task<SpanTranscription>> transcribeSpan,
        CancellationToken cancellationToken)
    {
        var vadEngine = GetOrCreateVadEngine(modelDirectory);
        var samples = WavAudioUtils.ReadMonoFloat(audioBytes, VadSampleRate);
        var spanTranscriber = new VadSpanTranscriber(
            vadEngine,
            _options.WithOverrides(onset, offset),
            transcribeSpan,
            _gate);

        // The gate is handed to the transcriber and held for segmentation only, so one request's
        // inference can overlap with the next request's VAD run.
        return await spanTranscriber.TranscribeAsync(samples, cancellationToken);
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _engines.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _engines.TryReleaseEngine(engineKey);

    public void Dispose()
    {
        _gate.Dispose();
        _engines.Dispose();
    }

    private ISileroVadEngine GetOrCreateVadEngine(string modelDirectory)
    {
        var vadPath = Path.Combine(modelDirectory, SileroVadAsset);

        if (!File.Exists(vadPath))
        {
            throw new FileNotFoundException($"Model file not found: {vadPath}");
        }

        return _engines.GetOrCreate(vadPath, () =>
        {
            Console.WriteLine($"[{_logPrefix}] Loading Silero VAD model: {vadPath}");
            return new SileroVadEngine(vadPath, VadSampleRate);
        });
    }
}
