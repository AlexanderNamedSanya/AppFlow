using System.Runtime.InteropServices;
using System.Text;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

internal static class ExitNative
{
    internal sealed record Window(IntPtr Handle, int Pid, string ClassName, string Title, bool Visible, bool Minimized, IntPtr Owner);
    internal sealed record MenuItem(uint Id, string Label, bool Enabled, IntPtr Submenu, int Index);
    public static bool Alive(IntPtr handle) => WaitForSingleObject(handle, 0) == 258;
    public static int ExitCode(IntPtr handle) => GetExitCodeProcess(handle, out var code) && !Alive(handle)
        ? unchecked((int)code) : throw new InvalidOperationException("Не удалось получить код завершения проверенного процесса.");
    public static bool Owns(IntPtr window, IReadOnlyList<(IntPtr Handle, int Pid)> processes)
    {
        GetWindowThreadProcessId(window, out var pid);
        return processes.Any(p => p.Pid == pid && Alive(p.Handle));
    }
    public static List<Window> Windows(IReadOnlySet<int> pids)
    {
        var result = new List<Window>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains(pid)) result.Add(new(window, pid, Class(window), Title(window), IsWindowVisible(window), IsIconic(window), GetWindow(window, 4)));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static string Class(IntPtr window)
    {
        var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return NormalizeClass(name.ToString());
    }
    public static string NormalizeClass(string name)
    {
        if (name.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return "HwndWrapper[";
        if (name.StartsWith("WindowsForms10.", StringComparison.Ordinal) && name.IndexOf(".app.", StringComparison.Ordinal) is var index && index >= 0)
            return name[..(index + 5)];
        return name;
    }
    public static string Title(IntPtr window) { var name = new StringBuilder(513); GetWindowText(window, name, name.Capacity); return name.ToString(); }
    public static string Label(string text) => text.Split('\t')[0].Replace("&", "").Trim().TrimEnd('…').TrimEnd('.').Trim();
    public static IntPtr Locate(ExitWindow selector, IReadOnlyList<(IntPtr Handle, int Pid)> processes)
    {
        var matches = Windows(processes.Where(p => Alive(p.Handle)).Select(p => p.Pid).ToHashSet())
            .Where(w => w.ClassName == selector.ClassName && w.ClassName != "#32768").ToArray();
        if (matches.Length == 1) return matches[0].Handle;
        var titled = matches.Where(w => w.Title == selector.Title).ToArray();
        return titled.Length == 1 ? titled[0].Handle : IntPtr.Zero;
    }
    public static List<MenuItem> Items(IntPtr menu)
    {
        var result = new List<MenuItem>();
        for (var i = 0; i < Math.Min(GetMenuItemCount(menu), 128); i++)
        {
            var item = new MenuInfo { Size = (uint)Marshal.SizeOf<MenuInfo>(), Mask = 0x1 | 0x2 | 0x4 | 0x40,
                Text = Marshal.AllocHGlobal(1026), TextLength = 512 };
            try
            {
                Marshal.WriteInt16(item.Text, 0);
                if (GetMenuItemInfo(menu, (uint)i, true, ref item))
                    result.Add(new(item.Id, Label(Marshal.PtrToStringUni(item.Text) ?? ""), (item.State & 3) == 0, item.Submenu, i));
            }
            finally { Marshal.FreeHGlobal(item.Text); }
        }
        return result;
    }
    public static (IntPtr Menu, IntPtr Owner) Popup(IntPtr window)
    {
        var bar = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
        if (!GetMenuBarInfo(window, -4, 0, ref bar) || bar.Menu == IntPtr.Zero) return default;
        var thread = GetWindowThreadProcessId(window, out _);
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref gui) ? (bar.Menu, gui.MenuOwner) : default;
    }
    public static ExitRecipe? SelectedMenu(IntPtr window, int child)
    {
        var (menu, owner) = Popup(window);
        if (menu == IntPtr.Zero || owner == IntPtr.Zero) return null;
        var selected = Items(menu).Where(i => i.Enabled && i.Submenu == IntPtr.Zero
            && (child > 0 ? i.Index == child - 1 : (GetMenuState(menu, (uint)i.Index, 0x400) & 0x80) != 0)).ToArray();
        if (selected.Length != 1 || selected[0].Id is 0 or > ushort.MaxValue || selected[0].Label.Length == 0) return null;
        return new() { Method = ExitMethod.MenuCommand, Window = new(Class(owner), Title(owner)), CommandId = selected[0].Id, Label = selected[0].Label };
    }
    public static ExitRecipe? MenuAtPoint(IntPtr window, Point point)
    {
        var (menu, owner) = Popup(window);
        if (menu == IntPtr.Zero || owner == IntPtr.Zero) return null;
        foreach (var item in Items(menu).Where(i => i.Enabled && i.Submenu == IntPtr.Zero && i.Id is > 0 and <= ushort.MaxValue))
            if (GetMenuItemRect(IntPtr.Zero, menu, (uint)item.Index, out var bounds) && point.X >= bounds.Left && point.X < bounds.Right
                && point.Y >= bounds.Top && point.Y < bounds.Bottom)
                return new() { Method = ExitMethod.MenuCommand, Window = new(Class(owner), Title(owner)), CommandId = item.Id, Label = item.Label };
        return null;
    }
    public static bool SendCommand(IntPtr window, uint command, IReadOnlyList<(IntPtr Handle, int Pid)> processes) =>
        command is > 0 and <= ushort.MaxValue && Owns(window, processes) && IsWindowEnabled(window) && !IsHungAppWindow(window)
        && PostMessage(window, 0x111, new IntPtr(command), IntPtr.Zero);
    public static bool ClickControl(IntPtr window, ExitControl control, IReadOnlyList<(IntPtr Handle, int Pid)> processes)
    {
        if (!Owns(window, processes)) return false;
        var matches = new List<IntPtr>();
        EnumChildWindows(window, (child, _) =>
        {
            if (Owns(child, processes) && Class(child) == control.ClassName && Title(child) == control.Text) matches.Add(child);
            return true;
        }, IntPtr.Zero);
        if (matches.Count > 1) matches = matches.Where(w => GetDlgCtrlID(w) == control.Id).ToList();
        return matches.Count == 1 && Owns(matches[0], processes) && IsWindowEnabled(matches[0]) && !IsHungAppWindow(matches[0])
            && PostMessage(matches[0], 0xF5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK executes the real button handler.
    }
    public static bool IsModal(IReadOnlyList<(IntPtr Handle, int Pid)> processes) =>
        Windows(processes.Where(p => Alive(p.Handle)).Select(p => p.Pid).ToHashSet()).Any(w => w.Visible
            && (w.ClassName == "#32770" || w.Owner != IntPtr.Zero && !IsWindowEnabled(w.Owner)));
    internal static void CancelNewMenus(IReadOnlyList<(IntPtr Handle, int Pid)> processes, IReadOnlySet<IntPtr> before, Func<bool>? allowed = null)
    {
        var modal = IsModal(processes);
        foreach (var window in Windows(processes.Where(p => Alive(p.Handle)).Select(p => p.Pid).ToHashSet()).Where(w => w.Visible && !before.Contains(w.Handle)))
        {
            if (allowed?.Invoke() == false) return;
            var native = window.ClassName == "#32768";
            var popup = (GetWindowLong(window.Handle, -16) & unchecked((int)0x80C00000)) == unchecked((int)0x80000000);
            if (!native && (modal || !popup || !(window.ClassName.StartsWith("WindowsForms10.", StringComparison.Ordinal) || window.ClassName == "HwndWrapper["))) continue;
            var owner = native ? Popup(window.Handle).Owner : window.Owner;
            if (owner != IntPtr.Zero && Owns(owner, processes) && allowed?.Invoke() != false) PostMessage(owner, 0x1F, IntPtr.Zero, IntPtr.Zero);
            if (Owns(window.Handle, processes) && allowed?.Invoke() != false)
            {
                PostMessage(window.Handle, 0x1F, IntPtr.Zero, IntPtr.Zero);
                if (!native) { PostMessage(window.Handle, 0x100, new IntPtr(0x1B), IntPtr.Zero); PostMessage(window.Handle, 0x101, new IntPtr(0x1B), IntPtr.Zero); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct MenuBarInfo { public uint Size; public Rect Bar; public IntPtr Menu, MenuWindow; public uint Focus; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MenuInfo
    {
        public uint Size, Mask, Type, State, Id; public IntPtr Submenu, Checked, Unchecked, ItemData, Text; public uint TextLength; public IntPtr Bitmap;
    }
    internal delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] internal static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool position, ref MenuInfo info);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool GetMenuBarInfo(IntPtr window, int objectId, int item, ref MenuBarInfo info);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetPhysicalCursorPos")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source,
        IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    internal static List<(IntPtr Handle, int Pid)> Retain(IReadOnlyList<(IntPtr Handle, int Pid)> processes)
    {
        var retained = new List<(IntPtr Handle, int Pid)>();
        foreach (var process in processes)
            if (DuplicateHandle(new IntPtr(-1), process.Handle, new IntPtr(-1), out var handle, 0, false, 2)) retained.Add((handle, process.Pid));
        return retained;
    }
    public static IntPtr Verify(ProcessIdentity identity)
    {
        var handle = OpenProcess(0x1000 | 0x100000, false, identity.Pid);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        if (Alive(handle) && WindowsProcessService.ReadIdentity(handle, identity.Pid) == identity) return handle;
        CloseHandle(handle); return IntPtr.Zero;
    }
}
