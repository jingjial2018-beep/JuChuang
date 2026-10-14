using System.Reflection;
using System.Windows;

namespace JuChuang;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = "版本 " + (version?.Split('+')[0] ?? "未知");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
