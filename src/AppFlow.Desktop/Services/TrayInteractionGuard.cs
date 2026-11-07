using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AppFlow.Desktop.Services;

// Installed only during a short tray replay. Observe input without consuming or recording it.
internal sealed class TrayInteractionGuard : IDisposable
{
    internal static readonly UIntPtr InputTag = new(0x4150464C);
    private readonly CancellationTokenSource interrupted;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HookCallback mouse, keyboard;
    private Dispatcher? dispatcher;
    private int userInterrupted, disposed;
    private readonly bool started;
    public bool Ready { get; }
    public bool UserInterrupted => Volatile.Read(ref userInterrupted) != 0;
    public CancellationToken Token => interrupted.Token;

    public TrayInteractionGuard(CancellationToken ct)
    {
        interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        mouse = OnMouse; keyboard = OnKeyboard;
        if (IsDesktopBusy()) return;
        var thread = new Thread(Listen) { IsBackground = true, Name = "AppFlow tray input observer" };
        thread.SetApartmentState(ApartmentState.STA);
        started = true;
        thread.Start();
        try { Ready = ready.Task.Wait(500, ct) && ready.Task.Result && !IsDesktopBusy() && !UserInterrupted; }
        catch { Dispose(); throw; }
    }

    internal static bool IsDesktopBusy()
    {
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref last) || unchecked((uint)Environment.TickCount - last.Time) < 750) return true;
        foreach (var key in new[] { 1, 2, 4, 5, 6, 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return true;
        foreach (var name in new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
            if (FindWindow(name, null) is var overflow && overflow != IntPtr.Zero && ExitNative.IsWindowVisible(overflow)) return true;
        var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        return !GetGUIThreadInfo(0, ref gui) || (gui.Flags & 0x1E) != 0 || gui.Capture != IntPtr.Zero;
    }

    private void Listen()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        IntPtr mouseHook = IntPtr.Zero, keyboardHook = IntPtr.Zero;
        try
        {
            mouseHook = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
            keyboardHook = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
            ready.TrySetResult(mouseHook != IntPtr.Zero && keyboardHook != IntPtr.Zero);
            if (mouseHook != IntPtr.Zero && keyboardHook != IntPtr.Zero && Volatile.Read(ref disposed) == 0) Dispatcher.Run();
        }
        finally
        {
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            ready.TrySetResult(false);
            stopped.TrySetResult();
        }
    }

    internal void ObserveMouse(uint flags, UIntPtr extra)
    {
        // Other automation and accessibility tools also take precedence over our injected clicks.
        if (!IsOwnInput(flags, extra)) Interrupt();
    }
    internal static bool IsOwnInput(uint flags, UIntPtr extra) => (flags & 1) != 0 && extra == InputTag;
    private void Interrupt()
    {
        if (Interlocked.Exchange(ref userInterrupted, 1) == 0) _ = interrupted.CancelAsync();
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Volatile.Read(ref disposed) == 0)
        {
            var input = Marshal.PtrToStructure<MouseHook>(data);
            ObserveMouse(input.Flags, input.Extra);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Volatile.Read(ref disposed) == 0) Interrupt();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        // Hooks may currently be finishing their callback; release the token after that thread exits.
        if (started) _ = stopped.Task.ContinueWith(_ => interrupted.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else interrupted.Dispose();
    }

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public ExitNative.Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public ExitNative.Rect CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
