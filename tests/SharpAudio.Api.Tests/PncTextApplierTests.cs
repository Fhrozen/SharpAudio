// wiki: testing#unit-tests
using SharpAudio.Core.Models;
using SharpAudio.Core.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncTextApplierTests
{
    [Fact]
    public void Apply_joins_sentences_with_spaces()
    {
        var engine = new FakeEngine(_ => ["hello there", "world"]);

        Assert.Equal("hello there world", PncTextApplier.Apply(engine, "hello there world"));
    }

    [Fact]
    public void Apply_returns_the_input_when_the_engine_returns_nothing()
    {
        var engine = new FakeEngine(_ => []);

        Assert.Equal("hello", PncTextApplier.Apply(engine, "hello"));
    }

    [Fact]
    public void Apply_punctuates_text_and_segments_and_keeps_timestamps()
    {
        var engine = new FakeEngine(text => [char.ToUpper(text[0]) + text[1..] + "."]);
        var result = new TranscriptionResult(
            "hi there",
            null,
            1.0,
            2.0,
            8,
            [new TranscriptionSegment(0, 0.0, 1.0, "hi there"), new TranscriptionSegment(1, 1.0, 2.0, "you")]);

        var punctuated = PncTextApplier.Apply(engine, result);

        Assert.Equal("Hi there.", punctuated.Text);
        Assert.Equal(9, punctuated.CharacterCount);
        Assert.Equal("Hi there.", punctuated.Segments![0].Text);
        Assert.Equal("You.", punctuated.Segments[1].Text);
        Assert.Equal(1, punctuated.Segments[1].Id);
        Assert.Equal(1.0, punctuated.Segments[1].Start);
        Assert.Equal(2.0, punctuated.Segments[1].End);
    }

    [Fact]
    public void Apply_keeps_segments_null_when_the_result_has_none()
    {
        var engine = new FakeEngine(_ => ["hi"]);
        var result = new TranscriptionResult("hi", null, 1.0, 1.0, 2);

        var punctuated = PncTextApplier.Apply(engine, result);

        Assert.Equal("hi", punctuated.Text);
        Assert.Null(punctuated.Segments);
    }

    private static PncModelConfig DefaultConfig() => new() { PreLabels = ["<NULL>"], PostLabels = ["<NULL>"] };

    private sealed class FakeEngine(Func<string, IReadOnlyList<string>> apply) : IPncEngine
    {
        public PncModelConfig Config { get; } = DefaultConfig();

        public IReadOnlyList<string> Apply(string text) => apply(text);

        public void Dispose() { }
    }
}
