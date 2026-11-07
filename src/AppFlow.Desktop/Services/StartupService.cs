using Microsoft.Win32;

namespace AppFlow.Desktop.Services;

public static class StartupService
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь AppFlow.");
            key.SetValue("AppFlow", $"\"{executable}\" --background");
        }
        else key.DeleteValue("AppFlow", false);
    }
}
