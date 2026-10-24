// wiki: asr-engines#whisper-engine
using System.Text;
using SharpAudio.Core.Models;
using Whisper.net;

namespace SharpAudio.Core.Services;

/// <summary>
/// Wraps a single Whisper.net (whisper.cpp/GGML) model file for one-shot audio transcription.
/// Mirrors the single-ONNX-session pooling pattern used by KokoroTtsEngine.
/// </summary>
public sealed class WhisperAsrEngine : IDisposable
{
    private readonly WhisperFactory _factory;

    // A pooled engine is shared by concurrent requests, so runs are serialised here: the VAD gate now
    // covers segmentation only, which means two requests can otherwise reach the same engine at once.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WhisperAsrEngine(string ggmlModelPath)
    {
        _factory = WhisperFactory.FromPath(ggmlModelPath);
    }

    public async Task<(string Text, string? DetectedLanguage, IReadOnlyList<TranscriptionSegment> Segments)> TranscribeAsync(
        byte[] wavBytes, string? language, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var builderFactory = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language);

            using var processor = builderFactory.Build();

            // Whisper.net requires exactly 16kHz input and does not resample internally.
            var resampledWav = WavAudioUtils.ResampleToMono16kWav(wavBytes);
            using var audioStream = new MemoryStream(resampledWav);

            var textBuilder = new StringBuilder();
            var segments = new List<TranscriptionSegment>();
            string? detectedLanguage = string.IsNullOrWhiteSpace(language) ? null : language;
            var segmentId = 1;

            await foreach (var segment in processor.ProcessAsync(audioStream, cancellationToken))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.Append(' ');
                }

                var segmentText = segment.Text.Trim();
                textBuilder.Append(segmentText);
                detectedLanguage ??= segment.Language;
                segments.Add(new TranscriptionSegment(segmentId++, segment.Start.TotalSeconds, segment.End.TotalSeconds, segmentText));
            }

            return (textBuilder.ToString().Trim(), detectedLanguage, segments);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _factory.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}
