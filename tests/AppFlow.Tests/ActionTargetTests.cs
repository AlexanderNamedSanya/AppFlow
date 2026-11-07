using AppFlow.Core;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class ActionTargetTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData(" https://example.com/page?q=1 ", "https://example.com/page?q=1")]
    [InlineData("http://localhost:8080/test", "http://localhost:8080/test")]
    public void WebsiteAddressesAreNormalized(string input, string expected) => Assert.Equal(expected, ActionTarget.Website(input));

    [Theory]
    [InlineData("file:///C:/test.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://")]
    public void NonWebsiteTargetsAreRejected(string input) => Assert.Throws<InvalidDataException>(() => ActionTarget.Website(input));

    [Fact]
    public void WebsiteEditorHasOnlyOpenAndDelayOptions()
    {
        var row = new EventViewModel(new AppLink { Executable = "https://example.com/", Rules = null, Steps = [new() { Action = FlowAction.Launch }] });
        Assert.Equal("Открыть сайт", row.OpenActionLabel); Assert.False(row.CanClose);
        Assert.False(row.SingleLaunchOptions); Assert.False(row.CanRepeat);
        row.Action = FlowAction.Close; Assert.Equal(FlowAction.Launch, row.Action);
        var saved = row.ToModel(); Assert.Single(saved.Steps); Assert.Null(saved.RepeatSeconds);
    }

    [Fact]
    public void WebsiteCannotBeSavedWithCloseOrCycle()
    {
        var link = new AppLink { Executable = "https://example.com/", Rules = [new() { Steps = [new() { Action = FlowAction.Close }] }] };
        var config = new AppConfiguration { Parents = [new() { Executable = Path.GetFullPath("main.exe"), Links = [link] }] };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void ScriptInvocationUsesSeparateArgumentsAndDoesNotBypassPolicy()
    {
        var path = Path.GetFullPath("script with spaces & symbols.ps1");
        var info = ScriptRunner.CreateStartInfo(path);
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.Equal(path, info.ArgumentList.Last()); Assert.Contains("-File", info.ArgumentList);
        Assert.DoesNotContain("-ExecutionPolicy", info.ArgumentList);
        Assert.True(ScriptRunner.MatchesInvocation($"powershell.exe -NoProfile -File \"{path}\"", path));
        Assert.False(ScriptRunner.MatchesInvocation($"powershell.exe -File \"{path}.other.ps1\"", path));
    }

    [Fact]
    public void BatchPathIsQuotedAndPercentExpansionIsRejected()
    {
        var path = Path.GetFullPath("directory & spaces/test script.cmd");
        var info = ScriptRunner.CreateStartInfo(path);
        Assert.Contains($"\"\"{path}\"\"", info.Arguments);
        Assert.True(ScriptRunner.MatchesInvocation($"cmd.exe {info.Arguments}", path));
        Assert.Throws<InvalidDataException>(() => ScriptRunner.CreateStartInfo(Path.GetFullPath("%TEMP%/script.cmd")));
    }
}
