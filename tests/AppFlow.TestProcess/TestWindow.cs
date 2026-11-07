using System.Runtime.InteropServices;

internal static class TestWindow
{
    private delegate IntPtr WindowCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static void Run(string marker, string scenario = "normal")
    {
        var windows = new HashSet<IntPtr>();
        var saving = new HashSet<IntPtr>();
        IntPtr dialog = IntPtr.Zero;
        IntPtr dialogOwner = IntPtr.Zero;
        IntPtr main = IntPtr.Zero;
        var trayAdded = false;
        var tray = new NotifyIconData();
        var type = new WindowClass { ClassName = "AppFlowDisposableTestWindow" };
        void OpenDialog(IntPtr owner)
        {
            dialogOwner = owner;
            EnableWindow(owner, false);
            dialog = CreateWindowEx(0, type.ClassName, "Unsaved data", 0x10CF0000, -32000, -32000, 200, 100,
                owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        WindowCallback callback = (window, message, wParam, lParam) =>
        {
            if (message == 0x8014) { DestroyWindow(window); return IntPtr.Zero; } // Disposable test cleanup only.
            if (message == 0x111) File.AppendAllText(marker + ".commands", wParam.ToInt64() + "\n");
            if (message == 0x18 && wParam != IntPtr.Zero && main != IntPtr.Zero)
                File.AppendAllText(marker + ".shown", "shown\n");
            if (message == 0x111 && wParam.ToInt64() == 42017)
            {
                if (scenario is "menu-return" or "tray-return") { File.WriteAllText(marker + ".ignored-command", "ignored"); return IntPtr.Zero; }
                File.WriteAllText(marker + ".quit", "exit command");
                SetTimer(window, 1, 200, IntPtr.Zero);
                return IntPtr.Zero;
            }
            if (message == 0x800A || message == 0x800B && lParam.ToInt64() == 0x205)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, 0, new UIntPtr(42016), "Show application");
                AppendMenu(menu, 0, new UIntPtr(42017), "E&xit");
                SetForegroundWindow(window);
                GetCursorPos(out var cursor);
                var selected = TrackPopupMenu(menu, scenario is "menu-return" or "tray-return" ? 0x100u : 0u,
                    scenario.StartsWith("tray-", StringComparison.Ordinal) ? cursor.X : 30,
                    scenario.StartsWith("tray-", StringComparison.Ordinal) ? cursor.Y : 30, 0, window, IntPtr.Zero);
                DestroyMenu(menu);
                File.WriteAllText(marker + ".menu-result", selected.ToString());
                if (scenario is "menu-return" or "tray-return" && selected == 42017)
                {
                    File.WriteAllText(marker + ".quit", "exit command"); SetTimer(window, 1, 200, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }
            if (message == 0x11)
            {
                File.AppendAllText(marker + ".query-session", lParam.ToInt64() + "\n");
                // A tray application can ignore window close but implement Windows' save-and-exit protocol.
                return scenario == "session-tray" ? new IntPtr(1) : IntPtr.Zero;
            }
            if (message == 0x16 && wParam != IntPtr.Zero)
            {
                File.AppendAllText(marker + ".end-session", lParam.ToInt64() + "\n");
                if (scenario == "session-tray")
                {
                    File.WriteAllText(marker, "saved before normal exit");
                    DestroyWindow(window);
                }
                return IntPtr.Zero;
            }
            if (message == 0x112 && (wParam.ToInt64() & 0xFFF0) == 0xF060)
                File.AppendAllText(marker + ".system", "system close\n");
            if (message == 0x10)
            {
                File.AppendAllText(marker + ".requests", "close\n");
                if (window == dialog) { File.WriteAllText(marker + ".unsafe", "dialog dismissed"); return IntPtr.Zero; }
                if (scenario == "modal") { OpenDialog(window); return IntPtr.Zero; }
                if (scenario is "tray" or "session-tray" or "popup-menu" or "native-menu" or "visible-menu" or "menu-return" or "tray-icon" or "tray-return") { ShowWindow(window, 0); return IntPtr.Zero; }
                if (scenario == "ignore") return IntPtr.Zero;
                if (scenario == "hung") { Thread.Sleep(Timeout.Infinite); return IntPtr.Zero; }
                if (scenario == "system" && !File.Exists(marker + ".system")) File.WriteAllText(marker + ".unsafe", "system close bypassed");
                if (saving.Count != 0) File.WriteAllText(marker + ".unsafe", "overlapping close requests");
                saving.Add(window);
                SetTimer(window, 1, scenario == "normal" ? 2600u : scenario == "slow" ? 11000u : 300u, IntPtr.Zero);
                return IntPtr.Zero;
            }
            if (message == 0x113)
            {
                if (window == dialog)
                {
                    KillTimer(dialog, 1); DestroyWindow(dialog); dialog = IntPtr.Zero;
                    EnableWindow(dialogOwner, true);
                    return IntPtr.Zero;
                }
                File.WriteAllText(marker, "saved before normal exit"); saving.Remove(window); DestroyWindow(window); return IntPtr.Zero;
            }
            if (message == 2)
            {
                if (trayAdded) { Shell_NotifyIcon(2, ref tray); trayAdded = false; }
                windows.Remove(window); if (windows.Count == 0) PostQuitMessage(0); return IntPtr.Zero;
            }
            return DefWindowProc(window, message, wParam, lParam);
        };
        type.Callback = callback;
        RegisterClass(ref type);
        // Hidden or offscreen: tests never interact with the user's applications.
        var style = scenario == "frameless" ? 0x90000000u : 0x00CF0000u | (scenario is "normal" or "popup-menu" or "native-menu" or "menu-return" or "tray-icon" or "tray-return" ? 0 : 0x10000000u);
        main = CreateWindowEx(0, type.ClassName, "AppFlow test", style, scenario == "visible-menu" ? 160 : -32000, scenario == "visible-menu" ? 160 : -32000, 400, 220, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (main == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create test window.");
        windows.Add(main);
        if (scenario is "native-menu" or "visible-menu")
        {
            var menu = CreateMenu(); var file = CreatePopupMenu();
            AppendMenu(file, 0, new UIntPtr(42017), "E&xit\tAlt+F4");
            AppendMenu(menu, 0x10, (UIntPtr)file, "&File"); SetMenu(main, menu);
        }
        if (scenario is "tray-icon" or "tray-return")
        {
            const string tip = "AppFlow close learning test";
            tray = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = main, Id = 1, Flags = 7, Callback = 0x800B,
                Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)), Tip = tip, Info = "", InfoTitle = "" };
            trayAdded = Shell_NotifyIcon(0, ref tray);
            if (!trayAdded) throw new InvalidOperationException("Could not create disposable tray icon.");
            File.WriteAllText(marker + ".tray-tip", tip);
        }
        if (scenario == "multiple") windows.Add(CreateWindowEx(0, type.ClassName, "Second document", style, -32000, -32000,
            200, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        if (scenario == "preexisting-modal") OpenDialog(main);
        if (scenario == "preexisting-modal-resolve") { OpenDialog(main); SetTimer(dialog, 1, 600, IntPtr.Zero); }
        if (scenario == "disabled") EnableMenuItem(GetSystemMenu(main, false), 0xF060, 1);
        File.WriteAllText(marker + ".ready", "ready");
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        GC.KeepAlive(callback);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowCallback Callback;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string type, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, uint id, uint delay, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, uint id);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")] private static extern uint EnableMenuItem(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr CreateMenu();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr window, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
}
