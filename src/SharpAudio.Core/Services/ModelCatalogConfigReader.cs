// wiki: backend-architecture#core-abstractions
using System.Text.Json;

namespace SharpAudio.Core.Services;

/// <summary>Model config reading shared by every catalog: the same path (MODEL_CONFIG_PATH,
/// otherwise config.json next to the executable), the same case-insensitive JSON options and the
/// same failure behaviour. Each catalog picks the section it owns.</summary>
public static class ModelCatalogConfigReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// Returns the config text, or null when the file is absent or unreadable.
    public static string? Read(string? configPath, string label)
    {
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load {label} model config: {ex.Message}");
            return null;
        }
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
