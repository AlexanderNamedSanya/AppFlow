using System.Diagnostics;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

public sealed partial class StorageAndProcessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ShortScriptReportsSuccessfulCompletionOrFailureCode(int exitCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow.Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "quick.cmd");
        await File.WriteAllTextAsync(script, $"@echo off\r\nexit /b {exitCode}\r\n");
        try
        {
            var adapter = new WindowsProcessService();
            if (exitCode == 0) Assert.NotNull(await adapter.LaunchScriptIfAbsentAsync(script, default));
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.LaunchScriptIfAbsentAsync(script, default));
                Assert.Contains("7", error.Message);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ScriptAdapterDetectsUserScriptAndStopsOnlyItsOwnInterpreter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow.Tests", Guid.NewGuid().ToString(), "scripts & spaces");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "test script.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\n:wait\r\nfor /l %%i in (1,1,100000) do rem waiting\r\ngoto wait\r\n");
        var adapter = new WindowsProcessService(); ProcessIdentity? owned = null;
        using var external = Process.Start(ScriptRunner.CreateStartInfo(script))!;
        try
        {
            Assert.Null(await adapter.LaunchScriptIfAbsentAsync(script, default));
            Assert.False(external.HasExited);
            external.Kill(); await external.WaitForExitAsync();
            owned = await adapter.LaunchScriptIfAbsentAsync(script, default);
            Assert.NotNull(owned);
            Assert.True(await adapter.IsSameProcessAsync(owned, default));
            Assert.Null(await adapter.LaunchScriptIfAbsentAsync(script, default));
            Assert.True(await adapter.CloseAsync(owned, default));
            Assert.False(await adapter.IsSameProcessAsync(owned, default));
        }
        finally
        {
            if (!external.HasExited) { external.Kill(); await external.WaitForExitAsync(); }
            if (owned is not null) await adapter.CloseAsync(owned, default);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ScriptAndWebsiteRulesSurviveJsonRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow.Tests", Guid.NewGuid().ToString());
        try
        {
            var store = new ConfigurationStore(Path.Combine(directory, "configuration.json"));
            var config = new AppConfiguration { Parents = [new() { Executable = Path.GetFullPath("main.exe"), Links = [
                new() { Executable = "https://example.com/page", Rules = [new()] },
                new() { Executable = Path.GetFullPath("test script.ps1"), Rules = AutomationRule.CreateDefaults() }
            ] }] };
            await store.SaveAsync(config); var loaded = await store.LoadAsync();
            Assert.Equal(TargetKind.Website, loaded.Parents[0].Links[0].Kind);
            Assert.Equal(TargetKind.Script, loaded.Parents[0].Links[1].Kind);
            Assert.Equal(config.Parents[0].Links[1].Rules![1].Id, loaded.Parents[0].Links[1].Rules![1].Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task JsonRoundTripPreservesRulesAndSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow.Tests", Guid.NewGuid().ToString());
        try
        {
            var store = new ConfigurationStore(Path.Combine(directory, "configuration.json"));
            var configuration = new AppConfiguration
            {
                Settings = new() { AutoStart = true, Theme = ThemeChoice.Dark },
                Parents = [new() { Executable = Path.GetFullPath("parent.exe"), Links = [new() { Executable = Path.GetFullPath("child.exe"), RepeatSeconds = 12 }] }]
            };
            await store.SaveAsync(configuration); var loaded = await store.LoadAsync();
            Assert.True(loaded.Settings.AutoStart); Assert.Equal(ThemeChoice.Dark, loaded.Settings.Theme);
            Assert.Equal(configuration.Parents[0].Links[0].Id, loaded.Parents[0].Links[0].Id);
            Assert.Equal(180, loaded.Parents[0].Links[0].Steps[1].DelaySeconds);
            Assert.Equal(12, loaded.Parents[0].Links[0].RepeatSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InvalidJsonIsReportedWithoutOverwriting()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{ invalid"); var store = new ConfigurationStore(file);
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
            Assert.Equal("{ invalid", await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(604801)]
    public void RejectsInvalidDelays(double seconds)
    {
        var config = new AppConfiguration { Parents = [new() { Executable = Path.GetFullPath("p.exe"), Links = [new() { Executable = Path.GetFullPath("c.exe"), Steps = [new() { DelaySeconds = seconds }] }] }] };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public async Task WindowsAdapterVerifiesIdentityAndNeverClosesExternalProcess()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tests", "AppFlow.TestProcess"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var exe = Path.Combine(root.FullName, "tests", "AppFlow.TestProcess", "bin", configuration, "net10.0", "AppFlow.TestProcess.exe");
        Assert.True(File.Exists(exe), exe);
        var adapter = new WindowsProcessService(TimeSpan.FromMilliseconds(500));
        ProcessIdentity? owned = null;
        using var external = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True((await adapter.SnapshotAsync([exe], default)).Contains(exe));
            Assert.Null(await adapter.LaunchIfAbsentAsync(exe, default));
            Assert.False(external.HasExited);
            var real = (await adapter.SnapshotAsync([exe], default)).Processes.Single(p => p.Pid == external.Id);
            Assert.True(await adapter.CloseAsync(real with { StartTimeUtcTicks = real.StartTimeUtcTicks + 1 }, default));
            Assert.False(external.HasExited);
            external.Kill(); await external.WaitForExitAsync();
            owned = await adapter.LaunchIfAbsentAsync(exe, default);
            Assert.NotNull(owned); Assert.True(await adapter.IsSameProcessAsync(owned, default));
            Assert.Equal(ProcessCloseResult.Forced, await adapter.CloseWithResultAsync(owned, default));
            Assert.False(await adapter.IsSameProcessAsync(owned, default));
        }
        finally
        {
            if (!external.HasExited) { external.Kill(); await external.WaitForExitAsync(); }
            if (owned is not null && await adapter.IsSameProcessAsync(owned, default))
            {
                using var helper = Process.GetProcessById(owned.Pid);
                if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); }
            }
        }
    }

    [Fact]
    public async Task HiddenGuiApplicationWithoutLearningIsTerminated()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tests", "AppFlow.TestProcess"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var exe = Path.Combine(root.FullName, "tests", "AppFlow.TestProcess", "bin", configuration, "net10.0", "AppFlow.TestProcess.exe");
        var marker = Path.Combine(Path.GetTempPath(), "AppFlow-graceful-" + Guid.NewGuid());
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--window"); start.ArgumentList.Add(marker);
        using var helper = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(marker + ".ready") && !helper.HasExited; i++) await Task.Delay(50);
            Assert.True(File.Exists(marker + ".ready"));
            var adapter = new WindowsProcessService();
            var identity = Assert.Single((await adapter.SnapshotAsync([exe], default)).Processes, p => p.Pid == helper.Id);
            Assert.Equal(ProcessCloseResult.Forced, await adapter.CloseWithResultAsync(identity, default));
            Assert.True(helper.HasExited);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); }
            File.Delete(marker);
            File.Delete(marker + ".ready");
        }
    }
}
