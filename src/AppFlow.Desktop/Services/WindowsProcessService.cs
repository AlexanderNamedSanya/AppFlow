using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

public sealed class WindowsProcessService : IProcessService
{
    private readonly TimeSpan gracefulTimeout;
    private readonly CloseProfileStore? closeProfiles;
    public WindowsProcessService() : this(TimeSpan.FromSeconds(30)) { }
    public WindowsProcessService(CloseProfileStore closeProfiles) : this(TimeSpan.FromSeconds(30), closeProfiles) { }
    internal WindowsProcessService(TimeSpan gracefulTimeout, CloseProfileStore? closeProfiles = null)
    { this.gracefulTimeout = gracefulTimeout; this.closeProfiles = closeProfiles; }
    private readonly ScriptRunner scripts = new();
    private readonly Dictionary<ProcessIdentity, PackagedProcessSession> packagedSessions = [];
    private readonly HashSet<ProcessIdentity> scriptProcesses = [];
    public async Task<ProcessIdentity?> LaunchScriptIfAbsentAsync(string path, CancellationToken ct)
    {
        var identity = await scripts.LaunchAsync(path, this, ct);
        if (identity is not null) scriptProcesses.Add(identity);
        return identity;
    }
    public Task OpenWebsiteAsync(string address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var browser = Process.Start(new ProcessStartInfo(ActionTarget.Website(address)) { UseShellExecute = true });
        return Task.CompletedTask;
    }
    public async Task<ProcessSnapshot> SnapshotAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var found = new List<ProcessIdentity>();
        var packagedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var id = await PackagedProgramService.FindApplicationIdAsync(path, cancellationToken);
            if (id is not null) packagedTargets[path] = id;
        }
        var targets = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = paths.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return new ProcessSnapshot(found);
        // One native enumeration for every target; query full paths only for matching candidates.
        var captured = PackagedProcessSession.Capture(cancellationToken, names,
            packagedTargets.Count == 0 ? null : packagedTargets.Values.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var process in captured)
            if (targets.Contains(process.Identity.Executable)) found.Add(process.Identity);
        var runningTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packagedTargets.Count > 0)
        {
            foreach (var (path, id) in packagedTargets)
                if (captured.Any(p => string.Equals(p.ApplicationId, id, StringComparison.OrdinalIgnoreCase))) runningTargets.Add(path);
        }
        return new ProcessSnapshot(found, runningTargets);
    }

    public async Task<ProcessIdentity?> LaunchIfAbsentAsync(string executable, CancellationToken cancellationToken)
    {
        if ((await SnapshotAsync([executable], cancellationToken)).Contains(executable)) return null;
        if (!File.Exists(executable)) throw new FileNotFoundException("Исполняемый файл недоступен. Выберите его заново.", executable);
        try
        {
            var applicationId = await PackagedProgramService.FindApplicationIdAsync(executable, cancellationToken);
            if (applicationId is not null)
            {
                // Discovery can take time; recheck before activation to avoid adopting a user's instance.
                if ((await SnapshotAsync([executable], cancellationToken)).Contains(executable)) return null;
                cancellationToken.ThrowIfCancellationRequested();
                var packagedIdentity = PackagedProgramService.Activate(applicationId, executable);
                packagedSessions[packagedIdentity] = new(packagedIdentity, applicationId);
                return packagedIdentity;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! })
                ?? throw new InvalidOperationException("Не удалось запустить приложение.");
            // Capture the handle immediately; never adopt a child process of a launcher.
            var identity = ReadIdentity(process.Handle, process.Id);
            return identity ?? throw new InvalidOperationException("Процесс завершился до регистрации. Для лаунчера выберите EXE самой программы.");
        }
        catch (Win32Exception ex) { throw new InvalidOperationException($"Ошибка запуска: {ex.Message}", ex); }
    }

    public Task<bool> IsSameProcessAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (packagedSessions.TryGetValue(identity, out var session))
        {
            var alive = session.Update(session.CaptureCurrent(cancellationToken)).Count > 0;
            if (!alive) packagedSessions.Remove(identity);
            return Task.FromResult(alive);
        }
        var handle = OpenProcess(0x1000, false, identity.Pid);
        if (handle == IntPtr.Zero) return Task.FromResult(false);
        try { return Task.FromResult(GetExitCodeProcess(handle, out var code) && code == 259 && Matches(ReadIdentity(handle, identity.Pid), identity)); }
        finally { CloseHandle(handle); }
    }

    internal async Task<IReadOnlyList<ProcessIdentity>> OwnedIdentitiesAsync(ProcessIdentity launch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (packagedSessions.TryGetValue(launch, out var session)) return session.Update(session.CaptureCurrent(ct));
        return await IsSameProcessAsync(launch, ct) ? [launch] : [];
    }

    public async Task<bool> CloseAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        await CloseWithResultAsync(identity, cancellationToken) is ProcessCloseResult.Closed or ProcessCloseResult.Forced;

    public async Task<ProcessCloseResult> CloseWithResultAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        var stopScript = scriptProcesses.Contains(identity);
        var profiles = stopScript || closeProfiles is null ? [] : await closeProfiles.FindAllAsync(identity.Executable, cancellationToken);
        if (packagedSessions.TryGetValue(identity, out var session))
        {
            var waiting = Stopwatch.StartNew();
            var forced = false;
            // Share one deadline, including any verified replacement processes.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var live = session.Update(session.CaptureCurrent(cancellationToken));
                if (live.Count == 0)
                {
                    packagedSessions.Remove(identity);
                    return forced ? ProcessCloseResult.Forced : ProcessCloseResult.Closed;
                }
                var remaining = gracefulTimeout - waiting.Elapsed;
                if (remaining <= TimeSpan.Zero) return ProcessCloseResult.StillRunning;
                var result = await CloseVerifiedProcessesAsync(live, cancellationToken, remaining, profiles: profiles);
                if (result is not (ProcessCloseResult.Closed or ProcessCloseResult.Forced)) return result;
                forced |= result == ProcessCloseResult.Forced;
            }
            if (session.Update(session.CaptureCurrent(cancellationToken)).Count != 0) return ProcessCloseResult.StillRunning;
            packagedSessions.Remove(identity);
            return forced ? ProcessCloseResult.Forced : ProcessCloseResult.Closed;
        }
        // A background script has no application close protocol; stopping its own interpreter is explicit.
        var closedScript = await CloseVerifiedProcessesAsync([identity], cancellationToken,
            stopScript ? TimeSpan.Zero : gracefulTimeout, stopScript, profiles);
        if (closedScript is ProcessCloseResult.Closed or ProcessCloseResult.Forced) scriptProcesses.Remove(identity);
        return closedScript;
    }

    internal static async Task<ProcessCloseResult> CloseVerifiedProcessesAsync(IReadOnlyList<ProcessIdentity> identities,
        CancellationToken cancellationToken, TimeSpan gracePeriod, bool stopScript = false, IReadOnlyList<CloseProfile>? profiles = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        profiles ??= [];
        var forceClose = stopScript || profiles.Count == 0;
        var verified = new List<(IntPtr Handle, int Pid)>();
        var accessDenied = false;
        try
        {
            foreach (var identity in identities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Termination rights are needed only for scripts and applications without a learned exit.
                var handle = OpenProcess(0x1000 | 0x100000 | (forceClose ? 0x1u : 0u), false, identity.Pid);
                if (handle == IntPtr.Zero)
                {
                    accessDenied |= Marshal.GetLastWin32Error() != 87;
                    continue;
                }
                var actual = ReadIdentity(handle, identity.Pid);
                if (WaitForSingleObject(handle, 0) == 0 || actual is not null && !Matches(actual, identity))
                {
                    CloseHandle(handle);
                    continue;
                }
                if (actual is null) { CloseHandle(handle); accessDenied = true; continue; }
                verified.Add((handle, identity.Pid));
            }
            if (verified.Count == 0) return accessDenied ? ProcessCloseResult.AccessDenied : ProcessCloseResult.Closed;
            if (!forceClose)
            {
                if (accessDenied) return ProcessCloseResult.AccessDenied;
                if (gracePeriod <= TimeSpan.Zero) return ProcessCloseResult.StillRunning;
                var route = await LearnedApplicationCloser.RequestAsync(verified, profiles, cancellationToken);
                if (route != ExitRoute.None)
                    return await LearnedApplicationCloser.WaitAsync(verified, gracePeriod, cancellationToken);
                if (ExitNative.IsModal(verified)) return ProcessCloseResult.ConfirmationRequired;
                return profiles.Any(p => p.BackgroundVerified && p.Recipe.TrayIcon is not null)
                    ? ProcessCloseResult.Deferred : ProcessCloseResult.CloseUnavailable;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var forced = false;
            foreach (var process in verified)
            {
                if (WaitForSingleObject(process.Handle, 0) == 0) continue;
                if (TerminateProcess(process.Handle, 1)) forced = true;
                else if (WaitForSingleObject(process.Handle, 0) != 0) accessDenied = true;
            }
            // TerminateProcess is asynchronous; success is reported only after every handle signals exit.
            var waiting = Stopwatch.StartNew();
            while (verified.Any(p => WaitForSingleObject(p.Handle, 0) != 0) && waiting.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50, cancellationToken);
            if (accessDenied) return ProcessCloseResult.AccessDenied;
            if (verified.Any(p => WaitForSingleObject(p.Handle, 0) != 0)) return ProcessCloseResult.StillRunning;
            return forced ? ProcessCloseResult.Forced : ProcessCloseResult.Closed;
        }
        finally { foreach (var process in verified) CloseHandle(process.Handle); }
    }

    private static bool Matches(ProcessIdentity? actual, ProcessIdentity expected) => actual is not null && actual.Pid == expected.Pid
        && actual.StartTimeUtcTicks == expected.StartTimeUtcTicks && string.Equals(actual.Executable, expected.Executable, StringComparison.OrdinalIgnoreCase);

    internal static ProcessIdentity? ReadIdentity(IntPtr handle, int pid)
    {
        var length = 512;
        var path = new StringBuilder(length);
        if (!QueryFullProcessImageName(handle, 0, path, ref length))
        {
            if (Marshal.GetLastWin32Error() != 122) return null;
            length = 32768;
            path = new StringBuilder(length);
            if (!QueryFullProcessImageName(handle, 0, path, ref length)) return null;
        }
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
        return new(pid, created, path.ToString());
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
