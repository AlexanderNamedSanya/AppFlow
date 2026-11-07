using System.Diagnostics;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

internal enum ExitRoute { None, Direct, VisibleWindow, Tray }
internal static class LearnedApplicationCloser
{
    internal static async Task<ExitRoute> RequestAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes,
        IReadOnlyList<CloseProfile> profiles, CancellationToken ct)
    {
        if (profiles.Count == 0) return ExitRoute.None;
        if (ExitNative.IsModal(processes)) return ExitRoute.None;
        // Recorded commands are scoped to this executable version and freshly verified process handles.
        foreach (var profile in profiles.Where(p => p.DirectVerified))
        {
            ct.ThrowIfCancellationRequested();
            if (await DirectAsync(processes, profile.Recipe, ct)) return ExitRoute.Direct;
        }
        foreach (var profile in profiles.Where(p => p.VisibleVerified))
        {
            ct.ThrowIfCancellationRequested();
            var window = ExitNative.Locate(profile.Recipe.Window, processes);
            if (window != IntPtr.Zero && ExitNative.IsWindowVisible(window) && !ExitNative.IsIconic(window)
                && await DirectAsync(processes, profile.Recipe, ct))
                return ExitRoute.VisibleWindow;
        }
        foreach (var profile in profiles.Where(p => p.BackgroundVerified && p.Recipe.TrayIcon is not null))
        {
            ct.ThrowIfCancellationRequested();
            if (await ExitAutomation.TryTrayAsync(processes, profile.Recipe, ct)) return ExitRoute.Tray;
        }
        return ExitRoute.None;
    }

    internal static Task<bool> DirectAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes, ExitRecipe recipe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var window = ExitNative.Locate(recipe.Window, processes);
        if (recipe.Method == ExitMethod.Automation)
        {
            if (window != IntPtr.Zero && recipe.Control is not null && ExitNative.ClickControl(window, recipe.Control, processes)) return Task.FromResult(true);
            return ExitAutomation.TryWindowAsync(processes, recipe, ct);
        }
        return Task.FromResult(window != IntPtr.Zero && ExitNative.SendCommand(window, recipe.CommandId, processes));
    }

    internal static async Task<ProcessCloseResult> WaitAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes, TimeSpan timeout, CancellationToken ct,
        Func<bool>? remainsHidden = null)
    {
        var waiting = Stopwatch.StartNew();
        while (processes.Any(p => ExitNative.Alive(p.Handle)) && waiting.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (remainsHidden is not null && !remainsHidden()) throw new InvalidOperationException("При выходе программа показала окно. Этот способ нельзя сохранить как фоновый.");
            await Task.Delay(75, ct);
        }
        if (processes.All(p => !ExitNative.Alive(p.Handle))) return ProcessCloseResult.Closed;
        if (ExitNative.IsModal(processes)) return ProcessCloseResult.ConfirmationRequired;
        return ProcessCloseResult.StillRunning;
    }
}
