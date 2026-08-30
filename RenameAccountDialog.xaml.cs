using System.Windows;

namespace JuChuang;

public partial class RenameAccountDialog : Window
{
    public RenameAccountDialog(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string AccountName => NameTextBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            MessageBox.Show(this, "账号名称不能为空。", "聚窗", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
