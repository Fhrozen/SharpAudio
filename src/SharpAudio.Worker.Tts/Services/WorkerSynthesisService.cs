using SharpAudio.Core.Health;
using SharpAudio.Core.Services;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Grpc;
using Grpc.Core;

namespace SharpAudio.Worker.Tts.Services;

/// <summary>
/// gRPC service that performs TTS synthesis in worker process
/// </summary>
public sealed class WorkerSynthesisService : WorkerSynthesis.WorkerSynthesisBase, IDisposable
{
    private const string KokoroKind = "kokoro";
    private const string SupertonicKind = "supertonic";
    private const int FallbackSampleRate = 24000;

    private readonly WorkerServiceBase _base;

    public WorkerSynthesisService(IdleMonitor idleMonitor)
    {
        _base = new WorkerServiceBase(idleMonitor);
    }

    public override async Task<SynthesizeResponse> Synthesize(SynthesizeRequest request, ServerCallContext context)
    {
        _base.RecordActivity();
        var startTime = DateTime.UtcNow;

        try
        {
            Console.WriteLine($"[Worker] Synthesize request: engine={request.Engine}, model={request.ModelName}");

            // Determine which engine to use and get correct sample rate
            byte[] audioBytes;
            int sampleRate;
            
            if (string.Equals(request.Engine, "kokoro", StringComparison.OrdinalIgnoreCase))
            {
                sampleRate = 24000; // Kokoro always uses 24000 Hz
                audioBytes = await SynthesizeWithKokoroAsync(request, context.CancellationToken);
            }
            else if (string.Equals(request.Engine, "supertonic", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(request.Engine, "supertonic-3", StringComparison.OrdinalIgnoreCase))
            {
                var engine = GetOrCreateSupertonicEngine(request);
                sampleRate = engine.SampleRate; // Get actual sample rate (22050 Hz for Supertonic-3)
                audioBytes = await SynthesizeWithSupertonicAsync(request, context.CancellationToken);
            }
            else
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unsupported engine: {request.Engine}"));
            }

            if (audioBytes.Length == 0)
            {
                sampleRate = FallbackSampleRate; // Fallback uses 24000 Hz
                audioBytes = new byte[(int)(sampleRate * SynthesisFallback.GetSilenceDurationSeconds(request.Input.Length)) * 2];
            }

            // Generate WAV file with header using correct sample rate
            var wavBytes = WavAudioUtils.WrapPcm16MonoAsWav(audioBytes, sampleRate);

            // Calculate metrics
            var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;
            var audioDuration = WavAudioUtils.GetPcmDurationSeconds(audioBytes.Length, sampleRate);
            var charCount = request.Input.Length;

            Console.WriteLine($"[Worker] Synthesis complete: {charCount} chars, {processingTime:F2}s processing, {audioDuration:F2}s audio, sampleRate={sampleRate} Hz");
            // Console.Error.WriteLine($"[Worker] Synthesis complete: {charCount} chars, {processingTime:F2}s processing, {audioDuration:F2}s audio, sampleRate={sampleRate} Hz");

            return new SynthesizeResponse
            {
                AudioBytes = Google.Protobuf.ByteString.CopyFrom(wavBytes),
                ContentType = "audio/wav",
                FileName = $"{request.ModelName}.wav",
                ProcessingTimeSeconds = processingTime,
                AudioDurationSeconds = audioDuration,
                CharacterCount = charCount
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Worker] Synthesis failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[Worker] Stack trace: {ex.StackTrace}");
            
            // Return fallback silence
            var durationSeconds = SynthesisFallback.GetSilenceDurationSeconds(request.Input.Length);
            var wavBytes = WavAudioUtils.GenerateSilenceWav(durationSeconds, FallbackSampleRate);
            var processingTime = (DateTime.UtcNow - startTime).TotalSeconds;

            return new SynthesizeResponse
            {
                AudioBytes = Google.Protobuf.ByteString.CopyFrom(wavBytes),
                ContentType = "audio/wav",
                FileName = $"{request.ModelName}.wav",
                ProcessingTimeSeconds = processingTime,
                AudioDurationSeconds = durationSeconds,
                CharacterCount = request.Input.Length
            };
        }
    }

    private async Task<byte[]> SynthesizeWithKokoroAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        var engine = GetOrCreateKokoroEngine(request);
        var speed = Math.Clamp(request.Speed, 0.5f, 4.0f);
        var language = DetermineLanguage(request);

        // Load speaker voice if needed
        LoadKokoroSpeakerVoice(engine, request);

        cancellationToken.ThrowIfCancellationRequested();

