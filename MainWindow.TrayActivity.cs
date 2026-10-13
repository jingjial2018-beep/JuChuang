using System.Windows.Threading;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private static readonly TimeSpan TrayActivityPollInterval = TimeSpan.FromMilliseconds(650);

    private readonly WeChatTrayActivityMonitor _weChatTrayActivityMonitor = new();
    private DispatcherTimer? _trayActivityTimer;
    private int _trayActivityPollInProgress;
    private string? _lastTrayActivityStatusCode;

    private void StartWeChatTrayActivityMonitoring()
    {
        if (_trayActivityTimer is not null || _isClosing || _isPreviewMode)
        {
            return;
        }

        _weChatTrayActivityMonitor.Start();
        _trayActivityTimer = new DispatcherTimer
        {
            Interval = TrayActivityPollInterval
        };
        _trayActivityTimer.Tick += TrayActivityTimer_Tick;
        _trayActivityTimer.Start();

        DiagnosticLogService.WriteSystemEvent(
            "tray-monitor-started",
            "mode=injection-free; direct=Explorer notification-area metadata; fallback=out-of-context WinEvent");
    }

    private void StopWeChatTrayActivityMonitoring()
    {
        if (_trayActivityTimer is not null)
        {
            _trayActivityTimer.Stop();
            _trayActivityTimer.Tick -= TrayActivityTimer_Tick;
            _trayActivityTimer = null;
        }

        _weChatTrayActivityMonitor.Dispose();
        Interlocked.Exchange(ref _trayActivityPollInProgress, 0);
    }

    private async void TrayActivityTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _isPreviewMode)
        {
            return;
        }

        var trackedEntries = Entries
            .Where(entry =>
                entry.Kind == ClientKind.WeChat
                && entry.IsAttached
                && entry.IsOnline
                && entry.ProcessId > 0
                && entry.Handle != IntPtr.Zero
                && NativeMethods.IsWindow(entry.Handle))
            .ToArray();

        if (trackedEntries.Length == 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _trayActivityPollInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var processIds = trackedEntries
                .Select(entry => entry.ProcessId)
                .Distinct()
                .ToArray();

            TrayActivityPollResult result;
            try
            {
                result = await Task.Run(() => _weChatTrayActivityMonitor.Poll(processIds));
            }
            catch (Exception exception)
            {
                DiagnosticLogService.WriteSystemEvent(
                    "tray-monitor-poll-failed",
                    exception.GetType().Name);
                return;
            }

            if (!string.Equals(
                    _lastTrayActivityStatusCode,
                    result.StatusCode,
                    StringComparison.Ordinal))
            {
                _lastTrayActivityStatusCode = result.StatusCode;
                DiagnosticLogService.WriteSystemEvent(
                    "tray-monitor-status",
                    $"status={result.StatusCode}; direct={result.DirectEnumerationAvailable}; matching={result.MatchingIconCount}");
            }

            foreach (var processId in result.ChangedProcessIds)
            {
                var entry = Entries.FirstOrDefault(candidate =>
                    candidate.Kind == ClientKind.WeChat
                    && candidate.ProcessId == processId
                    && candidate.IsAttached
                    && NativeMethods.IsWindow(candidate.Handle));
                if (entry is null)
                {
                    continue;
                }

                DiagnosticLogService.WriteAccountEvent(entry, "tray-activity-direct");

                // 托盘变化不是“消息”。这里只把它当成触发器，随后由真实角标检测
                // 确认“从无到有”或可靠数字增长，确认后才让 JNest 闪烁。
                if (!IsEntryActivelyViewed(entry))
                {
                    TriggerImmediateBadgeScan(entry, notifyAfterConfirmedUnread: true);
                }
            }

            if (result.GenericTrayActivity)
            {
                // Windows 11 某些任务栏实现不再暴露经典 ToolbarWindow32。
                // 这时只在 Explorer 确实发出通知区可访问性变化事件后，低频复查
                // 当前所有微信账号；检测不到未读就什么也不做。
                foreach (var entry in trackedEntries)
                {
                    if (!Entries.Contains(entry)
                        || IsEntryActivelyViewed(entry)
                        || !NativeMethods.IsWindow(entry.Handle))
                    {
                        continue;
                    }

                    DiagnosticLogService.WriteAccountEvent(entry, "tray-activity-fallback");
                    TriggerImmediateBadgeScan(entry, notifyAfterConfirmedUnread: true);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _trayActivityPollInProgress, 0);
        }
    }
}
