using System.Windows;
using JuChuang.Services;

namespace JuChuang;

public partial class LockPasswordDialog : Window
{
    private readonly bool _changeExisting;

    public LockPasswordDialog(bool changeExisting = false)
    {
        InitializeComponent();
        _changeExisting = changeExisting;
        if (changeExisting)
        {
            Heading.Text = "修改 JNest 锁定密码";
            Title = "修改 JNest 锁定密码";
            Height = 380;
            OldPasswordPanel.Visibility = Visibility.Visible;
            Loaded += (_, _) => OldPasswordBox.Focus();
        }
        else
        {
            Loaded += (_, _) => NewPasswordBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var first = NewPasswordBox.Password;
        var second = ConfirmPasswordBox.Password;
        if (first.Length < 8)
        {
            ErrorText.Text = "密码至少需要 8 个字符。";
            return;
        }

        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的密码不一致。";
            ConfirmPasswordBox.Clear();
            return;
        }

        try
        {
            if (_changeExisting)
                LockCredentialService.ChangePassword(OldPasswordBox.Password, first);
            else
                LockCredentialService.SetPassword(first);
            OldPasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            DialogResult = true;
        }
        catch (UnauthorizedAccessException)
        {
            ErrorText.Text = "当前密码不正确。";
            OldPasswordBox.Clear();
        }
        catch (Exception)
        {
            ErrorText.Text = "密码保存失败，请检查当前 Windows 用户目录是否可写。";
        }
    }
}
