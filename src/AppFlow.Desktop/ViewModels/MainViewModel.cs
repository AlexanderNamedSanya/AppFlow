using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using Microsoft.Win32;

namespace AppFlow.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AutomationEngine engine;
    private readonly ConfigurationStore store;
    private readonly Action<ThemeChoice> applyTheme;
    private readonly CloseProfileStore closeProfiles;
    private readonly WindowsProcessService learningProcesses;
    private AppConfiguration saved;
    private ParentViewModel? selected;
    private string notice = "";
    private bool settingsOpen;
    private bool autoStart;
    private bool minimizeToTray;
    private ThemeChoice theme;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private bool pendingSave;
    private long revision;
    private string monitorStatus = "●  Мониторинг активен";
    public MainViewModel(AppConfiguration configuration, AutomationEngine engine, ConfigurationStore store, Action<ThemeChoice> applyTheme,
        CloseProfileStore? closeProfiles = null, WindowsProcessService? learningProcesses = null)
    {
        this.engine = engine; this.store = store; this.applyTheme = applyTheme; saved = configuration;
        this.closeProfiles = closeProfiles ?? new(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "close-profiles.json"));
        this.learningProcesses = learningProcesses ?? new(this.closeProfiles);
        saveTimer.Tick += async (_, _) => await FlushChangesAsync();
        autoStart = configuration.Settings.AutoStart; minimizeToTray = configuration.Settings.MinimizeToTray; theme = configuration.Settings.Theme;
        Parents = new(configuration.Parents.Select(p => new ParentViewModel(p)));
        foreach (var parent in Parents) Watch(parent);
        selected = Parents.FirstOrDefault();
        AddParentCommand = new AsyncCommand(AddParentAsync, ShowError);
        RemoveParentCommand = new AsyncCommand(RemoveParentAsync, ShowError);
        AddLinkCommand = new AsyncCommand(AddLinkAsync, ShowError);
        RemoveLinkCommand = new RelayCommand(p => { if (p is LinkViewModel link && Selected is not null) Selected.Links.Remove(link); });
        SettingsCommand = new RelayCommand(_ => SettingsOpen = !SettingsOpen);
        LaunchProgramCommand = new AsyncCommand(LaunchProgramAsync, ShowError);
        LearnCloseCommand = new AsyncCommand(LearnCloseAsync, ShowError);
        engine.StatusChanged += OnStatus;
        engine.ActivityOccurred += activity => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var parent = Parents.FirstOrDefault(p => p.Id == activity.ParentId);
            if (parent is not null) foreach (var link in parent.Links) link.Animate(activity);
        });
    }
    public ObservableCollection<ParentViewModel> Parents { get; }
    public ParentViewModel? Selected { get => selected; set { Set(ref selected, value); Raise(nameof(HasSelection)); Raise(nameof(NoSelection)); } }
    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public string Notice { get => notice; set { Set(ref notice, value); Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public string MonitorStatus { get => monitorStatus; private set => Set(ref monitorStatus, value); }
    public bool SettingsOpen { get => settingsOpen; set => Set(ref settingsOpen, value); }
    public bool AutoStart { get => autoStart; set { if (Set(ref autoStart, value)) ScheduleSave(); } }
    public bool MinimizeToTray { get => minimizeToTray; set { if (Set(ref minimizeToTray, value)) ScheduleSave(); } }
    public ThemeChoice Theme { get => theme; set { if (Set(ref theme, value)) { applyTheme(value); ScheduleSave(); } } }
    public bool CloseToTray => MinimizeToTray;
    public ICommand AddParentCommand { get; }
    public ICommand RemoveParentCommand { get; }
    public ICommand AddLinkCommand { get; }
    public ICommand RemoveLinkCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand LaunchProgramCommand { get; }
    public ICommand LearnCloseCommand { get; }

    private async Task LearnCloseAsync(object? parameter)
    {
        if (parameter is not LinkViewModel { CanLearnClose: true } link) return;
        await using var suspension = await engine.SuspendTargetAsync(link.Executable);
        await FlushChangesAsync();
        var dialog = new CloseLearningWindow(new(link.Executable, learningProcesses, closeProfiles), link.Name)
            { Owner = Application.Current.MainWindow };
        var completed = dialog.ShowDialog() == true;
        await dialog.StopAsync();
        if (completed) Notice = dialog.ResultMessage;
    }

    private async Task LaunchProgramAsync(object? parameter)
    {
        if (parameter is not string executable) return;
        var started = await Task.Run(() => engine.LaunchManuallyAsync(executable));
        Notice = ActionTarget.Kind(executable) switch
        {
            TargetKind.Website => "Сайт открыт.",
            TargetKind.Script => started ? "Скрипт запущен." : "Скрипт уже выполняется.",
            _ => started ? "Программа запущена." : "Программа уже работает."
        };
    }

    private InstalledProgram? PickInstalledProgram(bool allowOtherTargets = false)
    {
        var dialog = new ProgramPickerWindow(allowOtherTargets, Path.Combine(Path.GetDirectoryName(store.FilePath)!, "scripts")) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }
    private async Task AddParentAsync()
    {
        var program = PickInstalledProgram();
        if (program?.Executable is not { } path) return;
        var existing = Parents.FirstOrDefault(p => string.Equals(p.Executable, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { Selected = existing; return; }
        var parent = new ParentViewModel(new() { Name = program.Name, Executable = path });
        Watch(parent); Parents.Add(parent); Selected = parent;
        ScheduleSave();
        await FlushChangesAsync();
    }
    private async Task RemoveParentAsync()
    {
        if (Selected is null) return;
        if (MessageBox.Show($"Удалить «{Selected.Name}» и все его связи?", "AppFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Parents.Remove(Selected); Selected = Parents.FirstOrDefault(); ScheduleSave();
        await FlushChangesAsync();
    }
    private Task AddLinkAsync()
    {
        if (Selected is null) return Task.CompletedTask;
        var program = PickInstalledProgram(true);
        if (program?.Executable is not { } path) return Task.CompletedTask;
        var link = new LinkViewModel(new() { Name = program.Name, Executable = path,
            Rules = program.Kind == TargetKind.Website ? [new()] : AutomationRule.CreateDefaults() }, Selected.Name, Selected.Executable);
        Selected.Links.Add(link);
        return Task.CompletedTask;
    }
    private void ScheduleSave()
    {
        pendingSave = true;
        revision++;
        saveTimer.Stop();
        saveTimer.Start();
    }
    public async Task FlushChangesAsync()
    {
        saveTimer.Stop();
        await saveGate.WaitAsync();
        try
        {
            while (pendingSave)
            {
                var savingRevision = revision;
                var settings = new AppSettings { AutoStart = AutoStart, Theme = Theme, MinimizeToTray = MinimizeToTray };
                string? validationError = null;
                AppConfiguration next;
                try
                {
                    next = saved with { Parents = Parents.Select(p => p.ToModel()).ToList(), Settings = settings };
                    next.Validate();
                }
                catch (InvalidDataException ex)
                {
                    // Keep the last valid rules while an input is empty or incomplete.
                    validationError = ex.Message;
                    next = saved with { Settings = settings };
                }
                if (settings.AutoStart != saved.Settings.AutoStart) StartupService.SetEnabled(settings.AutoStart);
                await store.SaveAsync(next);
                await engine.ConfigureAsync(next);
                saved = next;
                pendingSave = revision != savingRevision;
                Notice = validationError is null ? "" : $"Изменения событий не применены: {validationError}";
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { saveGate.Release(); }
    }
    private void Watch(ParentViewModel parent)
    {
        foreach (var link in parent.Links) WatchLink(link);
        parent.Links.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (LinkViewModel link in e.NewItems) WatchLink(link);
            ScheduleSave();
        };
    }
    private void WatchLink(LinkViewModel link)
    {
        link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LinkViewModel.Enabled) or nameof(LinkViewModel.Summary)) ScheduleSave();
        };
    }
    private void OnStatus(EngineStatus status)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MonitorStatus = status.Error is null ? "●  Мониторинг активен" : "●  Ошибка мониторинга";
            foreach (var parent in Parents)
            {
                parent.Running = status.RunningParents.Contains(parent.Id);
                foreach (var link in parent.Links)
                    link.Status = !link.Enabled ? "Отключено" : status.LinkMessages.GetValueOrDefault(link.Id, "Ожидание события");
            }
            if (status.Error is not null) Notice = status.Error;
        });
    }
    public void ShowError(Exception ex) => Notice = ex.Message;
}
