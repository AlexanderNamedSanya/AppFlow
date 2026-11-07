using System.Windows.Threading;
using AppFlow.Core;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class AutoSaveTests
{
    [Fact]
    public Task RuleEditsAndSettingsSaveAutomatically() => OnDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AppFlow-autosave-" + Guid.NewGuid(), "configuration.json");
        var store = new ConfigurationStore(path);
        await using var engine = new AutomationEngine(new IdleProcesses());
        var configuration = Configuration();
        await store.SaveAsync(configuration);
        var appliedTheme = ThemeChoice.System;
        var editor = new MainViewModel(configuration, engine, store, theme => appliedTheme = theme);
        editor.Parents[0].Links[0].Events[0].TimedClose = true;
        editor.Parents[0].Links[0].Events[0].CloseAfter = "42";
        editor.Theme = ThemeChoice.Dark;
        editor.MinimizeToTray = false;
        await Task.Delay(1100);
        var saved = await store.LoadAsync();
        Assert.Equal(42, saved.Parents[0].Links[0].Rules![0].Steps[1].DelaySeconds);
        Assert.Equal(ThemeChoice.Dark, saved.Settings.Theme);
        Assert.Equal(ThemeChoice.Dark, appliedTheme);
        Assert.False(saved.Settings.MinimizeToTray);
        Assert.False(editor.CloseToTray);
        File.Delete(path);
        Directory.Delete(Path.GetDirectoryName(path)!);
    });

    [Fact]
    public Task InvalidInputKeepsValidRulesAndDoesNotBlockSettings() => OnDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "AppFlow-autosave-" + Guid.NewGuid(), "configuration.json");
        var store = new ConfigurationStore(path);
        await using var engine = new AutomationEngine(new IdleProcesses());
        var editor = new MainViewModel(Configuration(), engine, store, _ => { });
        var row = editor.Parents[0].Links[0].Events[0];
        row.TimedClose = true; row.CloseAfter = "42";
        await editor.FlushChangesAsync();
        row.CloseAfter = "";
        editor.MinimizeToTray = false;
        await editor.FlushChangesAsync();
        var saved = await store.LoadAsync();
        Assert.Equal(42, saved.Parents[0].Links[0].Rules![0].Steps[1].DelaySeconds);
        Assert.False(saved.Settings.MinimizeToTray);
        Assert.Contains("не применены", editor.Notice);
        row.CloseAfter = "60";
        await editor.FlushChangesAsync();
        Assert.Equal(60, (await store.LoadAsync()).Parents[0].Links[0].Rules![0].Steps[1].DelaySeconds);
        Assert.Empty(editor.Notice);
        editor.Parents[0].Links[0].Events.RemoveAt(0);
        await editor.FlushChangesAsync();
        Assert.Single((await store.LoadAsync()).Parents[0].Links[0].Rules!);
        editor.Parents[0].Links[0].Enabled = false;
        await editor.FlushChangesAsync();
        Assert.False((await store.LoadAsync()).Parents[0].Links[0].Enabled);
        File.Delete(path);
        Directory.Delete(Path.GetDirectoryName(path)!);
    });

    private static AppConfiguration Configuration() => new()
    {
        Parents = [new() { Executable = Path.GetFullPath("main.exe"), Links =
            [new() { Executable = Path.GetFullPath("child.exe"), Rules = AutomationRule.CreateDefaults() }] }]
    };

    internal static Task OnDispatcher(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class IdleProcesses : IProcessService
    {
        public Task<ProcessSnapshot> SnapshotAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken) => Task.FromResult(new ProcessSnapshot([]));
        public Task<ProcessIdentity?> LaunchIfAbsentAsync(string executable, CancellationToken cancellationToken) => Task.FromResult<ProcessIdentity?>(null);
        public Task<bool> IsSameProcessAsync(ProcessIdentity identity, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> CloseAsync(ProcessIdentity identity, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
