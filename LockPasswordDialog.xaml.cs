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
        FixedModalWindow.Attach(this);
        if (changeExisting)
        {
            Heading.Text = "修改 JNest 锁定密码";
            Title = "修改 JNest 锁定密码";
            OldPasswordPanel.Visibility = Visibility.Visible;
            UpdateRemovalPlaceholders();
            Loaded += (_, _) => OldPasswordBox.Focus();
        }
        else
        {
            Loaded += (_, _) => NewPasswordBox.Focus();
        }
    }

    private void PasswordField_Changed(object sender, RoutedEventArgs e)
        => UpdateRemovalPlaceholders();

    private void UpdateRemovalPlaceholders()
    {
        if (NewPasswordPlaceholder is null || ConfirmPasswordPlaceholder is null)
            return;
        NewPasswordPlaceholder.Visibility =
            _changeExisting && NewPasswordBox.Password.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPasswordPlaceholder.Visibility =
            _changeExisting && ConfirmPasswordBox.Password.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var first = NewPasswordBox.Password;
        var second = ConfirmPasswordBox.Password;

        // Both empty fields mean removal only in change-password mode.
        if (LockCredentialService.IsRemovalRequest(_changeExisting, first, second))
        {
            try
            {
                if (!LockCredentialService.Verify(OldPasswordBox.Password))
                {
                    ErrorText.Text = "当前密码不正确。";
                    OldPasswordBox.Clear();
                    OldPasswordBox.Focus();
                    return;
                }

                var confirm = new LockRemovalConfirmDialog { Owner = this };
                if (confirm.ShowDialog() != true)
                {
                    OldPasswordBox.Focus();
                    return;
                }

                // Check the password again at the actual point of deletion.
                LockCredentialService.RemovePassword(OldPasswordBox.Password);
                OldPasswordBox.Clear();
                NewPasswordBox.Clear();
                ConfirmPasswordBox.Clear();
                DialogResult = true;
            }
            catch (UnauthorizedAccessException)
            {
                ErrorText.Text = "当前密码不正确。";
                OldPasswordBox.Clear();
                OldPasswordBox.Focus();
            }
            catch (Exception)
            {
                ErrorText.Text = "取消密码失败，请检查权限后重试。";
            }
            return;
        }

        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的密码不一致。";
            ConfirmPasswordBox.Clear();
            ConfirmPasswordBox.Focus();
            return;
        }
        if (first.Length < 6)
        {
            ErrorText.Text = "密码至少需要 6 位。";
            NewPasswordBox.Focus();
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
            OldPasswordBox.Focus();
        }
        catch (Exception)
        {
            ErrorText.Text = "密码保存失败，请检查当前 Windows 用户目录是否可写。";
        }
    }
}
