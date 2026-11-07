using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Text;
using AppFlow.Core;

namespace AppFlow.Desktop.Services;

public static class PackagedProgramService
{
    private static readonly ConcurrentDictionary<string, string> ApplicationIds = new(StringComparer.OrdinalIgnoreCase);

    // Called on the catalogue's STA thread. AppsFolder supplies localized names and current-user registrations.
    public static IReadOnlyList<InstalledProgram> Discover(CancellationToken ct)
    {
        var programs = new List<InstalledProgram>();
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return programs;
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(index);
                    var id = (string)((dynamic)item).Path;
                    if (!id.Contains('!')) continue;
                    var name = (string)((dynamic)item).Name;
                    var directory = ((dynamic)item).ExtendedProperty("System.AppUserModel.PackageInstallPath") as string;
                    string? executable = null;
                    if (directory is not null)
                    {
                        try { executable = ResolveExecutable(directory, id); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { }
                    }
                    if (executable is not null && !File.Exists(executable)) executable = null;
                    if (executable is not null) ApplicationIds[executable] = id;
                    programs.Add(new(name, executable, directory));
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { }
                finally { Release(item); }
            }
        }
        catch (COMException) { }
        finally { Release(items); Release(folder); Release(shell); }
        return programs;
    }

    public static string? ResolveExecutable(string directory, string applicationId)
    {
        var separator = applicationId.IndexOf('!');
        if (separator < 1 || separator == applicationId.Length - 1) return null;
        var manifest = XDocument.Load(Path.Combine(directory, "AppxManifest.xml"));
        var application = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Applications")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == applicationId[(separator + 1)..]);
        var relative = (string?)application?.Attribute("Executable");
        if (string.IsNullOrWhiteSpace(relative) || !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(relative)) return null;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public static async Task<string?> FindApplicationIdAsync(string executable, CancellationToken ct)
    {
        if (ApplicationIds.TryGetValue(executable, out var id)) return id;
        if (!executable.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
            && !executable.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase)) return null;
        // Also initialize the mapping after restart, before the picker has ever been opened.
        await InstalledProgramService.DiscoverAsync(ct);
        return ApplicationIds.TryGetValue(executable, out id) ? id : null;
    }

    public static ProcessIdentity Activate(string applicationId, string executable)
    {
        object? manager = null;
        try
        {
            manager = new ApplicationActivationManager();
            ((IApplicationActivationManager)manager).ActivateApplication(applicationId, "", 2, out var pid);
            using var process = Process.GetProcessById(checked((int)pid));
            var identity = WindowsProcessService.ReadIdentity(process.Handle, process.Id);
            if (identity is null || !string.Equals(ReadApplicationId(process.Handle), applicationId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Приложение Store запущено, но его процесс не удалось проверить. Автоматическое завершение недоступно.");
            return identity;
        }
        finally { Release(manager); }
    }

    public static string? ReadApplicationId(IntPtr handle)
    {
        var buffer = new StringBuilder(128);
        uint length = (uint)buffer.Capacity;
        var result = GetApplicationUserModelId(handle, ref length, buffer);
        if (result == 122 && length <= 32768)
        {
            buffer = new StringBuilder((int)length);
            result = GetApplicationUserModelId(handle, ref length, buffer);
        }
        return result == 0 ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr handle, ref uint length, StringBuilder value);

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}
