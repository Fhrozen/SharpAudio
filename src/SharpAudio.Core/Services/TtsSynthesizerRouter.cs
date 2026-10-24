// wiki: backend-architecture#worker-mode-vs-in-process-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>
/// Routes synthesis requests to the correct <see cref="ITtsSynthesizer"/> implementation
/// based on the model's engine name. This preserves the single-interface DI contract while
/// supporting multiple TTS backends (Kokoro, Supertonic-3, …).
/// </summary>
public sealed class TtsSynthesizerRouter : ITtsSynthesizer, IIdleTracking, IDisposable
{
    private readonly KokoroTtsSynthesizer _kokoro;
    private readonly SupertonicTtsSynthesizer _supertonic;
    private readonly IdleTrackingAggregate _tracking;

    public TtsSynthesizerRouter(
        KokoroTtsSynthesizer kokoro,
        SupertonicTtsSynthesizer supertonic)
    {
        _kokoro     = kokoro;
        _supertonic = supertonic;
        _tracking = new IdleTrackingAggregate(kokoro, supertonic);
    }

    public Task<SynthesisResult> SynthesizeAsync(
        TtsModelDefinition model,
        string modelDirectory,
        OpenAiSpeechRequest request,
        CancellationToken cancellationToken)
    {
        if (SupertonicMetadata.IsSupertonic3Engine(model.Engine))
        {
            return _supertonic.SynthesizeAsync(model, modelDirectory, request, cancellationToken);
        }

        // Default: Kokoro (and any future unrecognised engine falls back to Kokoro's
        // built-in graceful silence generation)
        return _kokoro.SynthesizeAsync(model, modelDirectory, request, cancellationToken);
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _tracking.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _tracking.TryReleaseEngine(engineKey);

    public void Dispose() => _tracking.Dispose();
}
