using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JuChuang.Models;
using JuChuang.Services;
using WinForms = System.Windows.Forms;

namespace JuChuang;

public partial class MainWindow
{
    private HwndSource? _windowSource;
    private uint _shellHookMessage;
    private bool _shellHookRegistered;

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        if (_windowSource is not null)
        {
            // 系统 shell 通知（HSHELL_*）通过 RegisterShellHookWindow 发送时，使用的
            // 消息号由 RegisterWindowMessage("SHELLHOOK") 决定。必须用这个固定名称，
            // 否则永远收不到客户端窗口的闪烁通知（HSHELL_FLASH）。
            _shellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
            _shellHookRegistered = _shellHookMessage != 0
                && NativeMethods.RegisterShellHookWindow(handle);
            _windowSource.AddHook(WindowMessageHook);
        }

        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            handle,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (!_isClosing
            && _shellHookMessage != 0
            && message == _shellHookMessage
            && wParam.ToInt32() == NativeMethods.HSHELL_FLASH)
        {
            var flashingHandle = lParam;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => HandleClientAttentionRequest(flashingHandle)));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void HandleClientAttentionRequest(IntPtr flashingHandle)
    {
        if (_isClosing || flashingHandle == IntPtr.Zero)
        {
            return;
        }

        var entry = Entries.FirstOrDefault(candidate => candidate.Handle == flashingHandle);
        if (entry is null)
        {
            NativeMethods.GetWindowThreadProcessId(flashingHandle, out var rawProcessId);
            var processId = unchecked((int)rawProcessId);
            entry = Entries.FirstOrDefault(candidate => candidate.ProcessId == processId);
        }

        if (entry is null)
        {
            return;
        }

        // 无论账号是否处于选中状态，都先读取客户端真实角标。
        TriggerImmediateBadgeScan(entry);

        // 当前正在查看的账号不播放声音或闪烁任务栏，但数字仍会同步。
        if (ReferenceEquals(entry, SelectedEntry))
        {
            return;
        }

        // Shell 只告诉我们“这个窗口请求关注”，并不携带真实未读数。
        // 先显示红点，随后由角标 OCR 用真实数字替换，避免每次闪烁都错误 +1。
        entry.MessageAlertCount = 0;
        entry.AlertDisplayMode = AlertDisplayMode.Dot;
        StatusMessageText.Text = $"{entry.DisplayName} 收到新消息。";
        SystemSounds.Exclamation.Play();

        // HSHELL_FLASH 只是一个"可能有消息"的信号（微信 4.x 实际很少触发），
        // 真正的未读数需要靠截图识别来确认。立即对这条账号做一次截图识别，
        // 用真实数字覆盖临时红点。
        var managerHandle = new WindowInteropHelper(this).Handle;
        if (managerHandle == IntPtr.Zero)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            _notifyIcon?.ShowBalloonTip(
                3000,
                "聚窗",
                $"{entry.DisplayName} 收到新消息。",
                WinForms.ToolTipIcon.Info);
            return;
        }

        var flashInfo = new NativeMethods.FLASHWINFO
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            Window = managerHandle,
            Flags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
            Count = uint.MaxValue,
            Timeout = 0
        };
        NativeMethods.FlashWindowEx(ref flashInfo);
    }
}
