// wiki: testing#unit-tests
using SharpAudio.Core.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncConfigReaderTests
{
    // The real config.yaml of 1-800-BAD-CODE/punctuation_fullstop_truecase_english.
    private const string ConfigYaml = """
        # English model
        languages:  [
          "en"
        ]

        # Model has 512 positional embeddings, but only trained on sequences up to 256 tokens
        max_length: 256

        # Not actually used, but included for pipeline generality
        pre_labels: [
          "<NULL>",
          "¿",
        ]

        post_labels: [
          "<NULL>",
          "<ACRONYM>",
          ".",
          ",",
          "?",
        ]
        """;

    [Fact]
    public void Parse_reads_labels_and_window_settings()
    {
        var config = PncConfigReader.Parse(ConfigYaml);

        Assert.Equal("<NULL>,¿", string.Join(",", config.PreLabels));
        Assert.Equal("<NULL>,<ACRONYM>,.,,,?", string.Join(",", config.PostLabels));
        Assert.Equal(256, config.MaxLength);
        Assert.Equal(16, config.Overlap);
    }

    [Fact]
    public void Parse_reads_single_line_lists_and_comments()
    {
        var config = PncConfigReader.Parse("""
            max_length: 128
            overlap: 8
            pad_id: 3
            pre_labels: ["<NULL>", "!"]  # comment
            post_labels: ["<NULL>", "."]
            """
        );

        Assert.Equal(128, config.MaxLength);
        Assert.Equal(8, config.Overlap);
        Assert.Equal(3, config.PadId);
        Assert.Equal(2, config.PostLabels.Count);
    }

    [Fact]
    public void Label_lookup_maps_the_null_label_to_no_punctuation()
    {
        var config = PncConfigReader.Parse(ConfigYaml);

        Assert.Null(config.PreLabel(0));
        Assert.Equal("¿", config.PreLabel(1));
        Assert.Null(config.PostLabel(0));
        Assert.Equal("<ACRONYM>", config.PostLabel(1));
        Assert.Equal(".", config.PostLabel(2));
    }
}
