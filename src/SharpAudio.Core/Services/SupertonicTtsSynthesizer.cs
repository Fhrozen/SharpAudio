// wiki: backend-architecture#worker-mode-vs-in-process-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>
/// ITtsSynthesizer implementation for the Supertonic-3 engine.
/// Manages a pool of <see cref="SupertonicTtsEngine"/> instances (one per model+directory
/// combination) and delegates synthesis to the appropriate engine.
/// </summary>
public sealed class SupertonicTtsSynthesizer : ITtsSynthesizer, IIdleTracking, IEngineWarmer<TtsModelDefinition>, IDisposable
{
    private const int FallbackSampleRate = 22050;

    private readonly EnginePool<SupertonicTtsEngine> _pool = new();

    public Task<SynthesisResult> SynthesizeAsync(
        TtsModelDefinition model,
        string modelDirectory,
        OpenAiSpeechRequest request,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        if (!SupertonicMetadata.IsSupertonic3Engine(model.Engine))
        {
            return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, FallbackSampleRate));
        }

        try
        {
            var engine = GetOrCreateEngine(model, modelDirectory);

            // Resolve language
            var lang = "en";
            if (SupertonicMetadata.TryNormalizeLanguage(request.Language, out var normalizedLang))
            {
                lang = normalizedLang;
            }

            // Resolve speaker
            var speaker = ResolveSpeaker(model, request);

            // Load voice style
            var style = LoadVoiceStyle(model, modelDirectory, speaker);

            var speed = Math.Clamp(request.Speed, 0.5f, 2.0f);

            cancellationToken.ThrowIfCancellationRequested();

            var audioBytes = engine.SynthesizeToBytes(request.Input, lang, style, speed: speed);

            if (audioBytes.Length == 0)
            {
                return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, engine.SampleRate));
            }

            var sampleRate = engine.SampleRate;
            var wavBytes   = WavAudioUtils.WrapPcm16MonoAsWav(audioBytes, sampleRate);

            var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
            var audioDuration  = WavAudioUtils.GetPcmDurationSeconds(audioBytes.Length, sampleRate);
            var charCount      = request.Input.Length;

            return Task.FromResult(new SynthesisResult(
                wavBytes,
                "audio/wav",
                $"{model.Name}.wav",
                processingTime,
                audioDuration,
                charCount));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Supertonic-3 synthesis failed: {ex.Message}");
            return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, FallbackSampleRate));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    public Task<bool> WarmUpAsync(TtsModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        if (!SupertonicMetadata.IsSupertonic3Engine(model.Engine))
        {
            return Task.FromResult(false);
        }

        GetOrCreateEngine(model, modelDirectory);
        return Task.FromResult(true);
    }

    private SupertonicTtsEngine GetOrCreateEngine(TtsModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";

        return _pool.GetOrCreate(cacheKey, () =>
        {
            // modelPath for supertonic-3 is the relative path of the onnx sub-directory
            var onnxDir = Path.Combine(modelDirectory, model.ModelPath);
            if (!Directory.Exists(onnxDir))
            {
                throw new DirectoryNotFoundException($"ONNX directory not found: {onnxDir}");
            }

            Console.WriteLine($"[Supertonic-3] Creating engine for {model.Name} from: {onnxDir}");
            return new SupertonicTtsEngine(onnxDir);
        });
    }

    private static string ResolveSpeaker(TtsModelDefinition model, OpenAiSpeechRequest request)
    {
        var speaker = request.Speaker;
        if (SupertonicMetadata.IsDefaultVoiceValue(speaker))
        {
            speaker = request.Voice;
        }

        if (SupertonicMetadata.IsDefaultVoiceValue(speaker))
        {
            speaker = model.Speakers.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(speaker))
        {
            speaker = SupertonicMetadata.DefaultSpeaker;
        }

        return speaker;
    }

    private static SupertonicStyle LoadVoiceStyle(TtsModelDefinition model,
        string modelDirectory, string speaker)
    {
        var voicesPath    = model.VoicesPath ?? "voice_styles";
        var voicesDir     = Path.Combine(modelDirectory, voicesPath);
        var extension     = string.IsNullOrWhiteSpace(model.VoiceFileExtension) ? ".json" : model.VoiceFileExtension;
        var voiceFile     = Path.Combine(voicesDir, $"{speaker}{extension}");

        if (File.Exists(voiceFile))
        {
            return SupertonicTtsEngine.LoadVoiceStyle(voiceFile);
        }

        // Fallback: try any available speaker file
        var fallback = model.Speakers
            .Select(s => Path.Combine(voicesDir, $"{s}{extension}"))
            .FirstOrDefault(File.Exists);

        if (fallback != null)
        {
            Console.WriteLine($"[Supertonic-3] Voice '{speaker}' not found, using fallback: {Path.GetFileName(fallback)}");
            return SupertonicTtsEngine.LoadVoiceStyle(fallback);
        }

        // Last resort: return a zero-filled style so the engine still runs
        // (shape [1,1,1] is a placeholder — the engine will produce silence or noise)
        Console.WriteLine($"[Supertonic-3] No voice style files found in {voicesDir}; using zero style");
        return new SupertonicStyle([], [1, 1, 1], [], [1, 1, 1]);
    }


    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _pool.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _pool.TryReleaseEngine(engineKey);

    public void Dispose() => _pool.Dispose();
}
