using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

internal sealed class ExitRecorder : IDisposable
{
    private readonly ProcessIdentity identity;
    private readonly object sync = new();
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? dispatcher;
    private ExitRecipe? hovered, recorded;
    private ExitElement? trayIcon, trayOpener;
    private long hoverAt, iconAt, openerAt;
    private Rect hoverBounds = Rect.Empty;
    private readonly List<(IntPtr Surface, ExitRecipe Recipe, Rect Bounds)> menuCache = [];
    private readonly HashSet<IntPtr> scanning = [];
    private bool frozenIcon;
    private int sampling;
    private bool disposed;
    private WinEventCallback? winEvent;
    private HookCallback? mouse, keyboard;
    private AutomationFocusChangedEventHandler? focus;
    private AutomationEventHandler? invoked;
    public ExitRecorder(ProcessIdentity identity)
    {
        this.identity = identity;
        thread = new Thread(Listen) { IsBackground = true, Name = "AppFlow exit learning" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public Task Ready => ready.Task;
    public ExitRecipe? Recorded { get { lock (sync) return recorded is null ? null : WithTray(recorded); } }
    public async Task FlushAsync(CancellationToken ct)
    {
        var waiting = Stopwatch.StartNew();
        while (Volatile.Read(ref sampling) != 0 && waiting.ElapsedMilliseconds < 750) await Task.Delay(15, ct);
    }

    private void Listen()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        var previousDpi = ExitNative.SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr eventHook = IntPtr.Zero, menuHook = IntPtr.Zero, mouseHook = IntPtr.Zero, keyboardHook = IntPtr.Zero;
        try
        {
            winEvent = OnWindowEvent; mouse = OnMouse; keyboard = OnKeyboard;
            eventHook = SetWinEventHook(0x8002, 0x8013, IntPtr.Zero, winEvent, (uint)identity.Pid, 0, 0);
            menuHook = SetWinEventHook(4, 7, IntPtr.Zero, winEvent, (uint)identity.Pid, 0, 0);
            mouseHook = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
            keyboardHook = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
            if (eventHook == IntPtr.Zero || mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось начать наблюдение за закрытием.");
            focus = (sender, _) => { if (sender is AutomationElement element) Capture(element, false); };
            invoked = (sender, _) => { if (sender is AutomationElement element) Capture(element, true); };
            Automation.AddAutomationFocusChangedEventHandler(focus);
            Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement, TreeScope.Subtree, invoked);
            Capture(AutomationElement.FocusedElement, false);
            ready.TrySetResult();
            Dispatcher.Run();
        }
        catch (Exception ex) { ready.TrySetException(ex); }
        finally
        {
            if (eventHook != IntPtr.Zero) UnhookWinEvent(eventHook);
            if (menuHook != IntPtr.Zero) UnhookWinEvent(menuHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            try { if (focus is not null) Automation.RemoveAutomationFocusChangedEventHandler(focus); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
            if (previousDpi != IntPtr.Zero) ExitNative.SetThreadDpiAwarenessContext(previousDpi);
            try { if (invoked is not null) Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement, invoked); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
        }
    }
    private void OnWindowEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int child, uint sourceThread, uint time)
    {
        try
        {
            if (disposed || window == IntPtr.Zero) return;
            var className = ExitNative.Class(window);
            if (className != "#32768")
            {
                if (eventId is 0x8002 or 4 or 6 && ExitNative.IsWindowVisible(window)
                    && ExitNative.GetAncestor(window, 2) == window
                    && (className.StartsWith("WindowsForms10.", StringComparison.Ordinal) || className == "HwndWrapper["))
                    _ = CacheMenuAsync(window);
                return;
            }
            var candidate = ExitNative.SelectedMenu(window, child);
            if (candidate is null) return;
            var owner = ExitNative.Popup(window).Owner;
            ExitNative.GetWindowThreadProcessId(owner, out var pid);
            if (pid != identity.Pid) return;
            Remember(candidate, eventId == 0x8013);
        }
        catch { /* Accessibility events race with destruction of the source menu. */ }
    }
    private async Task CacheMenuAsync(IntPtr surface)
    {
        lock (sync) { if (disposed || !scanning.Add(surface)) return; }
        try
        {
            await ExitAutomation.RunAsync(token =>
            {
                var root = AutomationElement.FromHandle(surface);
                var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                var captured = new List<(IntPtr Surface, ExitRecipe Recipe, Rect Bounds)>();
                foreach (AutomationElement item in items.Cast<AutomationElement>().Take(128))
                {
                    token.ThrowIfCancellationRequested();
                    var recipe = ExitAutomation.CaptureElement(item, identity.Pid, surface);
                    if (recipe is not null) captured.Add((surface, recipe, item.Current.BoundingRectangle));
                }
                token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (disposed) return false;
                    menuCache.RemoveAll(item => item.Surface == surface);
                    menuCache.AddRange(captured);
                }
                return true;
            }, false, CancellationToken.None, 700);
        }
        finally { lock (sync) scanning.Remove(surface); }
    }
    private void Capture(AutomationElement element, bool commit)
    {
        try
        {
            if (disposed) return;
            var candidate = ExitAutomation.CaptureElement(element, identity.Pid);
            if (candidate is not null) Remember(candidate, commit, element.Current.BoundingRectangle);
            else if (!commit && element.Current.ProcessId == identity.Pid) { lock (sync) { hovered = null; hoverBounds = Rect.Empty; } }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException) { }
    }
    private void Remember(ExitRecipe recipe, bool commit, Rect? bounds = null)
    {
        lock (sync)
        {
            if (disposed) return;
            // Prefer the native command over an accessibility description of the same menu item.
            if (hovered is { Method: ExitMethod.MenuCommand } native && native.Label == recipe.Label
                && Stopwatch.GetElapsedTime(hoverAt).TotalSeconds < 2) recipe = native;
            hovered = recipe; hoverAt = Stopwatch.GetTimestamp();
            hoverBounds = bounds ?? Rect.Empty;
            if (commit) recorded = WithTray(recipe);
        }
    }
    private ExitRecipe WithTray(ExitRecipe recipe) => recipe with
    {
        TrayIcon = Stopwatch.GetElapsedTime(iconAt).TotalSeconds < 15 ? trayIcon : null,
        TrayOpener = Stopwatch.GetElapsedTime(openerAt).TotalSeconds < 30 ? trayOpener : null
    };
    private void CommitHover(Point? point = null)
    {
        lock (sync)
            if (hovered is not null && Stopwatch.GetElapsedTime(hoverAt).TotalSeconds < 3
                && (point is null || !hoverBounds.IsEmpty && hoverBounds.Contains(point.Value))) recorded = WithTray(hovered);
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !disposed && (message.ToInt64() is 0x201 or 0x204))
        {
            var input = Marshal.PtrToStructure<MouseHook>(data);
            // Remote input and accessibility tools may also inject the user's demonstration.
            // The recording is explicitly scoped to this application, not to the input source.
            {
                var window = ExitNative.WindowFromPoint(input.Point);
                ExitNative.GetWindowThreadProcessId(window, out var pid);
                if (message.ToInt64() == 0x204)
                {
                    ExitElement? clicked = null;
                    if (ExitAutomation.IsTrayClass(ExitNative.Class(ExitNative.GetAncestor(window, 2))))
                    {
                        // Learning only: resolve the icon before Windows opens its menu or rearranges
                        // the tray. The hook is bounded to 100 ms and never waits on a provider indefinitely.
                        try { clicked = ExitAutomation.RunAsync(token => ExitAutomation.CaptureShellButton(new Point(input.Point.X, input.Point.Y)),
                            (ExitElement?)null, CancellationToken.None, 100).GetAwaiter().GetResult(); }
                        catch { /* A failed provider must not interrupt the user's input. */ }
                    }
                    lock (sync)
                    {
                        frozenIcon = clicked is not null;
                        trayIcon = clicked;
                        iconAt = frozenIcon ? Stopwatch.GetTimestamp() : 0;
                    }
                }
                if (pid == identity.Pid && message.ToInt64() == 0x201)
                {
                    var native = ExitNative.Class(window) == "#32768" ? ExitNative.MenuAtPoint(window, input.Point) : null;
                    if (native is not null) Remember(native, true);
                    else
                    {
                        var point = new Point(input.Point.X, input.Point.Y);
                        lock (sync)
                        {
                            var matches = menuCache.Where(item => item.Surface == ExitNative.GetAncestor(window, 2) && item.Bounds.Contains(point)).ToArray();
                            if (matches.Length == 1) recorded = WithTray(matches[0].Recipe);
                            else CommitHover(point);
                        }
                    }
                }
                if ((message.ToInt64() != 0x204 || !frozenIcon) && Interlocked.CompareExchange(ref sampling, 1, 0) == 0)
                    _ = SamplePointAsync(input.Point, message.ToInt64() == 0x204, pid == identity.Pid);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private async Task SamplePointAsync(ExitNative.Point point, bool right, bool target)
    {
        try
        {
            await ExitAutomation.RunAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                if (disposed) return false;
                var location = new Point(point.X, point.Y);
                if (target && !right) { Capture(AutomationElement.FromPoint(location), true); return true; }
                var shell = ExitAutomation.CaptureShellButton(location);
                if (shell is not null)
                {
                    lock (sync)
                    {
                        if (right && !frozenIcon) { trayIcon = shell; iconAt = Stopwatch.GetTimestamp(); }
                        else if ((shell.AutomationId + " " + shell.Name).Contains("hidden", StringComparison.OrdinalIgnoreCase)
                            || (shell.AutomationId + " " + shell.Name).Contains("overflow", StringComparison.OrdinalIgnoreCase)
                            || shell.Name.Contains("скрыт", StringComparison.OrdinalIgnoreCase))
                        { trayOpener = shell; openerAt = Stopwatch.GetTimestamp(); }
                    }
                }
                else if (!right) Capture(AutomationElement.FromPoint(location), true);
                return true;
            }, false, CancellationToken.None, 600);
        }
        finally { Interlocked.Exchange(ref sampling, 0); }
    }
    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !disposed && message.ToInt64() is 0x100 or 0x104)
        {
            var key = Marshal.PtrToStructure<KeyboardHook>(data);
            if (key.Key is 0x0D or 0x20)
            {
                ExitNative.GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                if (pid == identity.Pid) CommitHover();
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    public void Dispose()
    {
        lock (sync) { disposed = true; }
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        // Hooks are removed on the thread that installed them. Never block the WPF UI thread on UIA.
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public ExitNative.Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHook { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int child, uint thread, uint time);
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
