// wiki: backend-architecture#worker-mode-vs-in-process-mode
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

/// <summary>The silence response every TTS path returns when an engine cannot produce audio.</summary>
public static class SynthesisFallback
{
    private const double CharsPerSecond = 30.0;
    private const double MinDurationSeconds = 0.5;
    private const double MaxDurationSeconds = 8.0;

    public static double GetSilenceDurationSeconds(int inputLength)
    {
        return Math.Clamp(inputLength / CharsPerSecond, MinDurationSeconds, MaxDurationSeconds);
    }

    public static SynthesisResult Silence(TtsModelDefinition model, OpenAiSpeechRequest request, DateTime startTime, int sampleRate)
    {
        var durationSeconds = GetSilenceDurationSeconds(request.Input.Length);
        var pcm = new byte[(int)(sampleRate * durationSeconds) * 2];
        var wavBytes = WavAudioUtils.WrapPcm16MonoAsWav(pcm, sampleRate);

        return new SynthesisResult(
            wavBytes,
            "audio/wav",
            $"{model.Name}.wav",
            (DateTime.UtcNow - startTime).TotalSeconds,
            WavAudioUtils.GetPcmDurationSeconds(pcm.Length, sampleRate),
            request.Input.Length);
    }
}
