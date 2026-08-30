using System.IO;
using System.Windows;
using JuChuang.Services;
using WinForms = System.Windows.Forms;

namespace JuChuang;

public partial class MainWindow
{
    private WinForms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;
                _trayIcon = new System.Drawing.Icon(buffer);
            }
        }
        catch
        {
            _trayIcon = null;
        }

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "聚窗 - 一窗聚合多媒",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());

        var startupEnabled = StartupService.IsEnabled();
        _settings.LaunchAtStartup = startupEnabled;
        var startupItem = new WinForms.ToolStripMenuItem("开机自启")
        {
            Checked = startupEnabled
        };
        startupItem.Click += (_, _) =>
        {
            var shouldEnable = !startupItem.Checked;
            StartupService.SetEnabled(shouldEnable);
            _settings.LaunchAtStartup = shouldEnable;
            _settings.Save();
            startupItem.Checked = shouldEnable;
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }
}
