// wiki: testing#unit-tests
using SharpAudio.Api.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncInputCleanerTests
{
    [Fact]
    public void Clean_lowercases_and_strips_punctuation()
    {
        Assert.Equal("hello world", PncInputCleaner.Clean("Hello, World!"));
    }

    [Fact]
    public void Clean_collapses_whitespace_runs_to_single_spaces()
    {
        Assert.Equal("a b c", PncInputCleaner.Clean("  a\t\n  b   c  "));
    }

    [Fact]
    public void Clean_removes_punctuation_without_leaving_double_spaces()
    {
        Assert.Equal("usa", PncInputCleaner.Clean("U.S.A."));
        Assert.Equal("hello world", PncInputCleaner.Clean("hello . world"));
    }

    [Fact]
    public void Clean_keeps_letters_and_digits()
    {
        Assert.Equal("test 42 café", PncInputCleaner.Clean("Test 42 Café"));
    }

    [Fact]
    public void Clean_returns_empty_for_punctuation_only_input()
    {
        Assert.Equal("", PncInputCleaner.Clean("..."));
    }
}
