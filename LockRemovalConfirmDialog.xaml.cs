using System.Windows;

namespace JuChuang;

public partial class LockRemovalConfirmDialog : Window
{
    public LockRemovalConfirmDialog()
    {
        InitializeComponent();
        FixedModalWindow.Attach(this);
    }

    private void KeepPassword_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void RemovePassword_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
