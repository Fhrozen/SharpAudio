// wiki: testing#benchmarks
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;
using SharpAudio.Core.Services.Pnc;

// Baseline hot-path benchmarks for the refactor plan (docs plan Phase 0.2).
// Deliberately excludes anything that touches ONNX, Whisper or espeak so the whole
// suite runs in CI without model weights.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(5));

BenchmarkSwitcher.FromAssembly(typeof(WavWriterBenchmarks).Assembly).Run(args, config);

[MemoryDiagnoser]
public class WavWriterBenchmarks
{
    private float[] _samples = null!;
    private byte[] _pcm = null!;

    [GlobalSetup]
    public void Setup()
    {
        _samples = new float[3000]; // ~3 s VAD span at 16 kHz
        for (var i = 0; i < _samples.Length; i++)
        {
            _samples[i] = (float)(Math.Sin(i * 0.01) * 0.5);
        }

        _pcm = new byte[_samples.Length * 2];
        for (var i = 0; i < _samples.Length; i++)
        {
            var value = (short)(Math.Clamp(_samples[i], -1f, 1f) * short.MaxValue);
            _pcm[i * 2] = (byte)(value & 0xFF);
            _pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
    }

    [Benchmark]
    public byte[] EncodeMono16kWav() => WavAudioUtils.EncodeMono16kWav(_samples);

    [Benchmark]
    public byte[] WrapPcm16MonoAsWav() => WavAudioUtils.WrapPcm16MonoAsWav(_pcm, sampleRate: 16000);

    [Benchmark]
    public double GetDurationSeconds()
    {
        var wav = WavAudioUtils.WrapPcm16MonoAsWav(_pcm, sampleRate: 16000);
        return WavAudioUtils.GetDurationSeconds(wav);
    }
}

[MemoryDiagnoser]
public class VadPipelineBenchmarks
{
    private float[] _probabilities = null!;
    private float[] _window = null!;
    private VadOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        _options = new VadOptions();
        _probabilities = new float[5000]; // ~50 s of 10 ms frames
        var rng = new Random(1);
        for (var i = 0; i < _probabilities.Length; i++)
        {
            _probabilities[i] = (i / 500) % 3 == 0 ? 0.05f : 0.85f + (float)rng.NextDouble() * 0.1f;
        }

        _window = new float[160];
        for (var i = 0; i < _window.Length; i++)
        {
            _window[i] = 0.7f;
        }
    }

    [Benchmark]
    public IReadOnlyList<SpeechSpan> Segment() => VadSegmenter.Segment(_probabilities, frameMs: 10, _options);

    [Benchmark]
    public int CompleteWindows()
    {
        var stream = new VadWindowStream(160, 16000);
        var count = 0;
        for (var chunk = 0; chunk < 30; chunk++)
        {
            foreach (var _ in stream.CompleteWindows(_window))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    public int TryNextWindowSpan()
    {
        var stream = new VadWindowStream(160, 16000);
        var samples = 0;
        for (var chunk = 0; chunk < 30; chunk++)
        {
            for (var offset = 0; offset < _window.Length;)
            {
                if (!stream.TryNextWindow(_window.AsSpan(offset), out var window, out var consumed))
                {
                    break;
                }

                offset += consumed;
                samples += window.Length;
            }
        }

        return samples;
    }
}

[MemoryDiagnoser]
public class TextSanitizerBenchmarks
{
    private string _markdown = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            sb.Append("## Heading ").Append(i).Append('\n');
            sb.Append("- bullet *emphasis* and `code` (parenthesis) \n");
            sb.Append("[link](https://example.com/path) then more text with  extra   spaces.\n");
            sb.Append("Sentence with — em dash and ， CJK comma.\n\n\n\n");
        }

        _markdown = sb.ToString();
    }

    [Benchmark]
    public string Sanitize() => TextSanitizer.Sanitize(_markdown);
}

[MemoryDiagnoser]
public class TokenizerBenchmarks
{
    private UnigramSentencePieceTokenizer _tokenizer = null!;
    private string _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        var pieces = new List<SentencePiecePiece>();
        pieces.Add(new SentencePiecePiece("<unk>", 0f, SentencePiecePieceType.Unknown));
        pieces.Add(new SentencePiecePiece("<s>", 0f, SentencePiecePieceType.Control));
        pieces.Add(new SentencePiecePiece("</s>", 0f, SentencePiecePieceType.Control));
        pieces.Add(new SentencePiecePiece("<pad>", 0f, SentencePiecePieceType.Control));

        for (var c = 0; c < 128; c++)
        {
            pieces.Add(new SentencePiecePiece(((char)c).ToString(), 1f, SentencePiecePieceType.Normal));
        }

