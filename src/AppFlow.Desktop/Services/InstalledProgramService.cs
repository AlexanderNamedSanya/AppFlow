using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppFlow.Desktop.Services;

public sealed record InstalledProgram(string Name, string? Executable, string? InstallDirectory = null)
{
    public AppFlow.Core.TargetKind Kind => Executable is null ? AppFlow.Core.TargetKind.Application : AppFlow.Core.ActionTarget.Kind(Executable);
    public string Description => Executable ?? "Выберите исполняемый файл через «Обзор»";
    public ImageSource? Icon => Executable is null ? null : AppIcon.Get(Executable);
}

public static class InstalledProgramService
{
    // Shell links require COM/STA. Discovery never runs shortcuts or installer commands.
    public static Task<IReadOnlyList<InstalledProgram>> DiscoverAsync(CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<InstalledProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Discover(ct)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "AppFlow program discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<InstalledProgram> Discover(CancellationToken ct)
    {
        var programs = new List<InstalledProgram>();
        programs.AddRange(PackagedProgramService.Discover(ct));
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (IsUtility(name)) continue;
                    object? shellLink = null;
                    try
                    {
                        shellLink = new ShellLink();
                        ((IPersistFile)shellLink).Load(file, 0);
                        var buffer = new StringBuilder(32768);
                        ((IShellLinkPath)shellLink).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
                        var path = ParseExecutablePath(buffer.ToString());
                        if (path is not null && File.Exists(path) && !IsUtility(Path.GetFileNameWithoutExtension(path)))
                            programs.Add(new(name, path));
                    }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
                    finally { if (shellLink is not null) Marshal.FinalReleaseComObject(shellLink); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ct.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(hive, view);
            ReadRegistry(root, @"Software\Microsoft\Windows\CurrentVersion\App Paths", key =>
            {
                var path = ParseExecutablePath(key.GetValue(null) as string);
                if (path is not null && File.Exists(path) && !IsUtility(Path.GetFileNameWithoutExtension(path)))
                    programs.Add(new(AppIcon.DisplayName(path), path));
            }, ct);
            ReadRegistry(root, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", key =>
            {
                if (key.GetValue("SystemComponent") is int component && component == 1 || key.GetValue("ParentKeyName") is string) return;
                var name = key.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) return;
                var path = ParseExecutablePath(key.GetValue("DisplayIcon") as string);
                if (path is not null && (!File.Exists(path) || IsUtility(Path.GetFileNameWithoutExtension(path)))) path = null;
                // Never guess an application's EXE from an uninstaller or icon DLL.
                programs.Add(new(name, path, key.GetValue("InstallLocation") as string));
            }, ct);
        }
        return Merge(programs);
    }

    private static void ReadRegistry(RegistryKey root, string path, Action<RegistryKey> read, CancellationToken ct)
    {
        try
        {
            using var parent = root.OpenSubKey(path);
            if (parent is null) return;
            foreach (var name in parent.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try { using var key = parent.OpenSubKey(name); if (key is not null) read(key); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    public static string? ParseExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        string path;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end < 0) return null;
            path = value[1..end];
        }
        else
        {
            var match = Regex.Match(value, @"^(.+?\.exe)(?=$|[,\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            path = match.Groups[1].Value;
        }
        try { return Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static IReadOnlyList<InstalledProgram> Merge(IEnumerable<InstalledProgram> entries)
    {
        var all = entries.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToArray();
        var resolved = all.Where(p => p.Executable is not null).DistinctBy(p => p.Executable, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = resolved.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return resolved.Concat(all.Where(p => p.Executable is null && !names.Contains(p.Name)).DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.Executable is null).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsUtility(string name) => name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("удал", StringComparison.OrdinalIgnoreCase)
        || name.Equals("setup", StringComparison.OrdinalIgnoreCase) || name.Equals("maintenancetool", StringComparison.OrdinalIgnoreCase);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    // GetPath is the first IShellLinkW vtable method. Persistence is queried separately.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
    }
}
