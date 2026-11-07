using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

public sealed class ScriptRunner
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ProcessIdentity> started = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessIdentity?> LaunchAsync(string path, WindowsProcessService processes, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        if (!ActionTarget.IsScript(path) || !File.Exists(path)) throw new FileNotFoundException("Скрипт недоступен. Выберите его заново.", path);
        await gate.WaitAsync(ct);
        try
        {
            if (started.TryGetValue(path, out var previous) && await processes.IsSameProcessAsync(previous, ct)) return null;
            started.Remove(path);
            var names = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? "Name='powershell.exe' OR Name='pwsh.exe'" : "Name='cmd.exe'";
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE " + names);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item["CommandLine"] is not string command)
                        throw new InvalidOperationException("Нет доступа к командной строке интерпретатора. Запустите скрипт и AppFlow с одинаковыми правами.");
                    if (MatchesInvocation(command, path)) return null;
                }
            }
            using var process = Process.Start(CreateStartInfo(path)) ?? throw new InvalidOperationException("Не удалось запустить скрипт.");
            var identity = WindowsProcessService.ReadIdentity(process.Handle, process.Id)
                ?? throw new InvalidOperationException("Не удалось зарегистрировать экземпляр скрипта.");
            await Task.Delay(150, ct);
            if (process.HasExited && process.ExitCode != 0)
                throw new InvalidOperationException($"Скрипт завершился с кодом {process.ExitCode}. Проверьте скрипт и разрешение на его выполнение в Windows.");
            if (!process.HasExited) started[path] = identity;
            return identity;
        }
        catch (ManagementException ex) { throw new InvalidOperationException("Не удалось проверить запущенные скрипты через Windows WMI.", ex); }
        finally { gate.Release(); }
    }

    public static ProcessStartInfo CreateStartInfo(string path)
    {
        path = Path.GetFullPath(path);
        if (!ActionTarget.IsScript(path)) throw new InvalidDataException("Поддерживаются скрипты .ps1, .bat и .cmd.");
        var powershell = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = powershell ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, CreateNoWindow = true
        };
        if (powershell)
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", path }) info.ArgumentList.Add(argument);
        }
        else
        {
            // Percent expansion runs inside quoted cmd paths. Reject it instead of executing another path.
            if (path.Contains('%')) throw new InvalidDataException("Для .bat/.cmd выберите путь без символа %. Переименуйте файл или папку.");
            info.Arguments = $"/d /s /v:off /c \"\"{path}\"\"";
        }
        return info;
    }

    public static bool MatchesInvocation(string command, string script)
    {
        if (script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var pointer = CommandLineToArgvW(command, out var count);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                for (var i = 1; i + 1 < count; i++)
                    if (string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)), "-File", StringComparison.OrdinalIgnoreCase))
                        return SamePath(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, (i + 1) * IntPtr.Size)), script);
                return false;
            }
            finally { LocalFree(pointer); }
        }
        var match = Regex.Match(command, @"(?:^|\s)/(?:c|k)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var payload = match.Groups[1].Value.Trim();
        if (payload.StartsWith("\"\"", StringComparison.Ordinal) && payload.EndsWith('"')) payload = payload[1..^1];
        if (payload.StartsWith('"'))
        {
            var end = payload.IndexOf('"', 1);
            return end > 1 && SamePath(payload[1..end], script);
        }
        var path = Regex.Match(payload, @"^(.+?\.(?:bat|cmd))(?=$|\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return path.Success && SamePath(path.Groups[1].Value, script);
    }
    private static bool SamePath(string? candidate, string expected)
    {
        try { return candidate is not null && Path.IsPathFullyQualified(candidate) && string.Equals(Path.GetFullPath(candidate), expected, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
