using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Condition = System.Windows.Automation.Condition;

namespace AppFlow.Desktop.Services;

internal static class ExitAutomation
{
    private static readonly SemaphoreSlim TrayGate = new(1, 1);
    private static Task<bool> ForProcessesAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes,
        Func<IReadOnlyList<(IntPtr Handle, int Pid)>, CancellationToken, bool> action, CancellationToken ct, int milliseconds = 1800)
    {
        // A timed-out provider can still be finishing a read. Its handles must outlive that read.
        var held = ExitNative.Retain(processes);
        return RunAsync(token =>
        {
            try { return action(held, token); }
            finally { foreach (var process in held) ExitNative.CloseHandle(process.Handle); }
        }, false, ct, milliseconds);
    }
    // A provider belongs to another application and may hang. Cancel the work before returning;
    // every subsequent action checks the token and the exact process identity again.
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, T> action, T fallback, CancellationToken ct,
        int milliseconds = 1800)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(milliseconds);
        var token = deadline.Token;
        var task = Task.Run(() =>
        {
            var previousDpi = ExitNative.SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { return action(token); }
            catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException
                or COMException or UnauthorizedAccessException or OperationCanceledException) { return fallback; }
            finally { if (previousDpi != IntPtr.Zero) ExitNative.SetThreadDpiAwarenessContext(previousDpi); }
        });
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return fallback; }
        finally { deadline.Cancel(); }
    }

    internal static ExitElement Describe(AutomationElement element)
    {
        var current = element.Current;
        return new(current.Name ?? "", current.AutomationId ?? "", current.ControlType.Id);
    }
    private static Condition Match(ExitElement selector)
    {
        var conditions = new List<Condition> { new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.LookupById(selector.ControlType)) };
        // Both name and id are scoped to the recorded ancestors; never fall back to a similarly named control.
        if (selector.AutomationId.Length != 0) conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, selector.AutomationId));
        if (selector.Name.Length != 0) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, selector.Name));
        return conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray());
    }
    private static AutomationElement? Unique(AutomationElement root, ExitElement selector)
    {
        var matches = root.FindAll(TreeScope.Descendants, Match(selector));
        return matches.Count == 1 ? matches[0] : null;
    }
    internal static bool Invoke(AutomationElement element, Func<bool> allowed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!allowed() || !element.Current.IsEnabled) return false;
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ct.ThrowIfCancellationRequested();
            if (!allowed()) return false;
            ((InvokePattern)invoke).Invoke(); return true;
        }
        return false;
    }

    internal static Task<bool> TryWindowAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes, ExitRecipe recipe, CancellationToken ct)
        => ForProcessesAsync(processes, (held, token) =>
        {
            var window = ExitNative.Locate(recipe.Window, held);
            if (window == IntPtr.Zero) return false;
            var visible = ExitNative.IsWindowVisible(window) && !ExitNative.IsIconic(window);
            AutomationElement? current = AutomationElement.FromHandle(window);
            foreach (var selector in recipe.Elements)
            {
                token.ThrowIfCancellationRequested();
                current = Unique(current!, selector);
                if (current is null) return false;
                if (selector != recipe.Elements[^1] && current.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                {
                    if (!visible) return false;
                    token.ThrowIfCancellationRequested();
                    if (!ExitNative.Owns(window, held)) return false;
                    ((ExpandCollapsePattern)expand).Expand();
                }
            }
            return current is not null && Invoke(current, () => ExitNative.Owns(window, held), token);
        }, ct);

    internal static ExitRecipe? CaptureElement(AutomationElement element, int pid, IntPtr surface = default)
    {
        if (element.Current.ProcessId != pid || element.Current.ControlType is var type && type != ControlType.MenuItem && type != ControlType.Button) return null;
        var leaf = Describe(element);
        if (leaf.Name.Length == 0) return null;
        var path = new List<ExitElement> { leaf };
        var controlHandle = new IntPtr(element.Current.NativeWindowHandle);
        var current = element;
        ExitNative.GetWindowThreadProcessId(surface, out var surfacePid);
        var window = surfacePid == pid ? surface : IntPtr.Zero;
        for (var i = 0; i < 16; i++)
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(current);
            if (parent is null || parent.Current.ProcessId != pid) break;
            var native = new IntPtr(parent.Current.NativeWindowHandle);
            if (native != IntPtr.Zero && ExitNative.GetAncestor(native, 2) == native) window = native;
            if (parent.Current.ControlType == ControlType.MenuItem) path.Insert(0, Describe(parent));
            current = parent;
        }
        if (window == IntPtr.Zero) return null;
        if (ExitNative.Class(window) == "#32768")
        {
            var popup = ExitNative.Popup(window);
            if (popup.Owner != IntPtr.Zero) window = popup.Owner;
        }
        var controlClass = controlHandle == IntPtr.Zero ? "" : ExitNative.Class(controlHandle);
        var nativeButton = element.Current.ControlType == ControlType.Button && controlHandle != IntPtr.Zero && controlHandle != window
            && (controlClass.Equals("Button", StringComparison.OrdinalIgnoreCase) || controlClass.StartsWith("WindowsForms10.BUTTON", StringComparison.OrdinalIgnoreCase));
        return new() { Method = ExitMethod.Automation, Window = new(ExitNative.Class(window), ExitNative.Title(window)), Label = ExitNative.Label(leaf.Name), Elements = path,
            Control = nativeButton ? new(controlClass, ExitNative.Title(controlHandle), ExitNative.GetDlgCtrlID(controlHandle)) : null };
    }

    internal static ExitElement? CaptureShellButton(Point point)
    {
        var element = AutomationElement.FromPoint(point);
        for (var depth = 0; depth < 5 && element is not null; depth++, element = TreeWalker.ControlViewWalker.GetParent(element))
        {
            if (element.Current.ControlType != ControlType.Button) continue;
            var current = element;
            for (var i = 0; i < 12 && current is not null; i++, current = TreeWalker.ControlViewWalker.GetParent(current))
                if (IsTrayRoot(current)) return Describe(element);
        }
        // A newly opened context menu may cover the original icon before the sample is read.
        var matches = TrayRoots().SelectMany(root => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>())
            .Where(button => !button.Current.IsOffscreen && button.Current.BoundingRectangle.Contains(point)).ToArray();
        return matches.Length == 1 ? Describe(matches[0]) : null;
    }
    private static bool IsTrayRoot(AutomationElement element) => IsTrayClass(element.Current.ClassName);
    internal static bool IsTrayClass(string name) => name is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    private static bool TrayRootVisible(AutomationElement root)
    {
        var native = new IntPtr(root.Current.NativeWindowHandle);
        if (native != IntPtr.Zero) return ExitNative.IsWindowVisible(native);
        return ExitNative.Windows(new HashSet<int> { root.Current.ProcessId }).Any(w => w.Visible && w.ClassName == root.Current.ClassName);
    }
    private static IEnumerable<AutomationElement> TrayRoots() => AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
        .Cast<AutomationElement>().Where(root => IsTrayRoot(root) && TrayRootVisible(root));
    internal static AutomationElement? TrayOpener()
    {
        var matches = TrayRoots().SelectMany(root => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>())
            .Where(button => !button.Current.IsOffscreen && (button.Current.AutomationId == "HiddenIconButton"
                || button.Current.Name.Contains("скрыт", StringComparison.OrdinalIgnoreCase)
                || button.Current.Name.Contains("hidden", StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static AutomationElement? TrayButton(ExitElement selector)
    {
        var matches = new List<AutomationElement>();
        foreach (AutomationElement root in TrayRoots())
            {
                var found = root.FindAll(TreeScope.Descendants, Match(selector));
                foreach (AutomationElement button in found)
                    if (!button.Current.IsOffscreen) matches.Add(button);
            }
        return matches.Count == 1 ? matches[0] : null;
    }
    internal static AutomationElement? ReadyTrayButton(ExitElement selector, CancellationToken ct, Action<string>? trace = null)
    {
        var waiting = Stopwatch.StartNew();
        Rect previous = Rect.Empty;
        while (waiting.ElapsedMilliseconds < 450)
        {
            ct.ThrowIfCancellationRequested();
            var icon = TrayButton(selector);
            if (icon is not null)
            {
                var bounds = icon.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds == previous)
                {
                    var point = new ExitNative.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                    var hit = ExitNative.Class(ExitNative.GetAncestor(ExitNative.WindowFromPoint(point), 2));
                    if (IsTrayClass(hit)) return icon;
                    trace?.Invoke("Icon rectangle is covered by " + hit);
                }
                previous = bounds;
            }
            Thread.Sleep(15);
        }
        return null;
    }

    internal static async Task<bool> TryTrayAsync(IReadOnlyList<(IntPtr Handle, int Pid)> processes, ExitRecipe recipe, CancellationToken ct,
        Action<string>? trace = null, bool protectUser = true)
    {
        var issued = 0;
        var result = await ForProcessesAsync(processes, (held, token) =>
        {
            // Keep the lease until the UIA worker actually exits, even if its provider times out.
            if (!TrayGate.Wait(0)) return false;
            try
            {
                using var interaction = protectUser ? new TrayInteractionGuard(token) : null;
                if (interaction is { Ready: false }) { trace?.Invoke("Tray replay deferred for user activity"); return false; }
                return ReplayTray(held, recipe, interaction?.Token ?? token, trace, interaction, () =>
                {
                    Interlocked.Exchange(ref issued, 1);
                    trace?.Invoke("Exit command issued");
                });
            }
            finally { TrayGate.Release(); }
        }, ct, 2200);
        return result || Volatile.Read(ref issued) != 0;
    }

    private static bool ReplayTray(IReadOnlyList<(IntPtr Handle, int Pid)> held, ExitRecipe recipe, CancellationToken token,
        Action<string>? trace, TrayInteractionGuard? interaction, Action issued)
    {
            if (recipe.TrayIcon is null || ExitNative.IsModal(held)) { trace?.Invoke("No icon or modal interaction"); return false; }
        var foreground = GetForegroundWindow();
        var before = ExitNative.Windows(held.Select(p => p.Pid).ToHashSet()).Where(w => w.Visible).Select(w => w.Handle).ToHashSet();
        var openedOverflow = false;
        AutomationElement? opener = null;
        try
        {
            var icon = ReadyTrayButton(recipe.TrayIcon, token, trace);
            trace?.Invoke(icon is null ? "Icon not visible" : "Icon visible");
            if (icon is null)
            {
                opener = recipe.TrayOpener is null ? TrayOpener() : TrayButton(recipe.TrayOpener);
                if (opener is null || !Invoke(opener, () => held.Any(p => ExitNative.Alive(p.Handle)), token)) { trace?.Invoke("Cannot open overflow"); return false; }
                openedOverflow = true;
                var ready = Stopwatch.StartNew();
                while ((icon = ReadyTrayButton(recipe.TrayIcon, token, trace)) is null && ready.ElapsedMilliseconds < 500)
                { token.ThrowIfCancellationRequested(); Thread.Sleep(15); }
            }
            if (icon is null) { trace?.Invoke("No icon after overflow"); return false; }
            trace?.Invoke("Tray icon ready");
            token.ThrowIfCancellationRequested();
            var bounds = icon.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1 || !held.Any(p => ExitNative.Alive(p.Handle))) return false;
            var x = (int)(bounds.Left + bounds.Width / 2); var y = (int)(bounds.Top + bounds.Height / 2);
            var hitClass = ExitNative.Class(ExitNative.GetAncestor(ExitNative.WindowFromPoint(new(x, y)), 2));
            if (!IsTrayClass(hitClass)) { trace?.Invoke("Icon obscured by " + hitClass); return false; }
            using var popupDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            popupDeadline.CancelAfter(800);
            var popupToken = popupDeadline.Token;
            if (!RightClick(x, y, held, popupToken, trace, interaction)) return false;
            var waiting = Stopwatch.StartNew();
            while (waiting.ElapsedMilliseconds < 650)
            {
                popupToken.ThrowIfCancellationRequested();
                foreach (var popup in ExitNative.Windows(held.Where(p => ExitNative.Alive(p.Handle)).Select(p => p.Pid).ToHashSet())
                    .Where(w => w.Visible && (w.ClassName == "#32768" || w.Owner != IntPtr.Zero || w.ClassName.StartsWith("HwndWrapper[", StringComparison.Ordinal))))
                {
                    var root = AutomationElement.FromHandle(popup.Handle);
                    var label = recipe.Method == ExitMethod.Automation && recipe.Elements.Count > 0 ? recipe.Elements[^1].Name : recipe.Label;
                    var leaves = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label));
                    trace?.Invoke(popup.ClassName + ": matches=" + leaves.Count);
                    if (leaves.Count == 1 && ClickMenuItem(leaves[0], popup.Handle, held, popupToken, interaction, issued))
                    {
                        // Let the input reach the menu before any cleanup message can dismiss it.
                        for (var i = 0; i < 20 && !popupToken.IsCancellationRequested && ExitNative.Owns(popup.Handle, held) && ExitNative.IsWindowVisible(popup.Handle); i++)
                            Thread.Sleep(10);
                        return true;
                    }
                }
                Thread.Sleep(10);
            }
            return false;
        }
        finally
        {
            if (interaction?.UserInterrupted == true) trace?.Invoke("User interrupted tray replay");
            // New user input owns the desktop now; never undo their cursor, focus, or tray state.
            if (interaction?.UserInterrupted != true)
            {
                ExitNative.CancelNewMenus(held, before, () => interaction?.UserInterrupted != true);
                if (openedOverflow && opener is not null && interaction?.UserInterrupted != true)
                {
                    try
                    {
                        bool OverflowOpen() => TrayRoots().Any(root => root.Current.ClassName is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
                            && !root.Current.IsOffscreen);
                        if (OverflowOpen() && interaction?.UserInterrupted != true)
                        {
                            if (opener.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                            {
                                if (((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Expanded && interaction?.UserInterrupted != true) ((ExpandCollapsePattern)expand).Collapse();
                            }
                            else if (opener.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                            {
                                if (((TogglePattern)toggle).Current.ToggleState == ToggleState.On && interaction?.UserInterrupted != true) ((TogglePattern)toggle).Toggle();
                            }
                            else Invoke(opener, () => OverflowOpen() && interaction?.UserInterrupted != true, CancellationToken.None);
                        }
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                }
                // Do not restore a minimized window or steal focus back from new user input.
                var active = GetForegroundWindow();
                if (foreground != IntPtr.Zero && ExitNative.IsWindowVisible(foreground) && !ExitNative.IsIconic(foreground)
                    && (active == IntPtr.Zero || ExitNative.Owns(active, held) || ExitNative.Class(active) is "Shell_TrayWnd" or "NotifyIconOverflowWindow")
                    && interaction?.UserInterrupted != true)
                    SetForegroundWindow(foreground);
            }
        }
    }

    private static bool RightClick(int x, int y, IReadOnlyList<(IntPtr Handle, int Pid)> processes, CancellationToken ct,
        Action<string>? trace = null, TrayInteractionGuard? interaction = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!ExitNative.GetCursorPos(out var original)) return false;
        var before = ExitNative.Windows(processes.Select(p => p.Pid).ToHashSet()).Where(w => w.Visible).Select(w => w.Handle).ToHashSet();
        var sent = ClickAt(x, y, true);
        trace?.Invoke($"Right-click requested {x},{y}, sent={sent}");
        try
        {
            var waiting = Stopwatch.StartNew();
            while (sent && waiting.ElapsedMilliseconds < 300)
            {
                ct.ThrowIfCancellationRequested();
                if (ExitNative.Windows(processes.Where(p => ExitNative.Alive(p.Handle)).Select(p => p.Pid).ToHashSet()).Any(w => w.Visible && !before.Contains(w.Handle))) break;
                Thread.Sleep(5);
            }
            if (ExitNative.GetCursorPos(out var actual)) trace?.Invoke($"After click {actual.X},{actual.Y}; root=" + ExitNative.Class(ExitNative.GetAncestor(ExitNative.WindowFromPoint(actual), 2)));
            trace?.Invoke("Visible target windows: " + string.Join(",", ExitNative.Windows(processes.Select(p => p.Pid).ToHashSet()).Where(w => w.Visible).Select(w => w.ClassName)));
            return sent;
        }
        finally { if (interaction?.UserInterrupted != true && ExitNative.GetCursorPos(out var after) && after.X == x && after.Y == y) SetCursorPos(original.X, original.Y); }
    }

    internal static bool OpenTrayMenu(AutomationElement icon, IReadOnlyList<(IntPtr Handle, int Pid)> processes, CancellationToken ct, Action<string>? trace = null)
    {
        ct.ThrowIfCancellationRequested();
        var bounds = icon.Current.BoundingRectangle;
        if (bounds.IsEmpty || icon.Current.IsOffscreen) return false;
        var ready = ReadyTrayButton(Describe(icon), ct, trace);
        if (ready is null) return false;
        bounds = ready.Current.BoundingRectangle;
        return RightClick((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2), processes, ct, trace);
    }

    internal static bool ClickMenuItem(AutomationElement element, IntPtr popup,
        IReadOnlyList<(IntPtr Handle, int Pid)> processes, CancellationToken ct, TrayInteractionGuard? interaction = null, Action? issued = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!ExitNative.Owns(popup, processes) || !element.Current.IsEnabled || element.Current.IsOffscreen) return false;
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
        var point = new ExitNative.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        // A new foreground window must never receive a click intended for this menu.
        if (!ExitNative.Owns(ExitNative.WindowFromPoint(point), processes)) return false;
        ct.ThrowIfCancellationRequested();
        if (!ExitNative.GetCursorPos(out var original)) return false;
        ct.ThrowIfCancellationRequested();
        var sent = ClickAt(point.X, point.Y, false);
        if (sent) issued?.Invoke();
        try
        {
            // WinForms menus inspect the current cursor while handling mouse-up.
            // Keep it on the selected item until the popup consumes the click.
            var waiting = Stopwatch.StartNew();
            while (sent && !ct.IsCancellationRequested && ExitNative.Owns(popup, processes) && ExitNative.IsWindowVisible(popup) && waiting.ElapsedMilliseconds < 150)
                Thread.Sleep(5);
            return sent;
        }
        finally
        {
            if (interaction?.UserInterrupted != true && ExitNative.GetCursorPos(out var after) && after.X == point.X && after.Y == point.Y) SetCursorPos(original.X, original.Y);
        }
    }

    private static bool ClickAt(int x, int y, bool right)
        => SendPointer(x, y, right);

    private static bool SetCursorPos(int x, int y) => SendPointer(x, y, null);

    private static bool SendPointer(int x, int y, bool? right)
    {
        var previousDpi = ExitNative.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            if (width <= 1 || height <= 1) return false;
            // Movement and buttons must enter the same input stream in order. SetCursorPos followed
            // by immediate restoration can otherwise send the click to the previous cursor position.
            var move = new MouseInput { X = (int)Math.Round((x - left) * 65535d / (width - 1)),
                Y = (int)Math.Round((y - top) * 65535d / (height - 1)), Flags = 0x8000 | 0x4000 | 0x1, Extra = TrayInteractionGuard.InputTag };
            var inputs = right is null ? new[] { new Input { Mouse = move } }
                : new[] { new Input { Mouse = move }, new Input { Mouse = new MouseInput { Flags = right.Value ? 0x8u : 0x2u, Extra = TrayInteractionGuard.InputTag } }, new Input { Mouse = new MouseInput { Flags = right.Value ? 0x10u : 0x4u, Extra = TrayInteractionGuard.InputTag } } };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
        }
        finally { if (previousDpi != IntPtr.Zero) ExitNative.SetThreadDpiAwarenessContext(previousDpi); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
