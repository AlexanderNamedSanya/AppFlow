using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AppFlow.Desktop.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace AppFlow.Desktop;

public partial class MainWindow : FluentWindow
{
    public static string ApplicationVersion
    {
        get
        {
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            return $"{version.Major:00}.{version.Minor:00}.{version.Build}";
        }
    }
    private readonly MainViewModel viewModel;
    public bool AllowClose { get; set; }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        if (!Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Mica) || SystemParameters.HighContrast)
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        this.viewModel = viewModel;
        DataContext = viewModel;
    }
    private void EventRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != sender;
             source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source))
        {
            if (source is ButtonBase or ComboBox) return;
        }
        ToggleEventOptions(sender);
        e.Handled = true;
    }

    private void EventRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource != sender || e.Key is not (Key.Enter or Key.Space)) return;
        ToggleEventOptions(sender);
        e.Handled = true;
    }

    private static void ToggleEventOptions(object sender)
    {
        if (sender is FrameworkElement { DataContext: EventViewModel row })
            row.ToggleOptionsCommand.Execute(null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (viewModel.CloseToTray) Hide();
            else
                _ = ((App)Application.Current).ExitAsync();
        }
        base.OnClosing(e);
    }
}
