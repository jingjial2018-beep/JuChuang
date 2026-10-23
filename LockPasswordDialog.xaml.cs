using System.Windows;
using System.Windows.Interop;
using JuChuang.Services;

namespace JuChuang;

public partial class LockPasswordDialog : Window
{
    private readonly bool _changeExisting;

    public LockPasswordDialog(bool changeExisting = false)
    {
        InitializeComponent();
        _changeExisting = changeExisting;
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(BlockWindowMove);
        };
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

    private static IntPtr BlockWindowMove(IntPtr hwnd, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        const int wmNcLButtonDown = 0x00A1;
        const int wmNcLButtonDblClk = 0x00A3;
        const int wmSysCommand = 0x0112;
        const int htCaption = 2;
        const int scMove = 0xF010;
        if (((message == wmNcLButtonDown || message == wmNcLButtonDblClk)
                && wParam.ToInt64() == htCaption)
            || (message == wmSysCommand && (wParam.ToInt64() & 0xFFF0) == scMove))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var first = NewPasswordBox.Password;
        var second = ConfirmPasswordBox.Password;
        if (first.Length < 6)
        {
            ErrorText.Text = "密码至少需要 6 位。";
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
