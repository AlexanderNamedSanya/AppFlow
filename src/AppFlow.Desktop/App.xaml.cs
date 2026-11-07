using System.IO.Pipes;
using System.Windows;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Forms = System.Windows.Forms;

namespace AppFlow.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private AutomationEngine? engine;
    private CancellationTokenSource? pipeLifetime;
    private Task? pipeTask;
    private MainWindow? window;
    private MainViewModel? viewModel;
    private bool exiting;
    private ThemeChoice theme;
    private readonly string pipeName = "AppFlow.Activate." + Environment.UserName;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, @"Local\AppFlow." + Environment.UserName, out ownsMutex);
        if (!ownsMutex)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                await client.ConnectAsync(1500);
                await client.WriteAsync(new byte[] { e.Args.Contains("--exit") ? (byte)2 : (byte)1 });
            }
            catch { MessageBox.Show("AppFlow уже работает. Откройте окно через значок в трее.", "AppFlow"); }
            Shutdown(); return;
        }
        if (e.Args.Contains("--exit")) { Shutdown(); return; }
        try
        {
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            var dataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppFlow");
            var store = new ConfigurationStore(Path.Combine(dataDirectory, "configuration.json"));
            AppConfiguration configuration;
            string? loadError = null;
            try { configuration = await store.LoadAsync(); }
            catch (Exception ex)
            {
                // Preserve corrupt configuration and never silently overwrite it.
                var backup = store.FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(store.FilePath, backup);
                configuration = new();
                loadError = $"Не удалось загрузить настройки: {ex.Message}. Оригинал сохранён: {backup}";
            }
            ApplyTheme(configuration.Settings.Theme);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            var closeProfiles = new CloseProfileStore(Path.Combine(dataDirectory, "close-profiles.json"));
            var processService = new WindowsProcessService(closeProfiles);
            engine = new(processService);
            viewModel = new MainViewModel(configuration, engine, store, ApplyTheme, closeProfiles);
            if (loadError is not null) viewModel.Notice = loadError;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть AppFlow", null, (_, _) => Dispatcher.Invoke(ShowWindow));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, async (_, _) => await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());
            trayIcon = CreateTrayIcon();
            tray = new Forms.NotifyIcon { Text = "AppFlow · мониторинг программ", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
            pipeLifetime = new();
            pipeTask = ListenAsync(pipeLifetime.Token);
            await engine.ConfigureAsync(configuration);
            engine.Start();
            if (!e.Args.Contains("--background") || loadError is not null) ShowWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить AppFlow: {ex.Message}", "AppFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    public void ApplyTheme(ThemeChoice choice)
    {
        theme = choice;
        var dark = choice == ThemeChoice.Dark;
        if (choice == ThemeChoice.System)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (theme == ThemeChoice.System) Dispatcher.BeginInvoke(() => ApplyTheme(ThemeChoice.System));
    }
    private void ShowWindow()
    {
        if (viewModel is null) return;
        if (window is null) MainWindow = window = new(viewModel);
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }
    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, ct) > 0)
                {
                    if (buffer[0] == 2) _ = Dispatcher.BeginInvoke(new Action(() => _ = ExitAsync()));
                    else await Dispatcher.InvokeAsync(ShowWindow);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
    }
    public async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        if (viewModel is not null) await viewModel.FlushChangesAsync();
        if (pipeLifetime is not null) await pipeLifetime.CancelAsync();
        if (pipeTask is not null) await pipeTask;
        if (engine is not null) await engine.DisposeAsync();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        tray?.Dispose();
        pipeLifetime?.Dispose();
        if (window is not null) { window.AllowClose = true; window.Close(); }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        trayIcon?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/AppFlow.ico")).Stream;
        using var original = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)original.Clone();
    }
}
