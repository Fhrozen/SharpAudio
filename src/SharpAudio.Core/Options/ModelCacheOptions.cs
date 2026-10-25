// wiki: backend-architecture#config-and-env-vars
namespace SharpAudio.Core.Options;

public sealed class ModelCacheOptions
{
    public const string SectionName = "ModelCache";

    public string CacheDirectory { get; set; } = Environment.GetEnvironmentVariable("MODEL_CACHE_DIR")
                                                  ?? Path.Combine(Path.GetTempPath(), "sharp-audio-cache");

    /// <summary>
    /// How many models may be warmed up at the same time. ModelCache locks per model name, so
    /// parallel downloads are safe. Overridable via ModelCache__WarmupConcurrency.
    /// </summary>
    public int WarmupConcurrency { get; set; } = 4;

    /// <summary>
    /// Whether warmup also builds each model's engine (or pre-spawns its worker process) instead of
    /// only downloading files. Overridable via ModelCache__WarmEngines.
    /// </summary>
    public bool WarmEngines { get; set; } = true;
}
