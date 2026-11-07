using System.Windows;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace AppFlow.Desktop;

public partial class CloseLearningWindow : FluentWindow
{
    private readonly CloseLearningViewModel viewModel;
    internal string ResultMessage => viewModel.ResultMessage;
    internal Task StopAsync() => viewModel.StopAsync();
    internal CloseLearningWindow(CloseLearningService service, string name)
    {
        InitializeComponent();
        if (!Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Mica) || SystemParameters.HighContrast)
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        viewModel = new(new CloseLearningSession(service), name);
        DataContext = viewModel;
        viewModel.Completed += () => DialogResult = true;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
