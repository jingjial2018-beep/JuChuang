using System.Windows;

namespace JuChuang;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion?.Split('+')[0];
        VersionText.Text = $"版本 {version ?? "未知"}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
