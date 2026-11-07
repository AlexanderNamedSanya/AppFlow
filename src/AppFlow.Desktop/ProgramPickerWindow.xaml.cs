using System.Windows;
using System.Windows.Input;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using Microsoft.Win32;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace AppFlow.Desktop;

public partial class ProgramPickerWindow : FluentWindow
{
    private readonly ProgramPickerViewModel viewModel;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string scriptsDirectory;
    public InstalledProgram? Selection { get; private set; }
    public ProgramPickerWindow(bool allowOtherTargets = false, string? scriptsDirectory = null)
    {
        this.scriptsDirectory = scriptsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppFlow", "scripts");
        viewModel = new(allowOtherTargets);
        InitializeComponent();
        if (!Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Mica) || SystemParameters.HighContrast)
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        DataContext = viewModel;
        Loaded += async (_, _) => { SearchBox.Focus(); await viewModel.LoadAsync(lifetime.Token); };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
    public static InstalledProgram? Browse(Window? owner, InstalledProgram? program = null)
    {
        var dialog = new OpenFileDialog { Title = "Выберите EXE самой программы, а не её лаунчера", Filter = "Приложение Windows (*.exe)|*.exe", CheckFileExists = true };
        if (!string.IsNullOrWhiteSpace(program?.InstallDirectory) && Directory.Exists(program.InstallDirectory)) dialog.InitialDirectory = program.InstallDirectory;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return null;
        var path = Path.GetFullPath(dialog.FileName);
        return new(program?.Name ?? AppIcon.DisplayName(path), path);
    }
    private void OnBrowse(object sender, RoutedEventArgs e) => Complete(Browse(this));
    private void OnCreateScript(object sender, RoutedEventArgs e)
    {
        var dialog = new ScriptEditorWindow(scriptsDirectory) { Owner = this };
        if (dialog.ShowDialog() == true) Complete(dialog.Selection);
    }
    private void OnSelect(object sender, RoutedEventArgs e) => SelectCurrent();
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement((System.Windows.Controls.ListBox)sender, source) is not null)
            SelectCurrent();
    }
    private void SelectCurrent()
    {
        if (viewModel.Selected is not { } selected) return;
        Complete(selected.Executable is null ? Browse(this, selected) : selected);
    }
    private void Complete(InstalledProgram? program)
    {
        if (program is null) return;
        if (program.Kind != AppFlow.Core.TargetKind.Website && !File.Exists(program.Executable))
        {
            MessageBox.Show(this, "Файл больше недоступен. Укажите его через «Обзор».", "AppFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Selection = program;
        DialogResult = true;
    }
}