        // Perform synthesis on thread pool to avoid blocking gRPC thread
        return await Task.Run(() => engine.SynthesizeToBytes(request.Input, language, speed), cancellationToken);
    }

    private async Task<byte[]> SynthesizeWithSupertonicAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        var engine = GetOrCreateSupertonicEngine(request);
        var speed = Math.Clamp(request.Speed, 0.5f, 4.0f);
        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;
        var style = LoadSupertonicStyle(request);

        // Console.WriteLine($"[Worker] Supertonic synthesis: input_speed={request.Speed}, clamped_speed={speed}, language={language}, voice={request.Voice}, input_length={request.Input.Length}");
        // Console.Error.WriteLine($"[Worker] Supertonic synthesis: input_speed={request.Speed}, clamped_speed={speed}, language={language}, voice={request.Voice}, input_length={request.Input.Length}");

        cancellationToken.ThrowIfCancellationRequested();

        // Perform synthesis on thread pool - USE NAMED PARAMETERS
        var audioBytes = await Task.Run(() => 
            engine.SynthesizeToBytes(
                text: request.Input, 
                lang: language, 
                style: style, 
                totalStep: 8, 
                speed: speed
            ), 
            cancellationToken);
        
        // Console.WriteLine($"[Worker] Supertonic synthesis complete: audio_size={audioBytes.Length} bytes, used_speed={speed}");
        // Console.Error.WriteLine($"[Worker] Supertonic synthesis complete: audio_size={audioBytes.Length} bytes, used_speed={speed}");
        
        return audioBytes;
    }

    private KokoroTtsEngine GetOrCreateKokoroEngine(SynthesizeRequest request)
    {
        return _base.Engines.GetOrCreate(KokoroKind, $"{KokoroKind}:{request.ModelName}", () =>
        {
            if (!File.Exists(request.ModelPath))
            {
                throw new FileNotFoundException($"Model file not found: {request.ModelPath}");
            }

            Console.WriteLine($"[Worker] Loading Kokoro model: {request.ModelPath}");
            return new KokoroTtsEngine(request.ModelPath);
        });
    }

    private SupertonicTtsEngine GetOrCreateSupertonicEngine(SynthesizeRequest request)
    {
        return _base.Engines.GetOrCreate(SupertonicKind, $"{SupertonicKind}:{request.ModelName}", () =>
        {
            // Supertonic takes a directory path, not a file path
            if (!Directory.Exists(request.ModelPath))
            {
                throw new DirectoryNotFoundException($"Model directory not found: {request.ModelPath}");
            }

            Console.WriteLine($"[Worker] Loading Supertonic model from directory: {request.ModelPath}");
            return new SupertonicTtsEngine(request.ModelPath);
        });
    }

    private void LoadKokoroSpeakerVoice(KokoroTtsEngine engine, SynthesizeRequest request)
    {
        var speaker = request.Voice;
        if (KokoroMetadata.IsDefaultVoiceValue(speaker))
        {
            speaker = request.Speakers.FirstOrDefault() ?? "";
        }

        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(request.VoicesPath))
        {
            return;
        }

        var voiceFile = Path.Combine(request.VoicesPath, $"{speaker}{request.VoiceFileExtension}");

        if (File.Exists(voiceFile))
        {
            engine.LoadSpeakers(voiceFile);
        }
        else
        {
            // Try to find any voice file as fallback
            var anyVoiceFile = request.Speakers
                .Select(s => Path.Combine(request.VoicesPath, $"{s}{request.VoiceFileExtension}"))
                .FirstOrDefault(File.Exists);

            if (anyVoiceFile != null)
            {
                Console.WriteLine($"[Worker] Using fallback voice: {Path.GetFileName(anyVoiceFile)}");
                engine.LoadSpeakers(anyVoiceFile);
            }
        }
    }

    private SupertonicStyle LoadSupertonicStyle(SynthesizeRequest request)
    {
        var speaker = request.Voice;
        if (string.IsNullOrWhiteSpace(speaker))
        {
            speaker = request.Speakers.FirstOrDefault() ?? "";
        }

        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(request.VoicesPath))
        {
            // Return zero-filled style as fallback
            return new SupertonicStyle([], [1, 1, 1], [], [1, 1, 1]);
        }

        var voiceFile = Path.Combine(request.VoicesPath, $"{speaker}{request.VoiceFileExtension}");

        if (File.Exists(voiceFile))
        {
            return SupertonicTtsEngine.LoadVoiceStyle(voiceFile);
        }

        // Try to find any voice file as fallback
        var anyVoiceFile = request.Speakers
            .Select(s => Path.Combine(request.VoicesPath, $"{s}{request.VoiceFileExtension}"))
            .FirstOrDefault(File.Exists);

        if (anyVoiceFile != null)
        {
            Console.WriteLine($"[Worker] Using fallback Supertonic style: {Path.GetFileName(anyVoiceFile)}");
            return SupertonicTtsEngine.LoadVoiceStyle(anyVoiceFile);
        }

        // Last resort: zero-filled style
        Console.WriteLine($"[Worker] No Supertonic style files found, using zero style");
        return new SupertonicStyle([], [1, 1, 1], [], [1, 1, 1]);
    }

    private static string DetermineLanguage(SynthesizeRequest request)
    {
        if (KokoroMetadata.TryNormalizeLanguage(request.Language, out var normalized) &&
            KokoroMetadata.TryMapToEspeakVoice(normalized, out var espeakVoice))
        {
            return espeakVoice;
        }

        return "en-us";
    }


    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(_base.BuildHealthCheck());
    }

    public void Dispose()
    {
        _base.Dispose();
    }
}
