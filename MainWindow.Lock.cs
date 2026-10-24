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
    private bool _unlockInProgress;
    private long _lockGeneration;

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
        _lockGeneration++;
        var markerSaved = false;
        try
        {
            // Park native HWNDs before exposing the WPF lock surface.
            SuspendHostedWindowsForLock();
            JNestLockPersistence.SaveLocked();
            markerSaved = true;
            SetLockPersistenceWarning(false);
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
            if (!markerSaved) SetLockPersistenceWarning(true);
            else LockErrorText.Text = "锁定工作区时出现异常，当前保持锁定，请解锁后重试。";
            DiagnosticLogService.WriteActivityEvent("lock-failed-" + ex.GetType().Name);
        }
    }

    private void RestorePersistedLockIfNeeded()
    {
        if (!JNestLockPersistence.IsLocked) return;
        if (!_lockState.BeginLock()) return;
        _lockGeneration++;
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
        if (_unlockInProgress || _lockState.Current != JNestLockState.Phase.Locked) return;
        if (DateTime.UtcNow < _lockRetryAfterUtc)
        {
            LockErrorText.Text = "尝试过于频繁，请稍后再试。";
            return;
        }

        // Both Enter and the button must acquire this gate before the first await.
        _unlockInProgress = true;
        UnlockJNestButton.IsEnabled = false;
        LockPasswordInput.IsEnabled = false;
        try
        {
            await VerifyAndRestoreWorkspaceAsync();
        }
        finally
        {
            _unlockInProgress = false;
            UnlockJNestButton.IsEnabled = true;
            LockPasswordInput.IsEnabled = true;
            if (IsJNestProtected) LockPasswordInput.Focus();
        }
    }

    private async Task VerifyAndRestoreWorkspaceAsync()
    {
        var candidate = LockPasswordInput.Password;
        LockPasswordInput.Clear();
        bool authenticated;
        try
        {
            authenticated = await Task.Run(() => LockCredentialService.Verify(candidate));
        }
        catch (Exception exception)
        {
            // Never let a credential-service failure escape into the global shutdown handler.
            LockErrorText.Text = "无法验证密码，当前保持锁定，请检查本地凭据后重试。";
            DiagnosticLogService.WriteActivityEvent("unlock-verify-failed-" + exception.GetType().Name);
            return;
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
        var generation = _lockGeneration;
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
            // The menu is available while scanning. A later lock owns its own marker
            // and UI; an older restore must not clear or overwrite either of them.
            if (generation != _lockGeneration || IsJNestProtected) return;
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
            SetLockPersistenceWarning(false);
        }
        catch (Exception exception)
        {
            if (generation != _lockGeneration) return;
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

    private void SetLockPersistenceWarning(bool failed)
    {
        LockPersistenceWarning.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        RetryLockPersistenceButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RetryLockPersistence_Click(object sender, RoutedEventArgs e)
    {
        if (!IsJNestProtected) return;
        try
        {
            JNestLockPersistence.SaveLocked();
            SetLockPersistenceWarning(false);
        }
        catch (Exception exception)
        {
            SetLockPersistenceWarning(true);
            DiagnosticLogService.WriteActivityEvent("lock-persist-retry-failed-" + exception.GetType().Name);
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
