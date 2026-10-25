// wiki: testing#unit-tests
using SharpAudio.Core.Worker;

namespace SharpAudio.Api.Tests;

/// <summary>SERVER_MODE parsing, including the historical default and the new task families.</summary>
public class ServerModeTests
{
    [Theory]
    [InlineData(null, true, false, false, false)]
    [InlineData("", true, false, false, false)]
    [InlineData("tts", true, false, false, false)]
    [InlineData("asr", false, true, false, false)]
    [InlineData("music", false, false, true, false)]
    [InlineData("diar", false, false, false, true)]
    [InlineData("both", true, true, false, false)]
    [InlineData("all", true, true, true, true)]
    [InlineData("ALL", true, true, true, true)]
    [InlineData(" all ", true, true, true, true)]
    [InlineData("nonsense", false, false, false, false)]
    public void Mode_names_map_to_enabled_tasks(string? value, bool tts, bool asr, bool music, bool diar)
    {
        var mode = ServerMode.Parse(value);
        Assert.Equal(tts, mode.Tts);
        Assert.Equal(asr, mode.Asr);
        Assert.Equal(music, mode.Music);
        Assert.Equal(diar, mode.Diar);
    }
}
