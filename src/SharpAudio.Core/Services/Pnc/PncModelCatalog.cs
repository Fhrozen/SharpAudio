// wiki: pnc#wiring
using SharpAudio.Core.Models;
using SharpAudio.Core.Services;

namespace SharpAudio.Core.Services.Pnc;

/// <summary>PnC model catalog: reads the "pncModels" section of config.json so the model swap in
/// Phase F needs no code change. Storage and lookup come from <see cref="ModelCatalogBase{T}"/>; this
/// class adds the default-model fallback for an empty name.</summary>
public sealed class PncModelCatalog : ModelCatalogBase<PncModelDefinition>, IPncModelCatalog
{
    private readonly PncModelDefinition _defaultModel;

    public PncModelCatalog() : this((string?)null)
    {
    }

    /// <summary>Optional explicit config path; otherwise MODEL_CONFIG_PATH or the default config.json.</summary>
    public PncModelCatalog(string? configPath) : this(ResolveModels(configPath))
    {
    }

    private PncModelCatalog(IReadOnlyList<PncModelDefinition> models) : base(models)
    {
        _defaultModel = models[0];
    }

    private static IReadOnlyList<PncModelDefinition> ResolveModels(string? configPath)
    {
        var configured = LoadConfiguredModels(configPath);
        return configured.Count > 0 ? configured : GetDefaultModels();
    }

    /// <summary>Resolves a model by name; an empty name falls back to the default model.</summary>
    public override bool TryGetModel(string modelName, out PncModelDefinition? model)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            model = _defaultModel;
            return true;
        }

        return base.TryGetModel(modelName, out model);
    }

    private static IReadOnlyList<PncModelDefinition> LoadConfiguredModels(string? configPath)
    {
        var json = ModelCatalogConfigReader.Read(
            configPath ?? Environment.GetEnvironmentVariable("MODEL_CONFIG_PATH"), "PnC");
        if (json is null)
        {
            return [];
        }

        var parsed = ModelCatalogConfigReader.Deserialize<PncModelCatalogConfig>(json);
        return [.. parsed?.PncModels?.Where(m => !string.IsNullOrWhiteSpace(m.Name)) ?? []];
    }

    private static IReadOnlyList<PncModelDefinition> GetDefaultModels() =>
    [
        new()
        {
            Name = "pnc-en",
            DisplayName = "PnC English",
            Description = "Punctuation, capitalisation and sentence boundary model (ONNX, English).",
            Engine = "pnc",
            OnnxPath = "punct_cap_seg_en.onnx",
            TokenizerPath = "spe_32k_lc_en.model",
            ConfigPath = "config.yaml",
            SupportedLanguages = ["en"],
            Assets =
            [
                new ModelAsset
                {
                    RelativePath = "punct_cap_seg_en.onnx",
                    Url = "https://huggingface.co/1-800-BAD-CODE/punctuation_fullstop_truecase_english/resolve/main/punct_cap_seg_en.onnx"
                },
                new ModelAsset
                {
                    RelativePath = "spe_32k_lc_en.model",
                    Url = "https://huggingface.co/1-800-BAD-CODE/punctuation_fullstop_truecase_english/resolve/main/spe_32k_lc_en.model"
                },
                new ModelAsset
                {
                    RelativePath = "config.yaml",
                    Url = "https://huggingface.co/1-800-BAD-CODE/punctuation_fullstop_truecase_english/resolve/main/config.yaml"
                }
            ]
        }
    ];

    private sealed class PncModelCatalogConfig
    {
        public IReadOnlyList<PncModelDefinition> PncModels { get; init; } = [];
    }
}
