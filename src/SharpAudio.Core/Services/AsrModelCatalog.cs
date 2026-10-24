// wiki: asr-engines#domain-model-and-abstractions
using SharpAudio.Core.Models;

namespace SharpAudio.Core.Services;

public sealed class AsrModelCatalog : ModelCatalogBase<AsrModelDefinition>, IAsrModelCatalog
{
    public AsrModelCatalog()
        : base(ResolveModels())
    {
    }

    private static IReadOnlyList<AsrModelDefinition> ResolveModels()
    {
        var configured = LoadConfiguredModels();
        return configured.Count > 0 ? configured : GetDefaultModels();
    }

    private static IReadOnlyList<AsrModelDefinition> LoadConfiguredModels()
    {
        var json = ModelCatalogConfigReader.Read(ModelCatalogConfigReader.DefaultConfigPath, "ASR");
        if (json is null)
        {
            return [];
        }

        var parsed = ModelCatalogConfigReader.Deserialize<AsrModelCatalogConfig>(json);
        return [.. parsed?.AsrModels?.Where(m => !string.IsNullOrWhiteSpace(m.Name)) ?? []];
    }

    private static IReadOnlyList<AsrModelDefinition> GetDefaultModels() =>
    [
        new()
        {
            Name = "whisper-base",
            DisplayName = "Whisper Base",
            Description = "OpenAI Whisper base multilingual model (GGML), run via whisper.cpp.",
            Engine = "whisper",
            ModelPath = "ggml-base.bin",
            Assets =
            [
                new ModelAsset
                {
                    RelativePath = "ggml-base.bin",
                    Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"
                },
                new ModelAsset
                {
                    RelativePath = "silero_vad.onnx",
                    Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/silero_vad.onnx"
                }
            ],
            SupportedLanguages = [],
            SupportsLanguageAutoDetect = true,
            SupportsVad = true,
            SupportsStreaming = true
        },
        new()
        {
            Name = "nemotron-3.5",
            DisplayName = "Nemotron 3.5 ASR",
            Description = "NVIDIA Nemotron 3.5 streaming ASR (cache-aware FastConformer-RNNT, INT4 ONNX).",
            Engine = "nemotron-3.5",
            ModelPath = "",
            Assets =
            [
                new ModelAsset { RelativePath = "encoder.onnx", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/encoder.onnx" },
                new ModelAsset { RelativePath = "encoder.onnx.data", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/encoder.onnx.data" },
                new ModelAsset { RelativePath = "decoder.onnx", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/decoder.onnx" },
                new ModelAsset { RelativePath = "decoder.onnx.data", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/decoder.onnx.data" },
                new ModelAsset { RelativePath = "joint.onnx", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/joint.onnx" },
                new ModelAsset { RelativePath = "joint.onnx.data", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/joint.onnx.data" },
                new ModelAsset { RelativePath = "audio_processor_config.json", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/audio_processor_config.json" },
                new ModelAsset { RelativePath = "model_config.json", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/model_config.json" },
                new ModelAsset { RelativePath = "genai_config.json", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/genai_config.json" },
                new ModelAsset { RelativePath = "tokenizer.json", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/tokenizer.json" },
                new ModelAsset { RelativePath = "vocab.txt", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/vocab.txt" },
                new ModelAsset { RelativePath = "silero_vad.onnx", Url = "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/silero_vad.onnx" }
            ],
            SupportedLanguages =
            [
                "en", "es", "fr", "it", "pt", "nl", "de", "tr", "ru", "ar", "hi", "ja", "ko", "vi", "uk",
                "pl", "sv", "cs", "nb", "da", "bg", "fi", "hr", "sk", "zh", "hu", "ro", "et",
                "el", "lt", "lv", "mt", "sl", "he", "th", "nn"
            ],
            SupportsLanguageAutoDetect = true,
            SupportsVad = true,
            SupportsStreaming = true
        }
    ];

    private sealed class AsrModelCatalogConfig
    {
        public IReadOnlyList<AsrModelDefinition> AsrModels { get; init; } = [];
    }
}
