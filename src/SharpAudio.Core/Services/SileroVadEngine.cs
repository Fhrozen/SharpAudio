// wiki: asr-engines#vad-support
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharpAudio.Core.Services;

/// <summary>Raw Silero VAD ONNX session wrapper - ported from the validated Python reference's
/// SileroVadOrt (pyscripts/nemotron_speech_ort_only.py). Tensor names ("input"/"state"/"sr" in,
/// "output"/"stateN" out) and the (2,1,128) LSTM state shape are Silero VAD's own fixed ONNX
/// export contract, not something read from any config file.</summary>
public interface ISileroVadEngine
{
    /// <summary>Resets the LSTM state/context carried between calls - call once per utterance.</summary>
    void Reset();

    /// <summary>Runs Silero VAD over fixed-size windows of <paramref name="samples"/>, returning
    /// true if any window's speech probability meets <paramref name="threshold"/>.</summary>
    bool ContainsSpeech(float[] samples, float threshold);

    /// <summary>Window size in samples, i.e. one probability per this many samples.</summary>
    int FrameSamples { get; }

    /// <summary>Duration of one frame in milliseconds (32 ms at both supported rates).</summary>
    double FrameDurationMs { get; }

    /// <summary>Sample rate the engine was created for, so callers can map span times to samples.</summary>
    int SampleRate { get; }

    /// <summary>One speech probability per complete window. A trailing partial window is carried to
    /// the next call; use <see cref="FlushProbabilities"/> for the last one.</summary>
    float[] GetProbabilities(float[] samples);

    /// <summary>Probability of the zero-padded final window, empty when nothing is carried over.</summary>
    float[] FlushProbabilities();
}

/// <summary>Raw Silero VAD ONNX session wrapper - ported from the validated Python reference's
/// SileroVadOrt (pyscripts/nemotron_speech_ort_only.py). Tensor names ("input"/"state"/"sr" in,
/// "output"/"stateN" out) and the (2,1,128) LSTM state shape are Silero VAD's own fixed ONNX
/// export contract, not something read from any config file.
///
/// Single-owner: the LSTM state, the carried context and the window stream are per-utterance
/// state, so one engine instance serves one caller at a time. The input buffer, the state/context
/// arrays and the input tensors are allocated once and reused for every window; the state output
/// is copied into the array the next window reads rather than replacing it, which is what makes
/// reusing the tensor safe.</summary>
public sealed class SileroVadEngine : ISileroVadEngine, IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _sampleRate;
    private readonly int _windowSize;
    private readonly int _contextSize;
    private readonly VadWindowStream _windowStream;

    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context;
    private readonly float[] _input;

    private readonly DenseTensor<float> _inputTensor;
    private readonly DenseTensor<float> _stateTensor;
    private readonly DenseTensor<long> _srTensor;
    private readonly List<NamedOnnxValue> _inputs;

    // Output positions resolved once from the session metadata (Run returns outputs in metadata
    // order), so each window indexes the results instead of searching them by name.
    private readonly int _outputIndex;
    private readonly int _stateIndex;

    private bool _disposed;

    public SileroVadEngine(string modelPath, int sampleRate)
    {
        if (sampleRate != 8000 && sampleRate != 16000)
        {
            throw new ArgumentException($"SileroVad only supports 8000/16000 Hz, got {sampleRate}.");
        }

        _session = new InferenceSession(modelPath);
        _sampleRate = sampleRate;

        var factor = sampleRate / 8000;
        _windowSize = 256 * factor;
        _contextSize = 32 * factor;
        _windowStream = new VadWindowStream(_windowSize, sampleRate);

        _input = new float[_contextSize + _windowSize];
        _context = new float[_contextSize];
        _inputTensor = new DenseTensor<float>(_input, [1, _contextSize + _windowSize]);
        _stateTensor = new DenseTensor<float>(_state, [2, 1, 128]);
        _srTensor = new DenseTensor<long>(new long[] { _sampleRate }, Array.Empty<int>());
        _inputs = [
            NamedOnnxValue.CreateFromTensor("input", _inputTensor),
            NamedOnnxValue.CreateFromTensor("state", _stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", _srTensor),
        ];

        var names = new List<string>();
        foreach (var output in _session.OutputMetadata)
        {
            names.Add(output.Key);
        }

        _outputIndex = names.IndexOf("output");
        _stateIndex = names.IndexOf("stateN");
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
        _windowStream.Reset();
    }

    public int FrameSamples => _windowStream.FrameSamples;

    public double FrameDurationMs => _windowStream.FrameDurationMs;

    public int SampleRate => _sampleRate;

    public bool ContainsSpeech(float[] samples, float threshold)
    {
        var offset = 0;
        while (offset + _windowSize <= samples.Length)
        {
            if (ProcessWindow(samples.AsSpan(offset, _windowSize)) >= threshold)
            {
                return true;
            }

            offset += _windowSize;
        }

        if (offset < samples.Length)
        {
            var padded = new float[_windowSize];
            Array.Copy(samples, offset, padded, 0, samples.Length - offset);
            if (ProcessWindow(padded) >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    public float[] GetProbabilities(float[] samples)
    {
        var probabilities = new List<float>();
        for (var offset = 0; offset < samples.Length;)
        {
            if (!_windowStream.TryNextWindow(samples.AsSpan(offset), out var window, out var consumed))
            {
                break;
            }

            offset += consumed;
            probabilities.Add(ProcessWindow(window));
        }

        return [.. probabilities];
    }

    public float[] FlushProbabilities()
    {
        var window = _windowStream.FlushPaddedWindow();
        return window is null ? [] : [ProcessWindow(window)];
    }

    private float ProcessWindow(ReadOnlySpan<float> window)
    {
        ((ReadOnlySpan<float>)_context).CopyTo(_input);
        window.CopyTo(_input.AsSpan(_contextSize));

        using var results = _session.Run(_inputs);
        var output = results[_outputIndex].AsTensor<float>();
        var stateN = results[_stateIndex].AsTensor<float>();

        // Copy into the array the state tensor already wraps, so the next window reuses it.
        var i = 0;
        foreach (var value in stateN)
        {
            _state[i++] = value;
        }

        window[^_contextSize..].CopyTo(_context);

        return output[0, 0];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}
