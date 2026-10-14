using System.Media;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
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
    private NativeMethods.WinEventDelegate? _clientActivityCallback;
    private readonly List<IntPtr> _clientActivityHooks = [];
    private readonly Dictionary<ClientWindowEntry, DateTime> _lastClientActivityScan = [];
    private readonly Dictionary<ClientWindowEntry, int> _activityRetryGeneration = [];
    private static readonly TimeSpan ClientActivityDebounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AccountToastCooldown = TimeSpan.FromSeconds(10);
    private const string AccountToastGroup = "jnest-account-messages";
    private readonly Dictionary<ClientWindowEntry, DateTime> _lastAccountToast = [];

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        EnsureMainWindowTaskbarButton();
        ApplyJNestTaskbarIcon();
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
            if (!_shellHookRegistered)
            {
                DiagnosticLogService.WriteActivityEvent("shell-hook-unavailable");
            }
        }

        StartClientActivityMonitoring();

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

    private bool IsEntryActivelyViewed(ClientWindowEntry entry)
        => ReferenceEquals(entry, SelectedEntry)
           && WindowState != WindowState.Minimized
           && IsActive
           && WindowHost.IsManagerContextForeground;

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
            entry = Entries.FirstOrDefault(candidate => candidate.ProcessId == processId
                && candidate.Kind == ClientKind.WeChat);
        }

        if (entry is null)
        {
            return;
        }

        QueueClientActivityScan(entry, "shell-flash");
    }

    private void StartClientActivityMonitoring()
    {
        // WinEvent out-of-context 回调只观察客户端窗口，不装载代码到微信。
        // Windows 不公开跨进程的 Shell_NotifyIcon(NIM_MODIFY) 订阅，也不提供
        // 能可靠反查每个托盘图标所属微信 PID 的通知。这里监测可归属的窗口
        // 名称/状态变化；托盘活动不可见时由 HSHELL_FLASH 和前台校准兜底。
        _clientActivityCallback = OnClientWindowEvent;
        foreach (var eventId in new[]
                 {
                     NativeMethods.EVENT_OBJECT_NAMECHANGE,
                     NativeMethods.EVENT_OBJECT_STATECHANGE
                 })
        {
            var hook = NativeMethods.SetWinEventHook(
                eventId, eventId, IntPtr.Zero, _clientActivityCallback,
                0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero)
            {
                _clientActivityHooks.Add(hook);
            }
        }

        DiagnosticLogService.WriteActivityEvent(
            _clientActivityHooks.Count == 2
                ? "external-window-events-enabled;tray-modify-unavailable"
                : "external-window-events-degraded;tray-modify-unavailable");
    }

    private void StopClientActivityMonitoring()
    {
        foreach (var hook in _clientActivityHooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
        _clientActivityHooks.Clear();
        _clientActivityCallback = null;
        _lastClientActivityScan.Clear();
        _activityRetryGeneration.Clear();
        _lastAccountToast.Clear();
    }

    private void OnClientWindowEvent(
        IntPtr hook, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_isClosing || window == IntPtr.Zero
            || objectId != NativeMethods.OBJID_WINDOW
            || childId != NativeMethods.CHILDID_SELF)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var rawProcessId);
        var processId = unchecked((int)rawProcessId);
        if (processId <= 0 || !Entries.Any(candidate =>
                candidate.Kind == ClientKind.WeChat && candidate.ProcessId == processId))
        {
            return;
        }

        // 回调来自当前 UI 线程的消息循环；BeginInvoke 避免在 WinEvent 中截图。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_isClosing || !NativeMethods.IsWindow(window)) return;
            var entry = Entries.FirstOrDefault(candidate =>
                candidate.Kind == ClientKind.WeChat
                && candidate.ProcessId == processId
                && candidate.Handle == window);
            if (entry is not null)
            {
                QueueClientActivityScan(entry,
                    eventType == NativeMethods.EVENT_OBJECT_NAMECHANGE
                        ? "window-name-change" : "window-state-change");
            }
        }));
    }

    private void QueueClientActivityScan(ClientWindowEntry entry, string source)
    {
        if (_isClosing || entry.Handle == IntPtr.Zero || !Entries.Contains(entry)) return;
        var now = DateTime.UtcNow;
        if (_lastClientActivityScan.TryGetValue(entry, out var previous)
            && now - previous < ClientActivityDebounce
            && source != "shell-flash") return;

        _lastClientActivityScan[entry] = now;
        DiagnosticLogService.WriteActivityEvent($"activity-trigger-{source}", entry.ProcessId);
        TriggerImmediateBadgeScan(entry);

        // Shell/WinEvent 往往早于微信绘制角标；延后补查一次，仍以角标结果
        // 决定是否提醒。多次事件只保留最后一次补查。
        var retryGeneration = _activityRetryGeneration.TryGetValue(entry, out var previousGeneration)
            ? previousGeneration + 1 : 1;
        _activityRetryGeneration[entry] = retryGeneration;
        var handle = entry.Handle;
        var processId = entry.ProcessId;
        var accountGeneration = entry.AccountGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            await Task.Delay(700);
            if (_isClosing || !Entries.Contains(entry)
                || entry.Handle != handle || entry.ProcessId != processId
                || entry.AccountGeneration != accountGeneration
                || !_activityRetryGeneration.TryGetValue(entry, out var currentGeneration)
                || currentGeneration != retryGeneration) return;
            _activityRetryGeneration.Remove(entry);
            TriggerImmediateBadgeScan(entry);
        }));
    }

    /// <summary>
    /// 把“某个账号请求注意”统一映射到JNest自身的提醒。
    /// 不读取消息正文；任务栏闪烁与账号红点/数字是两条独立显示通道。
    /// </summary>
    private void NotifyAccountAttention(ClientWindowEntry entry)
    {
        if (_isClosing || IsEntryActivelyViewed(entry))
        {
            return;
        }

        ShowTemporaryStatus($"{entry.DisplayName} 收到新消息。", TimeSpan.FromSeconds(5), entry);
        if (_settings.EnableNotificationSound)
            SystemSounds.Exclamation.Play();

        var managerHandle = new WindowInteropHelper(this).Handle;
        if (managerHandle == IntPtr.Zero)
        {
            return;
        }

        // Windows toast is shown only when the manager is truly in the
        // background (or minimized). A different selected account in an
        // otherwise foreground JNest should not produce a popup.
        var isManagerInForeground = WindowState != WindowState.Minimized
            && (IsActive || WindowHost.IsManagerContextForeground);
        if (!isManagerInForeground && _settings.EnableToastNotifications)
        {
            ShowAccountNotification(entry);
        }

        if (!_settings.EnableTaskbarFlash) return;

        var flashInfo = new NativeMethods.FLASHWINFO
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            Window = managerHandle,
            Flags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
            Count = uint.MaxValue,
            Timeout = 0
        };
        // 返回值是调用前标题栏是否处于活动状态，不能作为调用是否成功的判断。
        NativeMethods.FlashWindowEx(ref flashInfo);
        DiagnosticLogService.WriteActivityEvent("taskbar-flash-requested", entry.ProcessId);
    }

    /// <summary>Use the existing account avatar cache for Windows toast; retain a legacy tray fallback.</summary>
    private static string GetAccountToastTag(ClientWindowEntry entry)
        => $"account-{entry.ProcessId}";

    private void DismissAccountToast(ClientWindowEntry entry)
    {
        _lastAccountToast.Remove(entry);
        try
        {
            ToastNotificationManagerCompat.History.Remove(
                GetAccountToastTag(entry), AccountToastGroup);
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteActivityEvent(
                "toast-dismiss-" + exception.GetType().Name, entry.ProcessId);
        }
    }

    private void ShowAccountNotification(ClientWindowEntry entry)
    {
        var now = DateTime.UtcNow;
        if (_lastAccountToast.TryGetValue(entry, out var previous)
            && now - previous < AccountToastCooldown) return;
        _lastAccountToast[entry] = now;
        try
        {
            var toast = new ToastContentBuilder()
                .AddText(entry.DisplayName)
                .AddText("收到新消息。");

            var avatar = entry.AvatarPath;
            if (!string.IsNullOrWhiteSpace(avatar) && File.Exists(avatar))
            {
                toast.AddAppLogoOverride(new Uri(Path.GetFullPath(avatar)),
                    ToastGenericAppLogoCrop.Circle);
            }
            else
            {
                // Only a public app logo is written to the temporary image cache.
                // Chat/message content and account profile data are never exported.
                var fallback = Path.Combine(Path.GetTempPath(), "JNest-brand-toast.png");
                if (!File.Exists(fallback))
                {
                    var resource = Application.GetResourceStream(
                        new Uri("pack://application:,,,/Assets/app-logo.png"));
                    if (resource is not null)
                    {
                        using var input = resource.Stream;
                        using var output = File.Create(fallback);
                        input.CopyTo(output);
                    }
                }
                if (File.Exists(fallback))
                    toast.AddAppLogoOverride(new Uri(fallback), ToastGenericAppLogoCrop.Circle);
            }
            // Stable tag+group replaces the existing account notification
            // instead of stacking Windows 11 toasts for rapid messages.
            toast.Show(notification =>
            {
                notification.Tag = GetAccountToastTag(entry);
                notification.Group = AccountToastGroup;
            });
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteActivityEvent(
                "toast-fallback-" + exception.GetType().Name, entry.ProcessId);
            _notifyIcon?.ShowBalloonTip(3000, entry.DisplayName,
                "收到新消息。", WinForms.ToolTipIcon.None);
        }
    }

}

