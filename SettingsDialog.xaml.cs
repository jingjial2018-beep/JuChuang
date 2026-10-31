using System.Windows;
using System.Windows.Controls;
using JuChuang.Services;

namespace JuChuang;

public partial class SettingsDialog : Window
{
    private sealed record TimeOption(int Minutes, string Label);

    private static readonly TimeOption[] DndTimeOptions = Enumerable.Range(0, 48)
        .Select(index => new TimeOption(index * 30, $"{index / 2:00}:{index % 2 * 30:00}"))
        .ToArray();

    private readonly AppSettings _settings;
    private readonly Action _notificationSettingsChanged;
    private readonly Action _startupSettingsChanged;
    private bool _loading = true;

    public SettingsDialog(AppSettings settings, Action notificationSettingsChanged, Action startupSettingsChanged)
    {
        InitializeComponent();
        _settings = settings;
        _notificationSettingsChanged = notificationSettingsChanged;
        _startupSettingsChanged = startupSettingsChanged;
        StartupCheck.IsChecked = StartupService.IsEnabled();
        FlashCheck.IsChecked = settings.EnableTaskbarFlash;
        ToastCheck.IsChecked = settings.EnableToastNotifications;
        SoundCheck.IsChecked = settings.EnableNotificationSound;
        DndStartTime.ItemsSource = DndTimeOptions;
        DndEndTime.ItemsSource = DndTimeOptions;
        DndStartTime.SelectedValue = settings.DndStartMinutes;
        DndEndTime.SelectedValue = settings.DndEndMinutes;
        DndCheck.IsChecked = settings.DndEnabled;
        _loading = false;
        UpdateDndAllDayHint();
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.SetEnabled(StartupCheck.IsChecked == true);
        var actual = StartupService.IsEnabled();
        _settings.LaunchAtStartup = actual;
        if (StartupCheck.IsChecked != actual) StartupCheck.IsChecked = actual;
        SaveSettings();
        _startupSettingsChanged();
    }

    private void NotificationChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.EnableTaskbarFlash = FlashCheck.IsChecked == true;
        _settings.EnableToastNotifications = ToastCheck.IsChecked == true;
        _settings.EnableNotificationSound = SoundCheck.IsChecked == true;
        SaveSettings();
        _notificationSettingsChanged();
    }

    private void DndChanged(object sender, RoutedEventArgs e) => SaveDoNotDisturb();

    private void DndTimeChanged(object sender, SelectionChangedEventArgs e) => SaveDoNotDisturb();

    private void SaveDoNotDisturb()
    {
        if (_loading) return;
        UpdateDndAllDayHint();
        _settings.DndEnabled = DndCheck.IsChecked == true;
        if (DndStartTime.SelectedValue is int start) _settings.DndStartMinutes = start;
        if (DndEndTime.SelectedValue is int end) _settings.DndEndMinutes = end;
        SaveSettings();
        _notificationSettingsChanged();
    }

    private void SaveSettings()
    {
        var saved = _settings.Save();
        RetrySaveButton.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        SaveStatusText.Text = saved ? "设置即时生效并自动保存" : "保存失败，本次修改尚未保存；请检查磁盘和权限后重试";
        SaveStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            saved ? System.Windows.Media.Color.FromRgb(0x66, 0x70, 0x85)
                  : System.Windows.Media.Color.FromRgb(0xC1, 0x3C, 0x3C));
    }

    private void RetrySave_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateDndAllDayHint()
    {
        var allDay = DndCheck.IsChecked == true
            && DndStartTime.SelectedValue is int start
            && DndEndTime.SelectedValue is int end
            && start == end;
        DndAllDayHint.Visibility = allDay ? Visibility.Visible : Visibility.Collapsed;
    }
}
