// wiki: pnc#wiring
using System.Text.Json;
using SharpAudio.Api.Models;

namespace SharpAudio.Api.Services.Pnc;

/// <summary>PnC model catalog (mirrors <see cref="AsrModelCatalog"/>): reads the "pncModels" section of
/// config.json so the model swap in Phase F needs no code change.</summary>
public sealed class PncModelCatalog : IPncModelCatalog
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private readonly IReadOnlyDictionary<string, PncModelDefinition> _models;
    private readonly PncModelDefinition _defaultModel;

    public PncModelCatalog() : this(null)
    {
    }

    /// <summary>Optional explicit config path; otherwise MODEL_CONFIG_PATH or the default config.json.</summary>
    public PncModelCatalog(string? configPath)
    {
        var configuredModels = LoadConfiguredModels(configPath);
        var models = configuredModels.Count > 0 ? configuredModels : GetDefaultModels();
        _models = models.ToDictionary(m => m.Name, Comparer);
        _defaultModel = models[0];
    }

    public IReadOnlyCollection<PncModelDefinition> GetSupportedModels() => _models.Values.ToArray();

    public bool TryGetModel(string modelName, out PncModelDefinition? model)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            model = _defaultModel;
            return true;
        }

        return _models.TryGetValue(modelName, out model);
    }

    private static IReadOnlyList<PncModelDefinition> LoadConfiguredModels(string? configPath)
    {
        try
        {
            var path = configPath
                       ?? Environment.GetEnvironmentVariable("MODEL_CONFIG_PATH")
                       ?? Path.Combine(AppContext.BaseDirectory, "config.json");

            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<PncModelCatalogConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed?.PncModels?
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .ToArray() ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load PnC model config: {ex.Message}");
            return [];
        }
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
