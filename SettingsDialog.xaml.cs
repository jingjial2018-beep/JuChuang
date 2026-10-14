using System.Windows;
using JuChuang.Services;

namespace JuChuang;

public partial class SettingsDialog : Window
{
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
        _loading = false;
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.SetEnabled(StartupCheck.IsChecked == true);
        var actual = StartupService.IsEnabled();
        _settings.LaunchAtStartup = actual;
        if (StartupCheck.IsChecked != actual) StartupCheck.IsChecked = actual;
        _settings.Save();
        _startupSettingsChanged();
    }

    private void NotificationChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.EnableTaskbarFlash = FlashCheck.IsChecked == true;
        _settings.EnableToastNotifications = ToastCheck.IsChecked == true;
        _settings.EnableNotificationSound = SoundCheck.IsChecked == true;
        _settings.Save();
        _notificationSettingsChanged();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}