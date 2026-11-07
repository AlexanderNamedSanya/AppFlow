using System.Windows;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace AppFlow.Desktop;

public partial class ScriptEditorWindow : FluentWindow
{
    private readonly ScriptEditorViewModel viewModel = new();
    private readonly string scriptsDirectory;
    public InstalledProgram? Selection { get; private set; }
    public ScriptEditorWindow(string scriptsDirectory)
    {
        this.scriptsDirectory = scriptsDirectory;
        InitializeComponent();
        if (!Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Mica) || SystemParameters.HighContrast)
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        DataContext = viewModel;
        Loaded += (_, _) => NameBox.Focus();
        Closing += (_, e) => { if (viewModel.Busy) e.Cancel = true; };
    }
    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        try
        {
            Selection = await viewModel.CreateAsync(scriptsDirectory);
            DialogResult = true;
        }
        catch (Exception ex) { viewModel.Error = ex.Message; }
    }
}
