using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JuChuang.Services;
using WinForms = System.Windows.Forms;

namespace JuChuang;

public partial class MainWindow
{
    private WinForms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private ContextMenu? _trayMenu;
    private MenuItem? _startupMenuItem;
    private TextBlock? _startupCheckmark;
    private System.Drawing.Point _trayMenuOpenClickPoint;
    private bool _trayExitRequested;

    private void RefreshStartupMenuState()
    {
        if (_startupMenuItem is not null)
            { var enabled = StartupService.IsEnabled(); _startupMenuItem.IsChecked = enabled; if (_startupCheckmark is not null) _startupCheckmark.Visibility = enabled ? Visibility.Visible : Visibility.Hidden; }
    }

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
            Text = "JNest",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            else if (args.Button == WinForms.MouseButtons.Right)
                Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
        };

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 249, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 222, 229)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            HasDropShadow = true,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = false
        };
        var template = new ControlTemplate(typeof(ContextMenu));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ContextMenu.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(ContextMenu.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(ContextMenu.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        border.SetValue(Border.PaddingProperty, new Thickness(6));
        var host = new FrameworkElementFactory(typeof(StackPanel));
        host.SetValue(StackPanel.IsItemsHostProperty, true);
        border.AppendChild(host);
        template.VisualTree = border;
        menu.Template = template;
        menu.Opened += (_, _) => BeginTrayMenuDismissTracking();
        menu.Closed += (_, _) => EndTrayMenuDismissTracking();

        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.5));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(38, 48, 65))));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 39.0));
        var itemTemplate = new ControlTemplate(typeof(MenuItem));
        var itemBorder = new FrameworkElementFactory(typeof(Border));
        itemBorder.Name = "MenuRow";
        itemBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        itemBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
        var itemContent = new FrameworkElementFactory(typeof(ContentPresenter));
        itemContent.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        itemContent.SetValue(FrameworkElement.MarginProperty, new Thickness(13, 0, 13, 0));
        itemContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemBorder.AppendChild(itemContent);
        itemTemplate.VisualTree = itemBorder;
        var highlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlight.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(232, 237, 243)), "MenuRow"));
        itemTemplate.Triggers.Add(highlight);
        style.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        menu.Resources.Add(typeof(MenuItem), style);

        var show = new MenuItem { Header = "显示主窗口" };
        show.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(show);

        var startupHeader = new Grid { Width = 110 };
        var startupText = new TextBlock { Text = "开机自启", VerticalAlignment = VerticalAlignment.Center };
        var checkmark = new TextBlock { Text = "√", Margin = new Thickness(18, 0, 0, 0),
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden };
        var startupLayout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(checkmark, Dock.Right);
        startupLayout.Children.Add(checkmark);
        startupLayout.Children.Add(startupText);
        startupHeader.Children.Add(startupLayout);
        _startupCheckmark = checkmark;
        var startup = new MenuItem { Header = startupHeader, IsCheckable = true,
            IsChecked = StartupService.IsEnabled() };
        startup.Click += (_, _) =>
        {
            StartupService.SetEnabled(startup.IsChecked);
            _settings.LaunchAtStartup = StartupService.IsEnabled();
            TrySaveSettings();
            startup.IsChecked = _settings.LaunchAtStartup;
            checkmark.Visibility = startup.IsChecked ? Visibility.Visible : Visibility.Hidden;
        };
        _startupMenuItem = startup;
        menu.Items.Add(startup);

        var separator = new Separator { Margin = new Thickness(12, 6, 12, 6),
            Height = 1, Background = new SolidColorBrush(Color.FromRgb(221, 226, 233)) };
        menu.Items.Add(separator);
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitFromTray();
        menu.Items.Add(exit);
        _trayMenu = menu;
        RefreshStartupMenuState();
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is null || _trayMenu.IsOpen) return;
        RefreshStartupMenuState();
        // Anchor to the tray click, including overflow flyouts and top taskbars.
        // Use monitor bounds (not WorkingArea, which excludes a top taskbar).
        var cursor = WinForms.Cursor.Position;
        var monitor = WinForms.Screen.FromPoint(cursor);
        var source = PresentationSource.FromVisual(this);
        var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        _trayMenu.ApplyTemplate();
        _trayMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var widthPx = Math.Ceiling(_trayMenu.DesiredSize.Width * dpiX);
        var heightPx = Math.Ceiling(_trayMenu.DesiredSize.Height * dpiY);
        var area = monitor.Bounds;
        const double gap = 6;
        var leftPx = cursor.X + gap * dpiX;
        var topPx = cursor.Y + gap * dpiY;
        if (leftPx + widthPx > area.Right)
            leftPx = cursor.X - widthPx - gap * dpiX;
        if (topPx + heightPx > area.Bottom)
            topPx = cursor.Y - heightPx - gap * dpiY;
        leftPx = Math.Max(area.Left, Math.Min(leftPx, area.Right - widthPx));
        topPx = Math.Max(area.Top, Math.Min(topPx, area.Bottom - heightPx));
        _trayMenu.HorizontalOffset = leftPx / dpiX;
        _trayMenu.VerticalOffset = topPx / dpiY;
        _trayMenuOpenClickPoint = cursor;
        _trayMenu.IsOpen = true;
    }

    private void ExitFromTray()
    {
        if (_isClosing || _allExitInProgress || _clientExitInProgress) return;
        if (_trayMenu is not null) _trayMenu.IsOpen = false;

        // Bring the locked shell back before entering Window.Closing. WPF
        // forbids calling Show() once the window has begun closing.
        if (IsJNestProtected) RestoreFromTray();
        _trayExitRequested = true;
        try
        {
            Close();
        }
        finally
        {
            // Closing can be cancelled by the exit-choice dialog. Keep this
            // flag during an asynchronous "exit all" until the final Close.
            if (!_isClosing && !_allExitInProgress)
                _trayExitRequested = false;
        }
    }

    private void RestoreFromTray()
    {
        if (_isClosing || _trayExitRequested
            || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (_trayMenu is not null) _trayMenu.IsOpen = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        WindowHost.HandleOwnerWindowStateChanged(false);
        UpdateLocalClock();
        _clockTimer.Start();
        if (IsJNestProtected)
        {
            Activate();
            LockPasswordInput.Focus();
            return;
        }
        Activate();
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                WindowHost.UpdateOverlayBounds();
                WindowHost.ActivateCurrentWindow();
            }));
    }
}
