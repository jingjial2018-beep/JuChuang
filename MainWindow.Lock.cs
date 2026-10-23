using System.Windows;
using System.Windows.Input;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private readonly JNestLockState _lockState = new();
    private ClientWindowEntry? _pendingLockedToastAccount;
    private long? _pendingLockedToastProcessStart;
    private int _lockFailedAttempts;
    private DateTime _lockRetryAfterUtc;
    private bool IsJNestProtected => _lockState.IsProtected;
    private bool _lockHostSuspended;

    private void SuspendHostedWindowsForLock()
    {
        if (_lockHostSuspended) return;
        WindowHost.SuspendForModalDialog();
        _lockHostSuspended = true;
    }

    private void ResumeHostedWindowsAfterLock()
    {
        if (!_lockHostSuspended) return;
        WindowHost.ResumeAfterModalDialog();
        _lockHostSuspended = false;
    }

    private void LockJNest_Click(object sender, RoutedEventArgs e)
    {
        if (IsJNestProtected) return;

        if (!LockCredentialService.IsConfigured)
        {
            // Match Settings/About: keep the hosted WeChat/WhatsApp workspace
            // visible and use the same owner, Z-order and modal-input guard.
            // The previous SuspendForModalDialog parked native clients off
            // screen, leaving a blank workspace behind this password prompt.
            var dialog = new LockPasswordDialog();
            ShowHostedModalDialog(dialog);
            if (dialog.DialogResult != true) return;
        }

        if (!_lockState.BeginLock()) return;
        try
        {
            // Park native HWNDs before exposing the WPF lock surface.
            SuspendHostedWindowsForLock();
            JNestLockPersistence.SaveLocked();
            ShowLockScreen();
            _lockState.LockCompleted();
            foreach (var entry in Entries.ToArray())
                DismissAccountToast(entry);
        }
        catch (Exception ex)
        {
            // A failed lock must never pretend protection is active.
            _lockState.FailClosed();
            ShowLockScreen();
            DiagnosticLogService.WriteActivityEvent("lock-failed-" + ex.GetType().Name);
        }
    }

    private void RestorePersistedLockIfNeeded()
    {
        if (!JNestLockPersistence.IsLocked) return;
        if (!_lockState.BeginLock()) return;
        SuspendHostedWindowsForLock();
        ShowLockScreen();
        _lockState.LockCompleted();
    }

    private void ShowLockScreen()
    {
        // HwndHost is a native child HWND. Parking hosted clients alone does
        // not remove its airspace, so it can still cover WPF unlock controls.
        // Hide the host itself before showing the lock surface.
        WindowHost.Visibility = Visibility.Collapsed;
        LockOverlay.Visibility = Visibility.Visible;
        MainMenuButton.IsEnabled = false;
        LockPasswordInput.Clear();
        LockErrorText.Text = "";
        LockPasswordInput.Focus();
    }

    private void LockPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = TryUnlockAsync();
    }

    private async void UnlockJNest_Click(object sender, RoutedEventArgs e)
        => await TryUnlockAsync();

    private async Task TryUnlockAsync()
    {
        if (_lockState.Current != JNestLockState.Phase.Locked) return;
        if (DateTime.UtcNow < _lockRetryAfterUtc)
        {
            LockErrorText.Text = "尝试过于频繁，请稍后再试。";
            return;
        }

        var candidate = LockPasswordInput.Password;
        LockPasswordInput.Clear();
        UnlockJNestButton.IsEnabled = false;
        bool authenticated;
        try
        {
            authenticated = await Task.Run(() => LockCredentialService.Verify(candidate));
        }
        finally
        {
            UnlockJNestButton.IsEnabled = true;
        }

        if (!authenticated)
        {
            _lockFailedAttempts++;
            if (_lockFailedAttempts >= 5)
            {
                _lockRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                _lockFailedAttempts = 0;
                LockErrorText.Text = "密码错误次数过多，请 30 秒后重试。";
            }
            else
            {
                LockErrorText.Text = "密码不正确，请重试。";
            }
            LockPasswordInput.Focus();
            return;
        }

        if (!_lockState.BeginUnlock(true)) return;
        try
        {
            // The credential has been verified. Rebuild normal UI first; native
            // windows remain parked until the layout and selection are ready.
            _lockState.UnlockCompleted();
            LockOverlay.Visibility = Visibility.Collapsed;
            MainMenuButton.IsEnabled = true;
            // Restore the selected WPF workspace (including HwndHost visibility)
            // before allowing the parked native client to reappear.
            ShowSelectedEntry();
            ResumeHostedWindowsAfterLock();
            _lockFailedAttempts = 0;
            _lockRetryAfterUtc = DateTime.MinValue;

            await TryScanAndAttachAsync();
            if (_pendingLockedToastAccount is { } requested
                && Entries.Contains(requested)
                && requested.ProcessStartUtcTicks is { } expected
                && expected == _pendingLockedToastProcessStart
                && TryGetProcessStartUtcTicks(requested.ProcessId) == expected)
            {
                SelectedEntry = requested;
                AccountsList.SelectedItem = requested;
            }
            _pendingLockedToastAccount = null;
            _pendingLockedToastProcessStart = null;
            ShowSelectedEntry();
            // Only clear the persisted lock once all restoration operations
            // have completed; failed retries must remain locked on restart.
            JNestLockPersistence.SaveUnlocked();
        }
        catch (Exception exception)
        {
            // Never display the old account state if restoration throws.
            if (_lockState.Current == JNestLockState.Phase.Unlocked)
                _lockState.BeginLock();
            _lockState.FailClosed();
            SuspendHostedWindowsForLock();
            ShowLockScreen();
            LockErrorText.Text = "恢复工作区失败，请重试。";
            DiagnosticLogService.WriteActivityEvent("unlock-restore-failed-" + exception.GetType().Name);
        }
    }

    private void RememberLockedToastAccount(ClientWindowEntry? account)
    {
        if (account is null || account.ProcessStartUtcTicks is not { } start)
        {
            _pendingLockedToastAccount = null;
            _pendingLockedToastProcessStart = null;
            return;
        }
        _pendingLockedToastAccount = account;
        _pendingLockedToastProcessStart = start;
    }
}
