using System.Runtime.InteropServices;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

public sealed record PackagedProcess(ProcessIdentity Identity, int ParentPid, string? ApplicationId);

// A launch lease follows only verified descendants of the exact process returned by Windows activation.
public sealed class PackagedProcessSession(ProcessIdentity root, string applicationId)
{
    private readonly Dictionary<int, ProcessIdentity> known = new() { [root.Pid] = root };
    private readonly HashSet<string> applicationIds = new(StringComparer.OrdinalIgnoreCase) { applicationId };
    private static readonly HashSet<string> NoExecutableNames = [];

    // Include former lease PIDs even when they now belong to another app, so PID reuse stays visible.
    internal IReadOnlyList<PackagedProcess> CaptureCurrent(CancellationToken ct) => Capture(ct, NoExecutableNames, applicationIds, known.Keys.ToHashSet());

    public IReadOnlyList<ProcessIdentity> Update(IReadOnlyList<PackagedProcess> snapshot)
    {
        var current = snapshot.ToDictionary(p => p.Identity.Pid);
        bool added;
        do
        {
            added = false;
            foreach (var process in snapshot)
            {
                var identity = process.Identity;
                if (known.ContainsKey(identity.Pid) || !string.Equals(process.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase)
                    || !known.TryGetValue(process.ParentPid, out var parent) || identity.StartTimeUtcTicks < parent.StartTimeUtcTicks) continue;
                // A reused parent PID must not connect an independent launch to this lease.
                if (current.TryGetValue(parent.Pid, out var actualParent) && actualParent.Identity != parent
                    && actualParent.Identity.StartTimeUtcTicks <= identity.StartTimeUtcTicks) continue;
                known[identity.Pid] = identity;
                added = true;
            }
        } while (added);
        return snapshot.Where(p => known.TryGetValue(p.Identity.Pid, out var expected) && expected == p.Identity
                && string.Equals(p.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Identity).OrderByDescending(p => p.StartTimeUtcTicks).ToArray();
    }

    public static IReadOnlyList<PackagedProcess> Capture(CancellationToken ct,
        IReadOnlySet<string>? executableNames = null, IReadOnlySet<string>? applicationIds = null, IReadOnlySet<int>? includedPids = null)
    {
        var processes = new List<PackagedProcess>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return processes;
            do
            {
                ct.ThrowIfCancellationRequested();
                var matchesName = executableNames?.Contains(entry.ExeFile) == true;
                var matchesPid = includedPids?.Contains((int)entry.Pid) == true;
                if (executableNames is not null && !matchesName && !matchesPid && applicationIds is null) continue;
                var handle = OpenProcess(0x1000, false, entry.Pid);
                if (handle == IntPtr.Zero)
                {
                    if (matchesName && Marshal.GetLastWin32Error() == 5)
                        throw new InvalidOperationException($"Нет доступа к {entry.ExeFile}. Запустите обе программы с одинаковыми правами.");
                    continue;
                }
                try
                {
                    var applicationId = executableNames is null || applicationIds is not null ? PackagedProgramService.ReadApplicationId(handle) : null;
                    if (!matchesName && !matchesPid && executableNames is not null && (applicationId is null || applicationIds?.Contains(applicationId) != true)) continue;
                    var identity = WindowsProcessService.ReadIdentity(handle, (int)entry.Pid);
                    if (identity is not null) processes.Add(new(identity, (int)entry.ParentPid, applicationId));
                }
                finally { CloseHandle(handle); }
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return processes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
