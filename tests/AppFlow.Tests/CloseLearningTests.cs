using System.Windows.Automation;
using AppFlow.Desktop.Services;
using AppFlow.Core;
using Xunit;

namespace AppFlow.Tests;

public sealed partial class StorageAndProcessTests
{
    [Theory]
    [InlineData("wpf", false)]
    [InlineData("winforms", false)]
    [InlineData("wpf", true)]
    [InlineData("winforms", true)]
    public async Task FrameworkExitLearningVerifiesActualWindowStateAndHonorsProfileScope(string framework, bool background)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-uia-learning-" + Guid.NewGuid());
        var store = new CloseProfileStore(Path.Combine(directory, "close-profiles.json"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            LearnedExit learned;
            await using (var first = await TestApplicationWindow.StartAsync(framework + "-visible", "AppFlow.TestGui", "net10.0-windows"))
            {
                var adapter = new WindowsProcessService();
                var identity = await first.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var recording = service.RecordAsync(identity, limit.Token, () => ready.SetResult());
                await ready.Task.WaitAsync(limit.Token);
                Assert.True(await ExitAutomation.RunAsync(token =>
                {
                    var window = ExitNative.Windows(new HashSet<int> { identity.Pid }).Single(w => w.Title.Contains("exit test", StringComparison.Ordinal));
                    var root = AutomationElement.FromHandle(window.Handle);
                    var button = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "AppFlowTestExit"));
                    Assert.NotNull(button);
                    button.SetFocus();
                    Thread.Sleep(150);
                    var handle = ExitNative.Verify(identity);
                    Assert.NotEqual(IntPtr.Zero, handle);
                    try { return ExitAutomation.ClickMenuItem(button, window.Handle, new[] { (handle, identity.Pid) }, token); }
                    finally { ExitNative.CloseHandle(handle); }
                }, false, limit.Token));
                learned = await recording;
                Assert.Equal(ExitMethod.Automation, learned.Recipe.Method);
                Assert.Equal("AppFlowTestExit", learned.Recipe.Elements[^1].AutomationId);
            }
            await using (var second = await TestApplicationWindow.StartAsync(framework, "AppFlow.TestGui", "net10.0-windows"))
            {
                var adapter = new WindowsProcessService();
                var identity = await second.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var profile = await service.VerifyAsync(learned, identity, background ? ExitRoute.Direct : ExitRoute.VisibleWindow, limit.Token);
                Assert.Equal(background, profile.BackgroundVerified); Assert.Equal(!background, profile.VisibleVerified);
                Assert.Equal(background ? "Minimized" : "Normal", await File.ReadAllTextAsync(second.Marker + ".state-on-exit"));
                Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(second.Marker));
                await store.SaveAsync(profile, limit.Token);
            }
            await using var third = await TestApplicationWindow.StartAsync(framework, "AppFlow.TestGui", "net10.0-windows");
            var replay = new WindowsProcessService(TimeSpan.FromSeconds(3), new(store.FilePath));
            var owned = await third.IdentityAsync(replay);
            if (background)
            {
                foreach (var window in ExitNative.Windows(new HashSet<int> { owned.Pid }).Where(w => w.Title.Contains("exit test", StringComparison.Ordinal))) ExitNative.ShowWindowAsync(window.Handle, 6);
                await Task.Delay(200, limit.Token);
            }
            Assert.Equal(ProcessCloseResult.Closed, await replay.CloseWithResultAsync(owned, limit.Token));
            Assert.Equal(0, third.Process.ExitCode);
            Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(third.Marker));
            Assert.False(File.Exists(third.Marker + ".restored"));
            if (background) return;
            await using var minimized = await TestApplicationWindow.StartAsync(framework, "AppFlow.TestGui", "net10.0-windows");
            var minimizedIdentity = await minimized.IdentityAsync(replay);
            var handles = new[] { (ExitNative.Verify(minimizedIdentity), minimizedIdentity.Pid) };
            try
            {
                foreach (var window in ExitNative.Windows(new HashSet<int> { minimizedIdentity.Pid }).Where(w => w.Owner == IntPtr.Zero)) ExitNative.ShowWindowAsync(window.Handle, 6);
                await Task.Delay(200, limit.Token);
                Assert.Equal(ExitRoute.None, await LearnedApplicationCloser.RequestAsync(handles, await store.FindAllAsync(minimizedIdentity.Executable), limit.Token));
                Assert.False(minimized.Process.HasExited);
                Assert.False(File.Exists(minimized.Marker + ".restored"));
            }
            finally { foreach (var handle in handles) ExitNative.CloseHandle(handle.Item1); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeExitMenuWithoutLearningUsesProcessTermination()
    {
        await using var window = await TestApplicationWindow.StartAsync("native-menu");
        var adapter = new WindowsProcessService(TimeSpan.FromSeconds(3));
        var identity = await window.IdentityAsync(adapter);
        Assert.Equal(ProcessCloseResult.Forced, await adapter.CloseWithResultAsync(identity, default));
        Assert.True(window.Process.HasExited);
        Assert.False(File.Exists(window.Marker + ".quit"));
        Assert.False(File.Exists(window.Marker + ".shown"));
        Assert.False(File.Exists(window.Marker + ".requests"));
    }

    [Fact]
    public async Task LearningRecordsActualPopupExitVerifiesBackgroundReplayAndClosesOnlyNewOwnedInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-learning-" + Guid.NewGuid());
        var store = new CloseProfileStore(Path.Combine(directory, "close-profiles.json"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            LearnedExit learned;
            await using (var first = await TestApplicationWindow.StartAsync("popup-menu"))
            {
                var adapter = new WindowsProcessService();
                var identity = await first.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var recording = service.RecordAsync(identity, limit.Token, () => ready.SetResult());
                await ready.Task.WaitAsync(limit.Token);
                await SelectTestExitAsync(identity, limit.Token);
                for (var i = 0; i < 40 && !first.Process.HasExited; i++) await Task.Delay(50, limit.Token);
                Assert.True(first.Process.HasExited, "Menu result: " + (File.Exists(first.Marker + ".menu-result") ? File.ReadAllText(first.Marker + ".menu-result") : "still open")
                    + "; commands: " + (File.Exists(first.Marker + ".commands") ? File.ReadAllText(first.Marker + ".commands") : "none"));
                learned = await recording;
                Assert.Equal("Exit", learned.Recipe.Label);
                Assert.Equal(ExitMethod.MenuCommand, learned.Recipe.Method);
                Assert.Equal(42017u, learned.Recipe.CommandId);
                Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(first.Marker));
                Assert.False(File.Exists(store.FilePath)); // Recording alone must never enable replay.
            }
            await using (var second = await TestApplicationWindow.StartAsync("popup-menu"))
            {
                var adapter = new WindowsProcessService();
                var identity = await second.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var profile = await service.VerifyAsync(learned, identity, ExitRoute.Direct, limit.Token);
                Assert.True(profile.BackgroundVerified); Assert.True(profile.DirectVerified);
                Assert.False(File.Exists(second.Marker + ".shown"));
                await store.SaveAsync(profile, limit.Token);
            }
            await using var third = await TestApplicationWindow.StartAsync("popup-menu");
            await using var independent = await TestApplicationWindow.StartAsync("popup-menu");
            var replay = new WindowsProcessService(TimeSpan.FromSeconds(3), new CloseProfileStore(store.FilePath));
            var owned = await third.IdentityAsync(replay);
            Assert.Equal(ProcessCloseResult.Closed, await CloseAfterTrayIdleAsync(replay, owned, limit.Token));
            Assert.True(File.Exists(third.Marker + ".quit"));
            Assert.False(File.Exists(third.Marker + ".shown"));
            Assert.False(independent.Process.HasExited);
            Assert.False(File.Exists(independent.Marker + ".quit"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("native")]
    [InlineData("winforms")]
    public async Task LearnedTrayExitReplaysRealNotificationMenuWithoutRestoringWindow(string framework)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-tray-learning-" + Guid.NewGuid());
        var store = new CloseProfileStore(Path.Combine(directory, "close-profiles.json"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Task<TestApplicationWindow> Start() => framework == "native" ? TestApplicationWindow.StartAsync("tray-return")
            : TestApplicationWindow.StartAsync("winforms-tray", "AppFlow.TestGui", "net10.0-windows");
        try
        {
            LearnedExit learned;
            await using (var first = await Start())
            {
                var adapter = new WindowsProcessService();
                var identity = await first.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var recording = service.RecordAsync(identity, limit.Token, () => ready.SetResult());
                await ready.Task.WaitAsync(limit.Token);
                var tip = await File.ReadAllTextAsync(first.Marker + ".tray-tip", limit.Token);
                var openTrace = new System.Collections.Concurrent.ConcurrentQueue<string>();
                Assert.True(await ExitAutomation.RunAsync(token =>
                {
                    var selector = new ExitElement(tip, "", ControlType.Button.Id);
                    var icon = ExitAutomation.ReadyTrayButton(selector, token, message => openTrace.Enqueue(message));
                    if (icon is null)
                    {
                        var opener = ExitAutomation.TrayOpener();
                        if (opener is null || !ExitAutomation.Invoke(opener, () => true, token)) return false;
                        for (var i = 0; i < 3 && icon is null; i++) { Thread.Sleep(20); icon = ExitAutomation.ReadyTrayButton(selector, token, message => openTrace.Enqueue(message)); }
                    }
                    if (icon is null) return false;
                    var handle = ExitNative.Verify(identity);
                    try { return ExitAutomation.OpenTrayMenu(icon, new[] { (handle, identity.Pid) }, token, message => openTrace.Enqueue(message)); }
                    finally { ExitNative.CloseHandle(handle); }
                }, false, limit.Token, 3000), string.Join("\n", openTrace.TakeLast(12)));
                await Task.Delay(200, limit.Token);
                try { await SelectTestExitAsync(identity, limit.Token, false, framework == "native" ? "Exit" : "Exit test app"); }
                catch (Exception ex) { throw new InvalidOperationException(string.Join("\n", openTrace), ex); }
                learned = await recording;
                Assert.NotNull(learned.Recipe.TrayIcon);
                Assert.Equal(tip, learned.Recipe.TrayIcon.Name);
                Assert.False(File.Exists(first.Marker + ".shown"));
            }
            await using (var second = await Start())
            {
                var adapter = new WindowsProcessService();
                var identity = await second.IdentityAsync(adapter);
                var service = new CloseLearningService(identity.Executable, adapter, store);
                var held = ExitNative.Verify(identity);
                try
                {
                    var missing = learned.Recipe with { Label = "Missing test command", Elements = learned.Recipe.Elements
                        .Select((e, i) => i == learned.Recipe.Elements.Count - 1 ? e with { Name = "Missing test command" } : e).ToList() };
                    Assert.False(await ExitAutomation.TryTrayAsync(new[] { (held, identity.Pid) }, missing, limit.Token));
                    await Task.Delay(150, limit.Token);
                    Assert.DoesNotContain(ExitNative.Windows(new HashSet<int> { identity.Pid }), w => w.Visible && !w.Minimized);
                    Assert.False(second.Process.HasExited);
                }
                finally { ExitNative.CloseHandle(held); }
                var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
                CloseProfile profile;
                try { profile = await service.VerifyAsync(learned, identity, ExitRoute.Tray, limit.Token, message => trace.Enqueue(message)); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException(ex.Message + "\n" + string.Join("\n", trace.TakeLast(12)), ex); }
                Assert.True(profile.BackgroundVerified); Assert.False(profile.DirectVerified);
                await store.SaveAsync(profile, limit.Token);
                Assert.False(File.Exists(second.Marker + ".shown"));
            }
            await DismissTestOverflowAsync(limit.Token);
            await using var third = await Start();
            var replay = new WindowsProcessService(TimeSpan.FromSeconds(3), new CloseProfileStore(store.FilePath));
            var owned = await third.IdentityAsync(replay);
            Assert.Equal(ProcessCloseResult.Closed, await CloseAfterTrayIdleAsync(replay, owned, limit.Token));
            Assert.True(File.Exists(third.Marker), "Exit handler was not reached; code=" + third.Process.ExitCode
                + "; state=" + (File.Exists(third.Marker + ".state-on-exit") ? File.ReadAllText(third.Marker + ".state-on-exit") : "none"));
            Assert.Equal("saved before normal exit", await File.ReadAllTextAsync(third.Marker));
            if (framework == "winforms") Assert.Equal("False", await File.ReadAllTextAsync(third.Marker + ".visible-on-exit"));
            Assert.False(File.Exists(third.Marker + ".shown"));
            Assert.False(File.Exists(third.Marker + ".ignored-command"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledLearningLeavesProcessAndExistingProfilesUntouched()
    {
        await using var window = await TestApplicationWindow.StartAsync("popup-menu");
        var adapter = new WindowsProcessService();
        var identity = await window.IdentityAsync(adapter);
        var path = Path.Combine(Path.GetTempPath(), "AppFlow-learning-cancel-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, "[]");
        try
        {
            var service = new CloseLearningService(identity.Executable, adapter, new(path));
            using var cancel = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recording = service.RecordAsync(identity, cancel.Token, () => ready.SetResult());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording);
            Assert.False(window.Process.HasExited);
            Assert.Equal("[]", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static async Task<ProcessCloseResult> CloseAfterTrayIdleAsync(WindowsProcessService service, ProcessIdentity identity, CancellationToken ct)
    {
        ProcessCloseResult result;
        while ((result = await service.CloseWithResultAsync(identity, ct)) == ProcessCloseResult.Deferred) await Task.Delay(1000, ct);
        return result;
    }

    private static async Task SelectTestExitAsync(ProcessIdentity identity, CancellationToken ct, bool open = true, string label = "Exit")
    {
        if (open)
        {
            var window = ExitNative.Windows(new HashSet<int> { identity.Pid }).Single(w => w.ClassName == "AppFlowDisposableTestWindow" && w.Owner == IntPtr.Zero).Handle;
            Assert.True(ExitNative.PostMessage(window, 0x800A, IntPtr.Zero, IntPtr.Zero));
        }
        ExitNative.Window? popup = null;
        for (var i = 0; i < 100 && popup is null; i++)
        {
            popup = ExitNative.Windows(new HashSet<int> { identity.Pid }).FirstOrDefault(w => w.Visible && (w.ClassName == "#32768"
                || w.ClassName.StartsWith("WindowsForms10.", StringComparison.Ordinal) && !w.Title.Contains("exit test", StringComparison.Ordinal)));
            if (popup is null) await Task.Delay(20, ct);
        }
        Assert.NotNull(popup);
        Assert.True(await ExitAutomation.RunAsync(token =>
        {
            var root = AutomationElement.FromHandle(popup.Handle);
            var exits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label));
            var exit = Assert.Single(exits.Cast<AutomationElement>());
            var handle = ExitNative.Verify(identity);
            Assert.NotEqual(IntPtr.Zero, handle);
            try { return ExitAutomation.ClickMenuItem(exit, popup.Handle, new[] { (handle, identity.Pid) }, token); }
            finally { ExitNative.CloseHandle(handle); }
        }, false, ct));
    }
}

public sealed class CloseProfileTests
{
    [Fact]
    public async Task ProfilesRequireVerificationSurviveReloadAndRejectChangedExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-profile-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "program.exe");
            await File.WriteAllTextAsync(executable, "first version");
            var profile = new CloseProfile { Executable = executable, FileHash = await CloseProfileStore.FingerprintAsync(executable),
                Recipe = new() { Window = new("TestClass", "Test"), Label = "Exit", CommandId = 42 } };
            var path = Path.Combine(directory, "close-profiles.json");
            var store = new CloseProfileStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(profile));
            profile = profile with { BackgroundVerified = true, DirectVerified = true, VerifiedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(profile);
            Assert.Equal(42u, (await new CloseProfileStore(path).FindAsync(executable))!.Recipe.CommandId);
            await store.SaveAsync(profile);
            Assert.Single(await store.FindAllAsync(executable));
            await File.WriteAllTextAsync(executable, "updated version");
            Assert.Null(await store.FindAsync(executable));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(profile));
        }
        finally { Directory.Delete(directory, true); }
    }
}
