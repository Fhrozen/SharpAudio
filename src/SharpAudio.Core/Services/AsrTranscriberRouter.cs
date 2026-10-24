// wiki: asr-engines#worker-process-di-wiring-and-server-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>
/// Routes transcription requests to the correct <see cref="IAsrTranscriber"/> implementation
/// based on the model's engine name (mirrors <see cref="TtsSynthesizerRouter"/>).
/// </summary>
public sealed class AsrTranscriberRouter : IAsrTranscriber, IIdleTracking, IDisposable
{
    private const string NemotronEngine = "nemotron-3.5";

    private readonly WhisperAsrTranscriber _whisper;
    private readonly NemotronAsrTranscriber _nemotron;
    private readonly IdleTrackingAggregate _tracking;

    public AsrTranscriberRouter(WhisperAsrTranscriber whisper, NemotronAsrTranscriber nemotron)
    {
        _whisper = whisper;
        _nemotron = nemotron;
        _tracking = new IdleTrackingAggregate(whisper, nemotron);
    }

    public Task<TranscriptionResult> TranscribeAsync(
        AsrModelDefinition model,
        string modelDirectory,
        AudioTranscriptionRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        if (string.Equals(model.Engine, NemotronEngine, StringComparison.OrdinalIgnoreCase))
        {
            return _nemotron.TranscribeAsync(model, modelDirectory, request, audioBytes, cancellationToken);
        }

        // Default: Whisper
        return _whisper.TranscribeAsync(model, modelDirectory, request, audioBytes, cancellationToken);
    }

    public Task<IStreamingTranscriptionSession> CreateStreamingSessionAsync(
        AsrModelDefinition model,
        string modelDirectory,
        string? language,
        bool enableVad,
        bool enableEndpointing,
        double segmentSeconds,
        CancellationToken cancellationToken)
    {
        if (string.Equals(model.Engine, NemotronEngine, StringComparison.OrdinalIgnoreCase))
        {
            return _nemotron.CreateStreamingSessionAsync(model, modelDirectory, language, enableVad, enableEndpointing, segmentSeconds, cancellationToken);
        }

        return _whisper.CreateStreamingSessionAsync(model, modelDirectory, language, enableVad, enableEndpointing, segmentSeconds, cancellationToken);
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _tracking.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _tracking.TryReleaseEngine(engineKey);

    public void Dispose() => _tracking.Dispose();
}
