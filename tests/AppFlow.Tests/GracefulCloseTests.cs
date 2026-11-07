using System.Diagnostics;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

// Serialize integration tests that use the shared disposable EXE.
public sealed partial class StorageAndProcessTests
{
    [Theory]
    [InlineData("frameless")]
    [InlineData("system")]
    [InlineData("multiple")]
    [InlineData("slow")]
    [InlineData("modal")]
    [InlineData("preexisting-modal")]
    [InlineData("tray")]
    [InlineData("session-tray")]
    [InlineData("disabled")]
    [InlineData("hung")]
    [InlineData("ignore")]
    [InlineData("console-save")]
    public async Task UnlearnedApplicationIsTerminatedWithoutLegacyCloseRequests(string scenario)
    {
        await using var window = await TestApplicationWindow.StartAsync(scenario);
        var adapter = new WindowsProcessService(TimeSpan.FromMilliseconds(300));
        var identity = await window.IdentityAsync(adapter);
        Assert.Equal(ProcessCloseResult.Forced, await adapter.CloseWithResultAsync(identity, default));
        Assert.False(await adapter.IsSameProcessAsync(identity, default));
        Assert.True(window.Process.HasExited);
        foreach (var suffix in new[] { "", ".requests", ".system", ".query-session", ".end-session", ".console-close", ".unsafe" })
            Assert.False(File.Exists(window.Marker + suffix), suffix);
    }

    [Fact]
    public async Task UnlearnedProcessGroupTerminatesOnlyMatchingInstances()
    {
        await using var gui = await TestApplicationWindow.StartAsync("system");
        await using var background = await TestApplicationWindow.StartAsync("console-save");
        await using var independent = await TestApplicationWindow.StartAsync("ignore");
        var adapter = new WindowsProcessService();
        var identities = new[] { await background.IdentityAsync(adapter), await gui.IdentityAsync(adapter),
            (await independent.IdentityAsync(adapter)) with { StartTimeUtcTicks = 1 } };
        Assert.Equal(ProcessCloseResult.Forced, await WindowsProcessService.CloseVerifiedProcessesAsync(identities,
            default, TimeSpan.FromSeconds(2)));
        Assert.True(gui.Process.HasExited);
        Assert.True(background.Process.HasExited);
        Assert.False(independent.Process.HasExited);
        foreach (var window in new[] { gui, background, independent })
        {
            Assert.False(File.Exists(window.Marker + ".requests"));
            Assert.False(File.Exists(window.Marker + ".query-session"));
        }
    }

    [Fact]
    public async Task UnavailableLearnedCommandDoesNotFallBackToWindowClose()
    {
        await using var window = await TestApplicationWindow.StartAsync("native-menu");
        var adapter = new WindowsProcessService();
        var identity = await window.IdentityAsync(adapter);
        var profile = new CloseProfile { DirectVerified = true, BackgroundVerified = true,
            Recipe = new() { Window = new("MissingWindowClass", ""), Label = "Exit", CommandId = 42017 } };
        Assert.Equal(ProcessCloseResult.CloseUnavailable, await WindowsProcessService.CloseVerifiedProcessesAsync([identity],
            default, TimeSpan.FromSeconds(1), profiles: [profile]));
        Assert.False(window.Process.HasExited);
        foreach (var suffix in new[] { ".quit", ".requests", ".query-session" }) Assert.False(File.Exists(window.Marker + suffix));
    }

    [Fact]
    public async Task CancelledRequestDoesNotTouchTheApplication()
    {
        await using var window = await TestApplicationWindow.StartAsync("native-menu");
        var adapter = new WindowsProcessService();
        var identity = await window.IdentityAsync(adapter);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.CloseWithResultAsync(identity, cancel.Token));
        Assert.False(window.Process.HasExited);
        Assert.False(File.Exists(window.Marker + ".quit"));
        Assert.False(File.Exists(window.Marker + ".requests"));
    }

    private sealed class TestApplicationWindow(Process process, string marker) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public string Marker { get; } = marker;

        public static async Task<TestApplicationWindow> StartAsync(string scenario, string project = "AppFlow.TestProcess", string framework = "net10.0")
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tests", "AppFlow.TestProcess"))) root = root.Parent;
            Assert.NotNull(root);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var exe = Path.Combine(root.FullName, "tests", project, "bin", configuration, framework, project + ".exe");
            var marker = Path.Combine(Path.GetTempPath(), "AppFlow-close-" + Guid.NewGuid());
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--window"); start.ArgumentList.Add(marker); start.ArgumentList.Add(scenario);
            var result = new TestApplicationWindow(Process.Start(start)!, marker);
            try
            {
                for (var i = 0; i < 200 && !File.Exists(marker + ".ready") && !result.Process.HasExited; i++) await Task.Delay(50);
                Assert.True(File.Exists(marker + ".ready"));
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        public async Task<ProcessIdentity> IdentityAsync(WindowsProcessService adapter) =>
            Assert.Single((await adapter.SnapshotAsync([Process.MainModule!.FileName], default)).Processes, p => p.Pid == Process.Id);

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                foreach (var window in ExitNative.Windows(new HashSet<int> { Process.Id }).Where(w => w.ClassName == "AppFlowDisposableTestWindow" || w.Title.Contains("exit test", StringComparison.Ordinal)))
                    ExitNative.PostMessage(window.Handle, 0x8014, IntPtr.Zero, IntPtr.Zero);
                for (var i = 0; i < 15 && !Process.HasExited; i++) await Task.Delay(20);
            }
            // Force termination is confined to this disposable test process.
            if (!Process.HasExited) { Process.Kill(); await Process.WaitForExitAsync(); }
            Process.Dispose();
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Marker)!, Path.GetFileName(Marker) + "*")) File.Delete(file);
        }
    }
}
