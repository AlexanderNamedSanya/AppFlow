using System.Runtime.InteropServices;
using System.Windows.Automation;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

public sealed partial class StorageAndProcessTests
{
    [Fact]
    public void OnlyAppFlowTaggedInjectedInputIsIgnoredByTheTrayObserver()
    {
        Assert.True(TrayInteractionGuard.IsOwnInput(1, TrayInteractionGuard.InputTag));
        Assert.False(TrayInteractionGuard.IsOwnInput(0, TrayInteractionGuard.InputTag));
        Assert.False(TrayInteractionGuard.IsOwnInput(1, UIntPtr.Zero));
    }

    [Fact]
    public async Task UserMenuIsPreservedAndDeferredTrayCloseSucceedsAfterItIsDismissed()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var target = await TestApplicationWindow.StartAsync("tray-return");
        await using var userApp = await TestApplicationWindow.StartAsync("menu-return");
        var adapter = new WindowsProcessService();
        var identity = await target.IdentityAsync(adapter);
        var userIdentity = await userApp.IdentityAsync(adapter);
        var owner = ExitNative.Windows(new HashSet<int> { userIdentity.Pid }).Single(w => w.ClassName == "AppFlowDisposableTestWindow").Handle;
        Assert.True(ExitNative.PostMessage(owner, 0x800A, IntPtr.Zero, IntPtr.Zero));
        ExitNative.Window? userMenu = null;
        for (var i = 0; i < 100 && userMenu is null; i++)
        {
            userMenu = ExitNative.Windows(new HashSet<int> { userIdentity.Pid }).FirstOrDefault(w => w.Visible && w.ClassName == "#32768");
            if (userMenu is null) await Task.Delay(20, limit.Token);
        }
        Assert.NotNull(userMenu);
        var profile = await NativeTrayProfileAsync(target, identity, limit.Token);
        Assert.True(ExitNative.GetCursorPos(out var cursor));
        var foreground = ForegroundWindow();
        Assert.Equal(ProcessCloseResult.Deferred, await WindowsProcessService.CloseVerifiedProcessesAsync([identity],
            limit.Token, TimeSpan.FromSeconds(2), profiles: [profile]));
        Assert.True(ExitNative.IsWindowVisible(userMenu.Handle));
        Assert.Equal(foreground, ForegroundWindow());
        Assert.True(ExitNative.GetCursorPos(out var after));
        Assert.Equal(cursor.X, after.X); Assert.Equal(cursor.Y, after.Y);
        Assert.False(target.Process.HasExited);
        Assert.True(ExitNative.PostMessage(owner, 0x1F, IntPtr.Zero, IntPtr.Zero));
        for (var i = 0; i < 100 && ExitNative.IsWindowVisible(userMenu.Handle); i++) await Task.Delay(20, limit.Token);
        Assert.False(ExitNative.IsWindowVisible(userMenu.Handle));
        Assert.Equal(ProcessCloseResult.Closed, await CloseNativeTrayWhenIdleAsync(identity, profile, limit.Token));
        Assert.False(userApp.Process.HasExited);
        Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(target.Marker, limit.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingInputDefersUnsentExitButKeepsAnAlreadyIssuedCommand(bool afterExitCommand)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var target = await TestApplicationWindow.StartAsync("tray-return");
        var adapter = new WindowsProcessService();
        var identity = await target.IdentityAsync(adapter);
        var profile = await NativeTrayProfileAsync(target, identity, limit.Token);
        var handle = ExitNative.Verify(identity);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            var injected = false;
            var traces = new List<string>();
            while (!injected)
            {
                var issued = await ExitAutomation.TryTrayAsync(new[] { (handle, identity.Pid) }, profile.Recipe, limit.Token, trace =>
                {
                    traces.Add(trace);
                    if (trace != (afterExitCommand ? "Exit command issued" : "Tray icon ready") || injected) return;
                    var inputs = new[] { new TestInput { Mouse = new TestMouse { X = 1, Flags = 1 } } };
                    Assert.Equal(1u, InjectInput(1, inputs, Marshal.SizeOf<TestInput>()));
                    injected = true;
                    Thread.Sleep(50); // Let the observer see the competing input before another UI action.
                });
                Assert.Equal(injected && afterExitCommand, issued);
                if (!injected)
                {
                    Assert.True(!limit.IsCancellationRequested, string.Join("\n", traces.TakeLast(20)));
                    try { await Task.Delay(1000, limit.Token); }
                    catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(string.Join("\n", traces.TakeLast(20))); }
                }
            }
            if (afterExitCommand)
            {
                Assert.Equal(ProcessCloseResult.Closed, await LearnedApplicationCloser.WaitAsync(new[] { (handle, identity.Pid) }, TimeSpan.FromSeconds(2), limit.Token));
                Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(target.Marker, limit.Token));
                return;
            }
            Assert.False(target.Process.HasExited);
            Assert.False(File.Exists(target.Marker + ".quit"));
            // The simulated user finishes with the overflow that this attempt opened.
            await DismissTestOverflowAsync(limit.Token);
            Assert.Equal(ProcessCloseResult.Closed, await CloseNativeTrayWhenIdleAsync(identity, profile, limit.Token));
            Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(target.Marker, limit.Token));
        }
        finally { ExitNative.CloseHandle(handle); await DismissTestOverflowAsync(CancellationToken.None); }
    }

    private static Task<bool> DismissTestOverflowAsync(CancellationToken ct) => ExitAutomation.RunAsync(token =>
    {
        var open = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition)
            .Cast<AutomationElement>().Any(w => w.Current.ClassName is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" && !w.Current.IsOffscreen);
        var opener = open ? ExitAutomation.TrayOpener() : null;
        return opener is null || ExitAutomation.Invoke(opener, () => true, token);
    }, false, ct);

    private static async Task<CloseProfile> NativeTrayProfileAsync(TestApplicationWindow target, ProcessIdentity identity, CancellationToken ct) => new()
    {
        Executable = identity.Executable, BackgroundVerified = true,
        Recipe = new() { Window = new("AppFlowDisposableTestWindow", "AppFlow test"), CommandId = 42017, Label = "Exit",
            TrayIcon = new(await File.ReadAllTextAsync(target.Marker + ".tray-tip", ct), "", ControlType.Button.Id) }
    };

    private static async Task<ProcessCloseResult> CloseNativeTrayWhenIdleAsync(ProcessIdentity identity, CloseProfile profile, CancellationToken ct)
    {
        ProcessCloseResult result;
        do
        {
            await Task.Delay(1000, ct);
            result = await WindowsProcessService.CloseVerifiedProcessesAsync([identity], ct, TimeSpan.FromSeconds(2), profiles: [profile]);
        } while (result == ProcessCloseResult.Deferred);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct TestInput { public uint Type; public TestMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct TestMouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", EntryPoint = "SendInput")] private static extern uint InjectInput(uint count, TestInput[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr ForegroundWindow();
}
