// wiki: backend-architecture#worker-mode-vs-in-process-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public sealed class KokoroTtsSynthesizer : ITtsSynthesizer, IIdleTracking, IEngineWarmer<TtsModelDefinition>, IDisposable
{
    private const string KokoroEngine = "kokoro";
    private const int SampleRate = 24000;
    private readonly EnginePool<KokoroTtsEngine> _pool = new();

    public Task<SynthesisResult> SynthesizeAsync(
        TtsModelDefinition model,
        string modelDirectory,
        OpenAiSpeechRequest request,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        // Only support Kokoro engine
        if (!string.Equals(model.Engine, KokoroEngine, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, SampleRate));
        }

        try
        {
            var engine = GetOrCreateEngine(model, modelDirectory);
            var speed = Math.Clamp(request.Speed, 0.5f, 2.0f);
            var language = DetermineLanguage(request);
            
            // Load speaker voice if needed
            LoadSpeakerVoice(engine, model, modelDirectory, request);

            cancellationToken.ThrowIfCancellationRequested();

            // Synthesize audio
            var audioBytes = engine.SynthesizeToBytes(request.Input, language, speed);
            
            if (audioBytes.Length == 0)
            {
                return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, SampleRate));
            }

            // Generate WAV file with header
            var wavBytes = WavAudioUtils.WrapPcm16MonoAsWav(audioBytes, SampleRate);
            
            // Calculate metrics
            var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
            var audioDuration = WavAudioUtils.GetPcmDurationSeconds(audioBytes.Length, SampleRate);
            var charCount = request.Input.Length;
            
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
            Console.Error.WriteLine($"Kokoro synthesis failed: {ex.Message}");
            return Task.FromResult(SynthesisFallback.Silence(model, request, startTime, SampleRate));
        }
    }

    public Task<bool> WarmUpAsync(TtsModelDefinition model, string modelDirectory, CancellationToken cancellationToken)
    {
        if (!string.Equals(model.Engine, KokoroEngine, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        GetOrCreateEngine(model, modelDirectory);
        return Task.FromResult(true);
    }

    private KokoroTtsEngine GetOrCreateEngine(TtsModelDefinition model, string modelDirectory)
    {
        var cacheKey = $"{model.Name}:{modelDirectory}";

        return _pool.GetOrCreate(cacheKey, () =>
        {
            var modelPath = Path.Combine(modelDirectory, model.ModelPath);

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Model file not found: {modelPath}");
            }

            Console.WriteLine($"Creating Kokoro engine for {model.Name} with model: {modelPath}");
            return new KokoroTtsEngine(modelPath);
        });
    }

    private void LoadSpeakerVoice(KokoroTtsEngine engine, TtsModelDefinition model, string modelDirectory, OpenAiSpeechRequest request)
    {
        var speaker = request.Speaker;
        if (KokoroMetadata.IsDefaultVoiceValue(speaker))
        {
            speaker = request.Voice;
        }
        if (KokoroMetadata.IsDefaultVoiceValue(speaker))
        {
            speaker = model.Speakers.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(speaker))
        {
            return;
        }

        var voicesPath = model.VoicesPath ?? "voices";
        var voicesDirectory = Path.Combine(modelDirectory, voicesPath);
        
        if (!Directory.Exists(voicesDirectory))
        {
            Console.WriteLine($"Voices directory not found: {voicesDirectory}");
            return;
        }

        var extension = string.IsNullOrWhiteSpace(model.VoiceFileExtension) ? ".bin" : model.VoiceFileExtension;
        var voiceFile = Path.Combine(voicesDirectory, $"{speaker}{extension}");

        if (File.Exists(voiceFile))
        {
            engine.LoadSpeakers(voiceFile);
        }
        else
        {
            // Try to find any voice file as fallback
            var anyVoiceFile = model.Speakers
                .Select(s => Path.Combine(voicesDirectory, $"{s}{extension}"))
                .FirstOrDefault(File.Exists);

            if (anyVoiceFile != null)
            {
                Console.WriteLine($"Using fallback voice: {Path.GetFileName(anyVoiceFile)}");
                engine.LoadSpeakers(anyVoiceFile);
            }
        }
    }

    private static string DetermineLanguage(OpenAiSpeechRequest request)
    {
        if (KokoroMetadata.TryNormalizeLanguage(request.Language, out var normalized) &&
            KokoroMetadata.TryMapToEspeakVoice(normalized, out var espeakVoice))
        {
            return espeakVoice;
        }

        return "en-us";
    }

    public IReadOnlyDictionary<string, DateTime> GetLoadedEngines() => _pool.GetLoadedEngines();

    public bool TryReleaseEngine(string engineKey) => _pool.TryReleaseEngine(engineKey);

    public void Dispose() => _pool.Dispose();
}
