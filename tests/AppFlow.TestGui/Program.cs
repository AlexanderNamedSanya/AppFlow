using System;
using System.IO;
using System.Windows.Automation;
using System.Windows.Threading;
using Wpf = System.Windows;
using Forms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is not ["--window", var marker, var framework]) return;
        var visible = framework.EndsWith("-visible", StringComparison.Ordinal);
        if (framework.StartsWith("wpf", StringComparison.Ordinal))
        {
            var app = new Wpf.Application();
            var window = new Wpf.Window { Title = "AppFlow WPF exit test", Width = 300, Height = 180, Left = visible ? 180 : -32000, Top = visible ? 180 : -32000, ShowInTaskbar = false };
            var button = new System.Windows.Controls.Button { Content = "Exit test app" };
            AutomationProperties.SetAutomationId(button, "AppFlowTestExit");
            window.Content = button;
            var quitting = false; var minimized = false;
            window.Closing += (_, e) => { if (!quitting) { e.Cancel = true; window.Hide(); } };
            window.StateChanged += (_, _) =>
            {
                if (minimized && window.WindowState == Wpf.WindowState.Normal) File.WriteAllText(marker + ".restored", "restored");
                minimized |= window.WindowState == Wpf.WindowState.Minimized;
            };
            button.Click += (_, _) =>
            {
                File.WriteAllText(marker + ".state-on-exit", window.WindowState.ToString());
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += (_, _) => { timer.Stop(); quitting = true; File.WriteAllText(marker, "saved before normal exit"); app.Shutdown(); };
                timer.Start();
            };
            window.Loaded += (_, _) =>
            {
                var source = (System.Windows.Interop.HwndSource)Wpf.PresentationSource.FromVisual(window);
                source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (message == 0x8014) { quitting = true; handled = true; app.Shutdown(); }
                    return IntPtr.Zero;
                });
                File.WriteAllText(marker + ".ready", "ready");
            };
            app.Run(window);
        }
        else
        {
            Forms.Application.EnableVisualStyles();
            using var window = new TestForm { Text = "AppFlow WinForms exit test", Width = 300, Height = 180,
                StartPosition = Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(visible ? 180 : -32000, visible ? 180 : -32000), ShowInTaskbar = false };
            var button = new Forms.Button { Text = "Exit test app", Name = "AppFlowTestExit", AccessibleName = "Exit test app", Dock = Forms.DockStyle.Fill };
            window.Controls.Add(button);
            var quitting = false; var minimized = false;
            window.FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; window.Hide(); } };
            window.Resize += (_, _) =>
            {
                if (minimized && window.WindowState == Forms.FormWindowState.Normal) File.WriteAllText(marker + ".restored", "restored");
                minimized |= window.WindowState == Forms.FormWindowState.Minimized;
            };
            using var timer = new Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) => { timer.Stop(); quitting = true; File.WriteAllText(marker, "saved before normal exit"); Forms.Application.Exit(); };
            void Exit() { File.WriteAllText(marker + ".state-on-exit", window.WindowState.ToString()); File.WriteAllText(marker + ".visible-on-exit", window.Visible.ToString()); timer.Start(); }
            button.Click += (_, _) => Exit();
            using var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Exit test app", null, (_, _) => Exit());
            using var tray = new Forms.NotifyIcon();
            if (framework == "winforms-tray")
            {
                tray.Text = "AppFlow WinForms close learning test"; tray.Icon = System.Drawing.SystemIcons.Application;
                tray.ContextMenuStrip = menu; tray.Visible = true; File.WriteAllText(marker + ".tray-tip", tray.Text);
            }
            window.Cleanup = () => { quitting = true; Forms.Application.Exit(); };
            window.Shown += (_, _) => { if (framework == "winforms-tray") window.Hide(); File.WriteAllText(marker + ".ready", "ready"); };
            Forms.Application.Run(window);
        }
    }
    private sealed class TestForm : Forms.Form
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Action? Cleanup { get; set; }
        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x8014) { Cleanup?.Invoke(); return; }
            base.WndProc(ref message);
        }
    }
}
