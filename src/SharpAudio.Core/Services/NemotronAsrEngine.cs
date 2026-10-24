// wiki: asr-engines#nemotron-engine
using System.Buffers.Binary;
using System.Text.Json;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharpAudio.Core.Services;

/// <summary>
/// Cache-aware streaming FastConformer-RNNT ASR engine (encoder + LSTM predictor + joint, 3
/// chained ONNX sessions), mirrors SupertonicTtsEngine's multi-session-in-one-class pattern.
/// All hyperparameters/tensor shapes are read from genai_config.json - NOT
/// audio_processor_config.json, which is missing several required details (lang_id mapping,
/// pre_encode_cache_size, chunk_samples, the correct log_eps value) and was only usable to get
/// the pipeline running without crashing, not to produce accurate transcriptions. This
/// implementation is ported from a validated Python/onnxruntime reference (see
/// pyscripts/nemotron_speech_ort_only.py) after the original config.json-only implementation was
/// found to produce garbled output - the encoder's output was nearly invariant to actual audio
/// content because it was fed lang_id values in the thousands (vocab.txt tag line indices)
/// instead of the small fixed integers (0-104) the model's language embedding actually expects.
/// </summary>
public sealed class NemotronAsrEngine : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly NemotronVocabulary _vocabulary;
    private readonly NemotronFeatureExtractor _featureExtractor;

    private readonly int _sampleRate;
    private readonly int _chunkSamples;
    private readonly int _encoderLayers;
    private readonly int _encoderHidden;
    private readonly int _cacheChannelFrames; // genai_config.json's "left_context"
    private readonly int _convContext;
    private readonly int _decoderLayers;
    private readonly int _decoderHidden;
    private readonly long _blankId;
    private readonly int _maxSymbolsPerStep;
    private readonly float _blankPenalty;
    private readonly string _modelDirectory;
    private readonly string _vadFilename;
    private readonly float _vadThreshold;
    private readonly double _vadSilenceDurationMs;
    private readonly double _vadPrefixPaddingMs;
    private readonly VadOptions _vadOptions;
    private readonly int _nFft;
    private readonly int _hopLength;
    private readonly int _winLength;
    private readonly int _nMels;
    private readonly float _preemphasis;
    private readonly float _logEps;
    private readonly int _preEncodeCacheSize;
    private SileroVadEngine? _vadEngine;
    private bool _disposed;

    /// <summary>Sample rate raw PCM audio must already be at before <see cref="Transcribe"/> or a
    /// streaming session sees it.</summary>
    public int SampleRate => _sampleRate;

    public NemotronAsrEngine(string modelDirectory, VadOptions? vadOptions = null)
    {
        _vadOptions = vadOptions ?? new VadOptions();
        _modelDirectory = modelDirectory;
        var options = new Microsoft.ML.OnnxRuntime.SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        _encoder = new InferenceSession(Path.Combine(modelDirectory, "encoder.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(modelDirectory, "decoder.onnx"), options);
        _joint = new InferenceSession(Path.Combine(modelDirectory, "joint.onnx"), options);

        _vocabulary = new NemotronVocabulary(Path.Combine(modelDirectory, "vocab.txt"));

        var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "genai_config.json")))
            .RootElement.GetProperty("model");

        _sampleRate = model.GetProperty("sample_rate").GetInt32();
        _chunkSamples = model.GetProperty("chunk_samples").GetInt32();
        _blankId = model.GetProperty("blank_id").GetInt64();
        _maxSymbolsPerStep = model.GetProperty("max_symbols_per_step").GetInt32();
        _blankPenalty = model.TryGetProperty("search", out var search) && search.TryGetProperty("blank_penalty", out var penaltyEl)
            ? penaltyEl.GetSingle()
            : 0f;

        var encoderCfg = model.GetProperty("encoder");
        _encoderHidden = encoderCfg.GetProperty("hidden_size").GetInt32();
        _encoderLayers = encoderCfg.GetProperty("num_hidden_layers").GetInt32();
        _cacheChannelFrames = model.GetProperty("left_context").GetInt32();
        _convContext = model.GetProperty("conv_context").GetInt32();

        var decoderCfg = model.GetProperty("decoder");
        _decoderHidden = decoderCfg.GetProperty("hidden_size").GetInt32();
        _decoderLayers = decoderCfg.GetProperty("num_hidden_layers").GetInt32();

        // Defaults mirror the Python reference's NemotronConfig.vad_* fallbacks for models/configs
        // that predate the "vad" section.
        var vadCfg = model.TryGetProperty("vad", out var vadEl) ? vadEl : default;
        _vadFilename = vadCfg.ValueKind == JsonValueKind.Object && vadCfg.TryGetProperty("filename", out var vf)
            ? vf.GetString() ?? "silero_vad.onnx"
            : "silero_vad.onnx";
        _vadThreshold = vadCfg.ValueKind == JsonValueKind.Object && vadCfg.TryGetProperty("threshold", out var vt) ? vt.GetSingle() : 0.5f;
        _vadSilenceDurationMs = vadCfg.ValueKind == JsonValueKind.Object && vadCfg.TryGetProperty("silence_duration_ms", out var vsd) ? vsd.GetDouble() : 3360;
        _vadPrefixPaddingMs = vadCfg.ValueKind == JsonValueKind.Object && vadCfg.TryGetProperty("prefix_padding_ms", out var vpp) ? vpp.GetDouble() : 560;

        _nFft = model.GetProperty("fft_size").GetInt32();
        _hopLength = model.GetProperty("hop_length").GetInt32();
        _winLength = model.GetProperty("win_length").GetInt32();
        _nMels = model.GetProperty("num_mels").GetInt32();
        _preemphasis = model.GetProperty("preemph").GetSingle();
        _logEps = model.GetProperty("log_eps").GetSingle();
        _preEncodeCacheSize = model.GetProperty("pre_encode_cache_size").GetInt32();

        _featureExtractor = CreateFeatureExtractor();
    }

    private NemotronFeatureExtractor CreateFeatureExtractor() => new(
        sampleRate: _sampleRate,
        nFft: _nFft,
        hopLength: _hopLength,
        winLength: _winLength,
        nMels: _nMels,
        preemphasis: _preemphasis,
        logEps: _logEps,
        preEncodeCacheSize: _preEncodeCacheSize);

    public (string Text, string? DetectedLanguage, IReadOnlyList<TranscriptionSegment> Segments) Transcribe(byte[] wavBytes, string? language, bool enableVad = false)
    {
        var samples = WavAudioUtils.ReadMonoFloat(wavBytes, _sampleRate);
        var languageId = NemotronLanguages.Resolve(language);

        var workspace = new TensorWorkspace(this, languageId);
        var (encoderOutputs, regions) = RunEncoderChunks(samples, workspace, enableVad);
        var (tokenIds, tokensPerFrame) = RunRnntGreedyDecode(workspace, encoderOutputs);

        var text = _vocabulary.Decode(tokenIds, out var languageTag);
        // In auto-detect mode the model itself emits the detected language as a leading tag;
        // fall back to it only when the caller didn't already specify a language explicitly.
        var detectedLanguage = language ?? languageTag?.Trim('<', '>');

        var segments = BuildSegments(regions, tokenIds, tokensPerFrame);

        return (text, detectedLanguage, segments);
    }

    /// <summary>Slices the decoded token stream by each region's encoder-frame range (tokens are
    /// emitted in frame order, so a region's frame range maps to a contiguous token slice) and
    /// decodes each region's tokens into its own <see cref="TranscriptionSegment"/>. Regions with
    /// no emitted tokens (e.g. a VAD-kept chunk that produced only blanks) are skipped.</summary>
    private List<TranscriptionSegment> BuildSegments(
        List<(int StartFrame, int EndFrame, double StartSeconds, double EndSeconds)> regions,
        List<int> tokenIds,
        List<int> tokensPerFrame)
    {
        var cumulativeTokens = new int[tokensPerFrame.Count + 1];
        for (var i = 0; i < tokensPerFrame.Count; i++)
        {
            cumulativeTokens[i + 1] = cumulativeTokens[i] + tokensPerFrame[i];
        }

        var segments = new List<TranscriptionSegment>();
        var nextId = 1;
        foreach (var region in regions)
        {
            var tokenStart = cumulativeTokens[region.StartFrame];
            var tokenEnd = cumulativeTokens[region.EndFrame];
            if (tokenEnd <= tokenStart)
            {
                continue;
            }

            var regionText = _vocabulary.Decode(tokenIds.GetRange(tokenStart, tokenEnd - tokenStart));
            if (string.IsNullOrWhiteSpace(regionText))
            {
                continue;
            }

            segments.Add(new TranscriptionSegment(nextId++, region.StartSeconds, region.EndSeconds, regionText));
        }

        return segments;
    }

    /// <summary>Runs the cache-aware encoder loop like the original implementation, additionally
    /// tracking contiguous "active audio" regions (maximal runs of non-VAD-dropped chunks, or one
    /// region spanning everything when VAD is disabled) for segment timestamping.</summary>
    private (List<float[]> Frames, List<(int StartFrame, int EndFrame, double StartSeconds, double EndSeconds)> Regions) RunEncoderChunks(
        float[] samples, TensorWorkspace workspace, bool enableVad)
    {
        _featureExtractor.Reset();

        SileroVadGate? vadGate = null;
        if (enableVad)
        {
            var vadEngine = GetOrCreateVadEngine();
            vadEngine.Reset();
            vadGate = new SileroVadGate(vadEngine, _vadThreshold, _chunkSamples, _sampleRate, _vadSilenceDurationMs, _vadPrefixPaddingMs);
        }

        var allOutputs = new List<float[]>();
        var regions = new List<(int StartFrame, int EndFrame, double StartSeconds, double EndSeconds)>();
        var regionStartFrame = -1;
        var regionStartSeconds = 0.0;
        var regionEndSeconds = 0.0;

        for (var offset = 0; offset < samples.Length; offset += _chunkSamples)
        {
            var chunk = new float[_chunkSamples];
            var available = Math.Min(_chunkSamples, samples.Length - offset);
            Array.Copy(samples, offset, chunk, 0, available);
            // Remaining entries stay zero (right-padding the final, possibly-short chunk).

            if (vadGate?.ShouldDropChunk(chunk) == true)
            {
                if (regionStartFrame >= 0)
                {
                    regions.Add((regionStartFrame, allOutputs.Count, regionStartSeconds, regionEndSeconds));
                    regionStartFrame = -1;
                }

                continue; // skip encoder/decoder inference for a chunk classified as silence
            }

            var melChunk = _featureExtractor.ProcessChunk(chunk);
            var frames = workspace.RunEncoderChunk(melChunk);
            if (frames > 0)
            {
                if (regionStartFrame < 0)
                {
                    regionStartFrame = allOutputs.Count;
                    regionStartSeconds = offset / (double)_sampleRate;
                }

                regionEndSeconds = Math.Min(offset + _chunkSamples, samples.Length) / (double)_sampleRate;
                for (var i = 0; i < frames; i++)
                {
                    allOutputs.Add(workspace.Frame(i).ToArray());
                }
            }
        }

        if (regionStartFrame >= 0)
        {
            regions.Add((regionStartFrame, allOutputs.Count, regionStartSeconds, regionEndSeconds));
        }

        return (allOutputs, regions);
    }

    /// <summary>Persistent ONNX buffers, tensors and input lists for one decode pipeline. One
    /// workspace per owner - a streaming session or a single batch call - so nothing is shared
    /// across threads and the same arrays are reused for every chunk. The encoder caches are the
    /// hot allocations: outputs are copied into the arrays the tensors already wrap instead of
    /// being reallocated per chunk, and output positions are resolved from the session metadata
    /// once so results are indexed rather than searched by name.</summary>
    private sealed class TensorWorkspace
    {
        private readonly NemotronAsrEngine _engine;

        private float[] _audioSignal;
        private int _signalFrames;

        private readonly float[] _cacheLastChannel;
        private readonly float[] _cacheLastTime;
        private readonly float[] _encoderFrames;
        private readonly float[] _encoderFrame;
        private readonly float[] _decoderOutput;
        private readonly float[] _hState;
        private readonly float[] _cState;
        private readonly float[] _hNext;
        private readonly float[] _cNext;

        private readonly long[] _length = new long[1];
        private readonly long[] _cacheLastChannelLen = new long[1];
        private readonly long[] _langId = new long[1];
        private readonly long[] _target = new long[1];

        private DenseTensor<float> _audioSignalTensor;
        private readonly DenseTensor<long> _lengthTensor;
        private readonly DenseTensor<float> _cacheLastChannelTensor;
        private readonly DenseTensor<float> _cacheLastTimeTensor;
        private readonly DenseTensor<long> _cacheLenTensor;
        private readonly DenseTensor<long> _langIdTensor;
        private readonly DenseTensor<long> _targetTensor;
        private readonly DenseTensor<float> _hStateTensor;
        private readonly DenseTensor<float> _cStateTensor;
        private readonly DenseTensor<float> _encoderFrameTensor;
        private readonly DenseTensor<float> _decoderOutputTensor;

        private readonly List<NamedOnnxValue> _encoderInputs;
        private readonly List<NamedOnnxValue> _decoderInputs;
        private readonly List<NamedOnnxValue> _jointInputs;

        private readonly Dictionary<string, int> _encoderOutputs;
        private readonly Dictionary<string, int> _decoderOutputs;
        private readonly Dictionary<string, int> _jointOutputs;

        /// <summary>The predictor's last emitted symbol, i.e. the token fed as the next step's target.</summary>
        public long LastToken
        {
            get => _target[0];
            set => _target[0] = value;
        }

        public TensorWorkspace(NemotronAsrEngine engine, long languageId)
        {
            _engine = engine;
            _langId[0] = languageId;
            _target[0] = engine._blankId;

            var maxFrames = engine._preEncodeCacheSize + engine._chunkSamples / engine._hopLength;
            _audioSignal = new float[maxFrames * engine._nMels];
            _encoderFrames = new float[maxFrames * engine._encoderHidden];
            _encoderFrame = new float[engine._encoderHidden];
            _decoderOutput = new float[engine._decoderHidden];
            _hState = new float[engine._decoderLayers * engine._decoderHidden];
            _cState = new float[engine._decoderLayers * engine._decoderHidden];
            _hNext = new float[engine._decoderLayers * engine._decoderHidden];
            _cNext = new float[engine._decoderLayers * engine._decoderHidden];
            _cacheLastChannel = new float[engine._encoderLayers * engine._cacheChannelFrames * engine._encoderHidden];
            _cacheLastTime = new float[engine._encoderLayers * engine._encoderHidden * engine._convContext];

            // DenseTensor has no span constructor, so each tensor wraps an array whose length is
            // exactly its shape; the audio_signal tensor is rebuilt only when the frame count changes.
            _audioSignalTensor = new DenseTensor<float>(_audioSignal, new[] { 1, maxFrames, engine._nMels });
            _lengthTensor = new DenseTensor<long>(_length, new[] { 1 });
            _cacheLastChannelTensor = new DenseTensor<float>(_cacheLastChannel, new[] { 1, engine._encoderLayers, engine._cacheChannelFrames, engine._encoderHidden });
            _cacheLastTimeTensor = new DenseTensor<float>(_cacheLastTime, new[] { 1, engine._encoderLayers, engine._encoderHidden, engine._convContext });
            _cacheLenTensor = new DenseTensor<long>(_cacheLastChannelLen, new[] { 1 });
            _langIdTensor = new DenseTensor<long>(_langId, new[] { 1 });
            _targetTensor = new DenseTensor<long>(_target, new[] { 1, 1 });
            _hStateTensor = new DenseTensor<float>(_hState, new[] { engine._decoderLayers, 1, engine._decoderHidden });
            _cStateTensor = new DenseTensor<float>(_cState, new[] { engine._decoderLayers, 1, engine._decoderHidden });
            _encoderFrameTensor = new DenseTensor<float>(_encoderFrame, new[] { 1, 1, engine._encoderHidden });
            _decoderOutputTensor = new DenseTensor<float>(_decoderOutput, new[] { 1, 1, engine._decoderHidden });

            _encoderInputs =
            [
                NamedOnnxValue.CreateFromTensor("audio_signal", _audioSignalTensor),
                NamedOnnxValue.CreateFromTensor("length", _lengthTensor),
                NamedOnnxValue.CreateFromTensor("cache_last_channel", _cacheLastChannelTensor),
                NamedOnnxValue.CreateFromTensor("cache_last_time", _cacheLastTimeTensor),
                NamedOnnxValue.CreateFromTensor("cache_last_channel_len", _cacheLenTensor),
                NamedOnnxValue.CreateFromTensor("lang_id", _langIdTensor),
            ];
            _decoderInputs =
            [
                NamedOnnxValue.CreateFromTensor("targets", _targetTensor),
                NamedOnnxValue.CreateFromTensor("h_in", _hStateTensor),
                NamedOnnxValue.CreateFromTensor("c_in", _cStateTensor),
            ];
            _jointInputs =
            [
                NamedOnnxValue.CreateFromTensor("encoder_output", _encoderFrameTensor),
                NamedOnnxValue.CreateFromTensor("decoder_output", _decoderOutputTensor),
            ];

            _encoderOutputs = ResolveOutputs(engine._encoder);
            _decoderOutputs = ResolveOutputs(engine._decoder);
            _jointOutputs = ResolveOutputs(engine._joint);
        }

        /// <summary>Runs the encoder on one already-extracted mel chunk, keeping the caches in the
        /// arrays this workspace owns. Returns the number of valid output frames, readable through
        /// <see cref="Frame"/>.</summary>
        public int RunEncoderChunk(float[][] melChunk)
        {
            var totalFrames = melChunk.Length;
            var nMels = totalFrames > 0 ? melChunk[0].Length : 0;

            if (totalFrames != _signalFrames)
            {
                _audioSignal = new float[totalFrames * nMels];
                _audioSignalTensor = new DenseTensor<float>(_audioSignal, new[] { 1, totalFrames, nMels });
                _encoderInputs[0] = NamedOnnxValue.CreateFromTensor("audio_signal", _audioSignalTensor);
                _signalFrames = totalFrames;
            }

            for (var i = 0; i < totalFrames; i++)
            {
                Array.Copy(melChunk[i], 0, _audioSignal, i * nMels, nMels);
            }

            _length[0] = totalFrames;

            using var results = _engine._encoder.Run(_encoderInputs);
            var outputs = results[_encoderOutputs["outputs"]].AsTensor<float>();
            var encodedLengths = results[_encoderOutputs["encoded_lengths"]].AsTensor<long>()[0];
            CopyTensor(results[_encoderOutputs["cache_last_channel_next"]].AsTensor<float>(), _cacheLastChannel);
            CopyTensor(results[_encoderOutputs["cache_last_time_next"]].AsTensor<float>(), _cacheLastTime);
            _cacheLastChannelLen[0] = results[_encoderOutputs["cache_last_channel_len_next"]].AsTensor<long>()[0];

            var validOutputFrames = (int)Math.Min(encodedLengths, outputs.Dimensions[1]);
            CopyTensor(outputs, _encoderFrames, validOutputFrames * _engine._encoderHidden);
            return validOutputFrames;
        }

        /// <summary>One encoder frame as it was produced by the last <see cref="RunEncoderChunk"/>.</summary>
        public ReadOnlySpan<float> Frame(int index) => _encoderFrames.AsSpan(index * _engine._encoderHidden, _engine._encoderHidden);

        /// <summary>Greedily decodes one encoder frame, keeping the predictor's LSTM state in the
        /// arrays this workspace owns. Appends the tokens the frame emits to <paramref name="tokens"/>
        /// and returns how many it emitted.</summary>
        public int DecodeFrame(ReadOnlySpan<float> encoderFrame, List<int> tokens)
        {
            encoderFrame.CopyTo(_encoderFrame);

            var emitted = 0;
            while (emitted < _engine._maxSymbolsPerStep)
            {
                var predicted = RunDecoderStepAndJoint();
                if (predicted == _engine._blankId)
                {
                    break;
                }

                tokens.Add((int)predicted);
                _target[0] = predicted;

                // The predictor advanced for the symbol it emitted; a blank leaves the state where it was.
                ((Span<float>)_hNext).CopyTo(_hState);
                ((Span<float>)_cNext).CopyTo(_cState);
                emitted++;
            }

            return emitted;
        }

        private long RunDecoderStepAndJoint()
        {
            using var results = _engine._decoder.Run(_decoderInputs);
            var rawOutput = results[_decoderOutputs["decoder_output"]].AsTensor<float>(); // [1, hidden, 1] (batch, hidden, seq)
            for (var d = 0; d < _engine._decoderHidden; d++)
            {
                _decoderOutput[d] = rawOutput[0, d, 0];
            }

            CopyTensor(results[_decoderOutputs["h_out"]].AsTensor<float>(), _hNext);
            CopyTensor(results[_decoderOutputs["c_out"]].AsTensor<float>(), _cNext);

            using var jointResults = _engine._joint.Run(_jointInputs);
            var logits = jointResults[_jointOutputs["joint_output"]].AsTensor<float>(); // [1,1,1,vocab]

            var vocabSize = logits.Dimensions[3];
            var bestId = 0L;
            var bestScore = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                var score = logits[0, 0, 0, v];
                if (v == _engine._blankId)
                {
                    score -= _engine._blankPenalty;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = v;
                }
            }

            return bestId;
        }

        // Output positions resolved once from the session metadata, which lists outputs in the order
        // Run returns them.
        private static Dictionary<string, int> ResolveOutputs(InferenceSession session)
        {
            var map = new Dictionary<string, int>();
            var index = 0;
            foreach (var output in session.OutputMetadata)
            {
                map[output.Key] = index++;
            }

            return map;
        }

        private static void CopyTensor(Tensor<float> tensor, float[] target, int count = -1)
        {
            var limit = count < 0 ? target.Length : count;
            var i = 0;
            foreach (var value in tensor)
            {
                if (i >= limit)
                {
                    break;
                }

                target[i++] = value;
            }
        }
    }

    private SileroVadEngine GetOrCreateVadEngine() =>
        _vadEngine ??= new SileroVadEngine(Path.Combine(_modelDirectory, _vadFilename), _sampleRate);

    private (List<int> TokenIds, List<int> TokensPerFrame) RunRnntGreedyDecode(TensorWorkspace workspace, List<float[]> encoderOutputs)
    {
        var tokenIds = new List<int>();
        var tokensPerFrame = new List<int>(encoderOutputs.Count);

        foreach (var frame in encoderOutputs)
        {
            var before = tokenIds.Count;
            workspace.DecodeFrame(frame, tokenIds);
            tokensPerFrame.Add(tokenIds.Count - before);
        }

        return (tokenIds, tokensPerFrame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _encoder.Dispose();
        _decoder.Dispose();
        _joint.Dispose();
        _vadEngine?.Dispose();
        _disposed = true;
    }

    /// <summary>Creates a stateful streaming session - a fresh feature extractor and cache/decoder
    /// state, plus a dedicated VAD engine if requested (never the shared <see cref="_vadEngine"/>,
    /// since its internal LSTM state can't safely be shared across concurrent sessions).</summary>
    public StreamingSession CreateStreamingSession(string? language, bool enableVad, double segmentSeconds, bool enableEndpointing = false) =>
        new(this, NemotronLanguages.Resolve(language), enableVad, segmentSeconds, enableEndpointing);

    /// <summary>
    /// Incremental cache-aware streaming session - mirrors the Python reference's
    /// NemotronOrtPipeline.process_chunk (encoder + greedy RNNT decode per chunk, threading cache/
    /// decoder state across calls) rather than <see cref="Transcribe"/>'s whole-utterance-at-once
    /// batch call. As a nested class it can reach the outer engine's private ONNX sessions and
    /// per-chunk primitives directly. Owns one <see cref="TensorWorkspace"/>, so its buffers are
    /// never shared with another session or a batch call.
    /// </summary>
    public sealed class StreamingSession : IStreamingTranscriptionSession
    {
        private readonly NemotronAsrEngine _engine;
        private readonly NemotronFeatureExtractor _featureExtractor;
        private readonly SileroVadEngine? _vadEngine;
        private readonly SileroVadGate? _vadGate;
        private readonly VadEndpointer? _endpointer;
        private double _audioElapsedMs;
        private double _lastSpeechMs;
        private readonly ByteRingBuffer _pending;
        private readonly byte[] _chunkScratch;
        private readonly float[] _samplesScratch;
        private readonly List<int> _tokenIds = [];
        private readonly List<int> _wordStarts = [];
        private readonly double _segmentSeconds;

        private readonly TensorWorkspace _workspace;
        private bool _finished;
        private int _committedTokenCount;
        private double _segmentElapsedSeconds;

        internal StreamingSession(NemotronAsrEngine engine, long languageId, bool enableVad, double segmentSeconds, bool enableEndpointing)
        {
            _engine = engine;
            _segmentSeconds = segmentSeconds;
            _featureExtractor = engine.CreateFeatureExtractor();
            _workspace = new TensorWorkspace(engine, languageId);
            _pending = new ByteRingBuffer(engine._chunkSamples * 2);
            _chunkScratch = new byte[engine._chunkSamples * 2];
            _samplesScratch = new float[engine._chunkSamples];

            if (enableVad)
            {
                _vadEngine = new SileroVadEngine(Path.Combine(engine._modelDirectory, engine._vadFilename), engine._sampleRate);
                _vadGate = new SileroVadGate(_vadEngine, engine._vadThreshold, engine._chunkSamples, engine._sampleRate, engine._vadSilenceDurationMs, engine._vadPrefixPaddingMs);
                // Endpointing needs the gate's per-chunk speech decision, so it only works with VAD on.
                if (enableEndpointing)
                {
                    _endpointer = new VadEndpointer(engine._vadOptions);
                }
            }
        }

        public int SampleRate => _engine._sampleRate;

        public Task<IReadOnlyList<StreamingUpdate>> ProcessChunkAsync(byte[] pcm16Chunk, CancellationToken cancellationToken)
        {
            if (_finished)
            {
                return Task.FromResult<IReadOnlyList<StreamingUpdate>>([]);
            }

            _pending.Add(pcm16Chunk);
            var chunkByteSize = _engine._chunkSamples * 2;
            var chunkDurationMs = _engine._chunkSamples / (double)_engine._sampleRate * 1000.0;
            var updates = new List<StreamingUpdate>();

            while (_pending.Length >= chunkByteSize)
            {
                _pending.CopyTo(_chunkScratch);
                _pending.Advance(chunkByteSize);

                BytesToFloatSamples(_chunkScratch, _samplesScratch);
                var producedNewTokens = DecodeOneChunk(_samplesScratch);
                _audioElapsedMs += chunkDurationMs;

                // Only count this chunk against the segment's time budget once the segment has
                // actually started producing text - leading silence before the user starts talking
                // must not eat into the budget (was causing premature commits right after the
                // first word or two).
                if (_tokenIds.Count > _committedTokenCount)
                {
                    _segmentElapsedSeconds += chunkDurationMs / 1000.0;
                }

                if (producedNewTokens)
                {
                    updates.Add(BuildUpdate());
                }

                if (_endpointer != null)
                {
                    if (_vadGate!.LastChunkHadSpeech)
                    {
                        _lastSpeechMs = _audioElapsedMs;
                    }

                    if (_endpointer.Poll(_audioElapsedMs, _lastSpeechMs) && _tokenIds.Count > _committedTokenCount)
                    {
                        updates.Add(CommitSegment(isEndpointFinal: true));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<StreamingUpdate>>(updates);
        }

        /// <summary>Decodes only the tokens since the last committed segment (small slice, not the
        /// whole conversation) and, if the segment boundary policy says to commit, resets the
        /// commit offset/timer - the underlying encoder/decoder cache state is left untouched.</summary>
        private StreamingUpdate BuildUpdate()
        {
            var segmentTokens = _tokenIds.GetRange(_committedTokenCount, _tokenIds.Count - _committedTokenCount);
            var segmentText = _engine._vocabulary.Decode(segmentTokens);

            if (SegmentBoundaryPolicy.ShouldCommit(_segmentElapsedSeconds, _segmentSeconds, segmentText))
            {
                return CommitSegment(isEndpointFinal: false);
            }

            return new StreamingUpdate(segmentText, IsSegmentFinal: false);
        }

        private StreamingUpdate CommitSegment(bool isEndpointFinal)
        {
            // An endpoint means the utterance ended, so the last word is complete and the whole
            // pending range can be committed; a policy commit holds back a possibly-continuing word.
            var commitBoundary = isEndpointFinal ? _tokenIds.Count : FindSafeCommitBoundary();
            var committedTokens = _tokenIds.GetRange(_committedTokenCount, commitBoundary - _committedTokenCount);
            var committedText = _engine._vocabulary.Decode(committedTokens);

            _committedTokenCount = commitBoundary;
            _segmentElapsedSeconds = 0;
            return new StreamingUpdate(committedText, IsSegmentFinal: true, isEndpointFinal);
        }

        /// <summary>Finds the largest safe commit boundary in the pending token range that doesn't
        /// split a word - the start of the last word is held back (it may still be continued by
        /// the next chunk's tokens). Falls back to committing everything if the whole pending
        /// range is a single word. The word-start indices are pushed as tokens arrive, so this reads
        /// the end of that stack instead of rescanning every pending token on each update.</summary>
        private int FindSafeCommitBoundary()
        {
            while (_wordStarts.Count > 0 && _wordStarts[^1] <= _committedTokenCount)
            {
                _wordStarts.RemoveAt(_wordStarts.Count - 1);
            }

            return _wordStarts.Count > 0 ? _wordStarts[^1] : _tokenIds.Count;
        }

        public Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            if (!_finished && _pending.Length > 0)
            {
                // Right-pad the trailing partial chunk with zeros, same as the batch path's last chunk.
                _pending.CopyTo(_chunkScratch);
                BytesToFloatSamples(_chunkScratch, _samplesScratch);
                DecodeOneChunk(_samplesScratch);
                _pending.Clear();
            }

            _finished = true;
            var trailingTokens = _tokenIds.GetRange(_committedTokenCount, _tokenIds.Count - _committedTokenCount);
            return Task.FromResult(_engine._vocabulary.Decode(trailingTokens));
        }

        private bool DecodeOneChunk(float[] chunk)
        {
            if (_vadGate?.ShouldDropChunk(chunk) == true)
            {
                return false;
            }

            var melChunk = _featureExtractor.ProcessChunk(chunk);
            var frames = _workspace.RunEncoderChunk(melChunk);

            var producedNewTokens = false;
            for (var i = 0; i < frames; i++)
            {
                var before = _tokenIds.Count;
                var emitted = _workspace.DecodeFrame(_workspace.Frame(i), _tokenIds);
                if (emitted == 0)
                {
                    continue;
                }

                for (var j = before; j < _tokenIds.Count; j++)
                {
                    if (_engine._vocabulary.IsWordStart(_tokenIds[j]))
                    {
                        _wordStarts.Add(j);
                    }
                }

                producedNewTokens = true;
            }

            return producedNewTokens;
        }

        /// <summary>Decodes PCM16 bytes into the session's reusable sample buffer, zero-padding the
        /// trailing slots so a partial final chunk matches the batch path.</summary>
        private static void BytesToFloatSamples(ReadOnlySpan<byte> pcm16Bytes, float[] target)
        {
            var sampleCount = Math.Min(target.Length, pcm16Bytes.Length / 2);
            for (var i = 0; i < sampleCount; i++)
            {
                target[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16Bytes.Slice(i * 2, 2)) / 32768f;
            }

            for (var i = sampleCount; i < target.Length; i++)
            {
                target[i] = 0f;
            }
        }

        public void Dispose() => _vadEngine?.Dispose();
    }
}