        for (var word = 0; word < 900; word++)
        {
            pieces.Add(new SentencePiecePiece("▁" + Word(word), 3f, SentencePiecePieceType.Normal));
        }

        for (var word = 0; word < 900; word++)
        {
            pieces.Add(new SentencePiecePiece(Word(word), 2.5f, SentencePiecePieceType.Normal));
        }

        _tokenizer = new UnigramSentencePieceTokenizer(pieces, byteFallback: true);

        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            sb.Append("the quick brown fox jumps over the lazy dog while nobody watches closely ");
        }

        _text = sb.ToString();
    }

    private static string Word(int index)
    {
        var letters = "abcdefghijklmnopqrstuvwxyz";
        var length = 3 + index % 6;
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(letters[(index * 7 + i * 11) % letters.Length]);
        }

        return sb.ToString();
    }

    [Benchmark]
    public IReadOnlyList<TokenWord> Encode() => _tokenizer.Encode(_text);
}

[MemoryDiagnoser]
public class PncWindowPlannerBenchmarks
{
    private int[] _ids = null!;
    private PncModelConfig _config = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ids = new int[4000];
        for (var i = 0; i < _ids.Length; i++)
        {
            _ids[i] = i % 512;
        }

        _config = new PncModelConfig
        {
            PreLabels = ["<NULL>", "<COMMA>"],
            PostLabels = ["<NULL>", "<PERIOD>"],
            MaxLength = 256,
            Overlap = 32,
            PadId = 0,
            UnkId = 1,
        };
    }

    [Benchmark]
    public IReadOnlyList<PncWindow> Plan() => PncWindowPlanner.Plan(_ids, _config);
}

/// <summary>Shape baseline for the pending-buffer pattern used by
/// <c>NemotronAsrEngine</c> (<c>GetRange(...).ToArray()</c> then <c>RemoveRange</c> on a
/// <c>List&lt;byte&gt;</c>). Work per chunk grows with the buffer, so the cost is quadratic in
/// stream length. Measured at three sizes so the quadratic term is visible; the ring-buffer
/// rewrite in Phase 4 should make the three rows grow linearly instead.</summary>
[MemoryDiagnoser]
public class PendingBufferShapeBenchmarks
{
    private byte[] _buffer = null!;

    [Params(1000, 5000, 20000)]
    public int ChunkCount;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[ChunkCount * 320];
        for (var i = 0; i < ChunkCount * 320; i++)
        {
            _buffer[i] = (byte)(i % 256);
        }
    }

    [Benchmark]
    public int DrainCurrentPattern()
    {
        var buffer = new List<byte>(_buffer);
        var total = 0;
        for (var chunk = 0; chunk < ChunkCount; chunk++)
        {
            var slice = buffer.GetRange(0, 320).ToArray();
            buffer.RemoveRange(0, 320);
            total += slice.Length;
        }

        return total;
    }

    [Benchmark]
    public int DrainRingBuffer()
    {
        var ring = new ByteRingBuffer(_buffer.Length);
        ring.Add(_buffer);
        var scratch = new byte[320];
        var total = 0;
        for (var chunk = 0; chunk < ChunkCount; chunk++)
        {
            ring.CopyTo(scratch);
            ring.Advance(320);
            total += scratch.Length;
        }

        return total;
    }
}

[MemoryDiagnoser]
public class ModelCacheEnsureBenchmarks
{
    private ModelCache _cache = null!;
    private AsrModelDefinition _model = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharp-audio-baseline-cache");
        _cache = new ModelCache(new NoopHttpClientFactory(), Options.Create(new ModelCacheOptions { CacheDirectory = directory }), NullLogger<ModelCache>.Instance);

        _model = new AsrModelDefinition
        {
            Name = "baseline-model",
            Engine = "whisper",
            Assets = Enumerable.Range(0, 12).Select(i => new ModelAsset { RelativePath = $"part{i}.onnx", Url = "https://example.invalid/part" }).ToList(),
        };

        for (var i = 0; i < 12; i++)
        {
            var path = Path.Combine(directory, _model.Name, $"part{i}.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "cached");
        }

        await _cache.EnsureModelAsync(_model, CancellationToken.None);
    }

    [Benchmark]
    public async Task<string> EnsureModelAlreadyCached() => await _cache.EnsureModelAsync(_model, CancellationToken.None);
}

/// <summary>Keeps the benchmark free of network: every asset already exists on disk, so the
/// fast path is the one being measured.</summary>
internal sealed class NoopHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new NoopHandler());

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
