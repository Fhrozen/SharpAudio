// wiki: testing#integration-tests
namespace SharpAudio.Api.IntegrationTests.Support;

/// <summary>Opt-in gate for the golden SentencePiece parity tests, which need the real
/// <c>spe_32k_lc_en.model</c> asset. Set <c>PNC_TOKENIZER_MODEL</c> to the model file path to opt in.</summary>
public static class PncModelTestGate
{
    public static string? ModelPath => Environment.GetEnvironmentVariable("PNC_TOKENIZER_MODEL");

    public static bool IsEnabled => !string.IsNullOrEmpty(ModelPath) && File.Exists(ModelPath);

    public const string SkipReason =
        "Skipped by default: needs the real SentencePiece .model asset. Set PNC_TOKENIZER_MODEL to the " +
        "path of spe_32k_lc_en.model to opt in.";
}
