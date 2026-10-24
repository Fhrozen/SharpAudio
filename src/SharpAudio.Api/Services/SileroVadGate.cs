namespace SharpAudio.Api.Services;

/// <summary>Consecutive-silence chunk-gating policy - ports the Python reference's VadGate
/// (StreamingProcessor::ShouldDropChunk), used to skip encoder/decoder inference for silent
/// chunks during Nemotron transcription when VAD is enabled.</summary>
public sealed class SileroVadGate
{
    private readonly ISileroVadEngine _engine;
    private readonly float _threshold;
    private readonly int _silenceDurationChunks;
    private readonly int _prefixPaddingChunks;
    private int _consecutiveSilenceChunks;

    /// <summary>Speech decision of the most recent ShouldDropChunk call, so endpointing can reuse
    /// it without running the model twice on the same chunk.</summary>
    public bool LastChunkHadSpeech { get; private set; }

    public SileroVadGate(ISileroVadEngine engine, float threshold, int chunkSamples, int sampleRate, double silenceDurationMs, double prefixPaddingMs)
    {
        _engine = engine;
        _threshold = threshold;

        var chunkDurationMs = chunkSamples / (double)sampleRate * 1000.0;
        _silenceDurationChunks = Math.Max(1, (int)(silenceDurationMs / chunkDurationMs));
        _prefixPaddingChunks = Math.Max(1, (int)(prefixPaddingMs / chunkDurationMs));
    }

    public bool ShouldDropChunk(float[] chunk)
    {
        if (_engine.ContainsSpeech(chunk, _threshold))
        {
            LastChunkHadSpeech = true;
            _consecutiveSilenceChunks = 0;
            return false;
        }

        LastChunkHadSpeech = false;
        _consecutiveSilenceChunks++;
        return _consecutiveSilenceChunks >= Math.Max(_prefixPaddingChunks, _silenceDurationChunks);
    }
}
