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
    private int _shellWindowReconcilePending;

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
        if (_isClosing
            || _shellHookMessage == 0
            || message != _shellHookMessage)
        {
            return IntPtr.Zero;
        }

        var shellEvent = wParam.ToInt32();
        if (shellEvent == NativeMethods.HSHELL_FLASH)
        {
            var flashingHandle = lParam;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => HandleClientAttentionRequest(flashingHandle)));
            handled = true;
        }
        else if (shellEvent is NativeMethods.HSHELL_WINDOWCREATED
                 or NativeMethods.HSHELL_WINDOWDESTROYED)
        {
            QueueClientWindowReconcile(
                lParam,
                shellEvent == NativeMethods.HSHELL_WINDOWDESTROYED);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Shell 的窗口创建/销毁事件作为快速触发源；5 秒扫描仍保留为最终兜底。
    /// 只响应已知账号窗口或微信/WhatsApp 进程，避免系统其它窗口频繁触发全量扫描。
    /// </summary>
    private void QueueClientWindowReconcile(IntPtr changedHandle, bool destroyed)
    {
        if (_isClosing || changedHandle == IntPtr.Zero)
        {
            return;
        }

        var relevant = Entries.Any(entry => entry.Handle == changedHandle);
        if (!relevant && !destroyed)
        {
            NativeMethods.GetWindowThreadProcessId(changedHandle, out var rawProcessId);
            var processId = unchecked((int)rawProcessId);
            relevant = processId > 0
                && ClientWindowDiscovery.IsPotentialClientProcess(processId);
        }

        if (!relevant
            || Interlocked.CompareExchange(ref _shellWindowReconcilePending, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () =>
            {
                try
                {
                    // 窗口刚创建时标题/尺寸/可见状态可能尚未稳定；先给客户端一点时间，
                    // 再补一次较晚的校准，覆盖 Login -> Main 连续重建的情况。
                    await Task.Delay(180);
                    await ScanAndAttachAsync();
                    if (!_isClosing)
                    {
                        await Task.Delay(500);
                        await ScanAndAttachAsync();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _shellWindowReconcilePending, 0);
                }
            }));
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
        // 先显示红点，随后由角标识别用真实数字替换，避免每次闪烁都错误 +1。
        entry.MessageAlertCount = 0;
        entry.AlertDisplayMode = AlertDisplayMode.Dot;
        NotifyAccountAttention(entry);
    }

    /// <summary>
    /// 把“某个账号请求注意”统一映射到聚窗自身的提醒。
    /// 不读取消息正文；任务栏闪烁与账号红点/数字是两条独立显示通道。
    /// </summary>
    private void NotifyAccountAttention(ClientWindowEntry entry)
    {
        if (_isClosing || ReferenceEquals(entry, SelectedEntry))
        {
            return;
        }

        StatusMessageText.Text = $"{entry.DisplayName} 收到新消息。";
        SystemSounds.Exclamation.Play();

        var managerHandle = new WindowInteropHelper(this).Handle;
        if (managerHandle == IntPtr.Zero)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            // 聚窗最小化后仍保留任务栏按钮，因此收到消息时不恢复窗口、不抢前台，
            // 只触发任务栏闪烁，并继续使用托盘气泡作为补充提示。
            if (!IsVisible)
            {
                // 兼容从旧版本状态或其它路径被显式 Hide 的情况。
                Show();
            }

            _notifyIcon?.ShowBalloonTip(
                3000,
                "聚窗",
                $"{entry.DisplayName} 收到新消息。",
                WinForms.ToolTipIcon.Info);
        }

        var flashInfo = new NativeMethods.FLASHWINFO
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            Window = managerHandle,
            Flags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
            Count = uint.MaxValue,
            Timeout = 0
        };
        NativeMethods.FlashWindowEx(ref flashInfo);
    }

}
