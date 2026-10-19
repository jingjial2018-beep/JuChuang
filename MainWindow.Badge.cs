using System.Windows;
using System.Windows.Threading;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

/// <summary>
/// 未读消息角标轮询：
/// 1) Shell/WinEvent 活动触发时立即检测对应账号
/// 2) 前台每 4 秒复查所有账号；后台每轮轮流复查一个微信账号
/// 3) 发现未读立即更新；只有“角标消失”需要连续两次确认，避免动画帧误清
/// </summary>
public partial class MainWindow
{
    // 前台每 4 秒校准；后台每轮只读一个微信账号，覆盖微信不发 Shell/WinEvent
    // 的消息，同时避免过去“每轮截取所有后台窗口”带来的 DWM 抖动。
    private static readonly TimeSpan BadgePollInterval = TimeSpan.FromMilliseconds(4000);

    private DispatcherTimer? _badgePollTimer;
    private readonly BadgeDetector _badgeDetector = new();
    private int _badgeScanInProgress; // 0 = 空闲，1 = 扫描中（防止重入）
    private readonly HashSet<ClientWindowEntry> _queuedImmediateBadgeScans = [];
    private bool _foregroundCalibrationPending;
    private int _backgroundBadgeCursor;
    // A pending clear must be confirmed quickly rather than waiting for the
    // next 4-second foreground/background polling round.
    private readonly HashSet<ClientWindowEntry> _pendingClearRechecks = [];

    // 每个账号的"上一次稳定结果"（用于"连续两次一致才更新"的状态同步）
    private readonly Dictionary<ClientWindowEntry, BadgeStableState> _badgeStableState = new();

    private void StartBadgePolling()
    {
        if (_badgePollTimer is not null)
        {
            return;
        }

        _badgePollTimer = new DispatcherTimer { Interval = BadgePollInterval };
        _badgePollTimer.Tick += BadgePollTimer_Tick;
        _badgePollTimer.Start();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => BadgePollTimer_Tick(null, EventArgs.Empty)));
    }

    private void StopBadgePolling()
    {
        _badgePollTimer?.Stop();
        _badgePollTimer = null;
        _queuedImmediateBadgeScans.Clear();
        _pendingClearRechecks.Clear();
        _foregroundCalibrationPending = false;
    }

    /// <summary>
    /// JNest重新回到前台时补做一次全量校准。后台只逐个复查微信，
    /// 避免集中 PrintWindow(PW_RENDERFULLCONTENT) 打断 GPU 合成。
    /// </summary>
    private void TriggerForegroundBadgeCalibration()
    {
        if (_isClosing || _isPreviewMode || _badgePollTimer is null)
        {
            return;
        }

        _foregroundCalibrationPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => BadgePollTimer_Tick(null, EventArgs.Empty)));
    }

    /// <summary>
    /// 活动事件本身不代表未读；单账号扫描确认角标新增后才提醒。
    /// </summary>
    private void TriggerImmediateBadgeScan(ClientWindowEntry entry)
    {
        if (_isClosing || _isPreviewMode || entry is null || entry.Handle == IntPtr.Zero)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _badgeScanInProgress, 1, 0) != 0)
        {
            // 定时校准正在截图时不能丢掉 Shell 事件；扫描结束后立即补做。
            _queuedImmediateBadgeScans.Add(entry);
            return;
        }
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () =>
            {
                try
                {
                    await ScanSingleAsync(entry);
                }
                catch (Exception exception)
                {
                    ReportBackgroundFailure("ImmediateBadgeScan", exception);
                }
                finally
                {
                    Interlocked.Exchange(ref _badgeScanInProgress, 0);
                    DrainQueuedImmediateBadgeScan();
                    SchedulePendingForegroundCalibration();
                }
            }));
    }

    private async void BadgePollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _isPreviewMode)
        {
            return;
        }

        // PrintWindow 可阻塞客户端渲染。后台只轮流检查一个微信窗口；
        // 全量校准仍等 JNest 回到前台后再做。
        var managerForeground = WindowHost.IsManagerContextForeground;
        if (!managerForeground) _foregroundCalibrationPending = true;

        if (Interlocked.CompareExchange(ref _badgeScanInProgress, 1, 0) != 0)
        {
            _foregroundCalibrationPending = true;
            return;
        }

        try
        {
            if (managerForeground) _foregroundCalibrationPending = false;
            var targets = Entries
                .Where(entry => entry.IsAttached && entry.Handle != IntPtr.Zero
                    && (managerForeground || entry.Kind == ClientKind.WeChat))
                .ToArray();

            if (targets.Length == 0)
            {
                return;
            }

            // 后台按账号轮转，避免同一轮集中截取多个微信窗口。
            var selected = managerForeground
                ? targets
                : new[] { targets[_backgroundBadgeCursor++ % targets.Length] };
            foreach (var entry in selected)
            {
                if (!NativeMethods.IsWindow(entry.Handle)) continue;
                await ScanSingleAsync(entry);
            }
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure(nameof(BadgePollTimer_Tick), exception);
        }
        finally
        {
            Interlocked.Exchange(ref _badgeScanInProgress, 0);
            DrainQueuedImmediateBadgeScan();
            SchedulePendingForegroundCalibration();
        }
    }

    private void SchedulePendingForegroundCalibration()
    {
        if (!_foregroundCalibrationPending
            || !WindowHost.IsManagerContextForeground
            || Volatile.Read(ref _badgeScanInProgress) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => BadgePollTimer_Tick(null, EventArgs.Empty)));
    }

    private void DrainQueuedImmediateBadgeScan()
    {
        if (_isClosing || _queuedImmediateBadgeScans.Count == 0)
        {
            return;
        }

        var entry = _queuedImmediateBadgeScans.First();
        _queuedImmediateBadgeScans.Remove(entry);
        if (Entries.Contains(entry) && NativeMethods.IsWindow(entry.Handle))
        {
            TriggerImmediateBadgeScan(entry);
        }
    }

    private void RequestSelectedBadgeCalibration(ClientWindowEntry entry)
    {
        if (_isClosing || _isPreviewMode || !Entries.Contains(entry)
            || !entry.IsAttached || !NativeMethods.IsWindow(entry.Handle)) return;
        TriggerImmediateBadgeScan(entry);
    }

    private void SchedulePendingClearConfirmation(ClientWindowEntry entry)
    {
        if (!_pendingClearRechecks.Add(entry)) return;
        var handle = entry.Handle;
        var processId = entry.ProcessId;
        var generation = entry.AccountGeneration;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                await Task.Delay(450);
                if (_isClosing || !Entries.Contains(entry) || !entry.IsAttached
                    || entry.Handle != handle || entry.ProcessId != processId
                    || entry.AccountGeneration != generation) return;
                TriggerImmediateBadgeScan(entry);
            }
            catch (Exception exception)
            {
                ReportBackgroundFailure("PendingClearConfirmation", exception);
            }
            finally
            {
                _pendingClearRechecks.Remove(entry);
            }
        }));
    }

    /// <summary>
    /// 对单个账号截图 + 识别 + 更新 UI。
    /// 未读数字立即更新；无角标需要连续两次确认后才清除。
    /// </summary>
    private async Task ScanSingleAsync(ClientWindowEntry entry)
    {
        if (!NativeMethods.IsWindow(entry.Handle) || IsProcessExiting(entry.ProcessId)) return;

        var scannedHandle = entry.Handle;
        var scannedProcessId = entry.ProcessId;
        var scannedGeneration = entry.AccountGeneration;
        var scannedExitVersion = GetProcessExitVersion(scannedProcessId);

        var result = await Task.Run(() => _badgeDetector.Detect(scannedHandle, entry.Kind))
            .ConfigureAwait(true);

        // 截图期间账号可能退出、重新登录或重建 HWND。旧结果绝不能写入新账号。
        if (_isClosing || IsProcessExiting(scannedProcessId)
            || !Entries.Contains(entry) || entry.Handle != scannedHandle
            || entry.ProcessId != scannedProcessId
            || entry.AccountGeneration != scannedGeneration
            || GetProcessExitVersion(scannedProcessId) != scannedExitVersion
            || !NativeMethods.IsWindow(scannedHandle)) return;

        if (result is null)
        {
            // Unavailable captures must never be mistaken for "no unread".
            DiagnosticLogService.WriteActivityEvent("badge-scan-unavailable", scannedProcessId);
            return;
        }

        var detection = result.Value;
        if (!_badgeStableState.TryGetValue(entry, out var stable))
        {
            stable = new BadgeStableState();
            _badgeStableState[entry] = stable;
        }

        if (detection.Confidence != BadgeConfidenceLevel.NoBadge)
        {
            // 首次扫描只建立基线；后续从无到有或可靠数字增长才是新未读。
            var shouldNotify = BadgeAttentionPolicy.IsNewUnread(stable.Stable, detection);
            ApplyDetection(entry, detection);
            stable.Stable = detection;
            stable.Pending = null;
            stable.PendingSince = null;
            if (shouldNotify)
            {
                DiagnosticLogService.WriteActivityEvent("badge-new-unread-confirmed", scannedProcessId);
                NotifyAccountAttention(entry);
            }
        }
        else if (stable.Pending.HasValue && IsResultEqual(stable.Pending.Value, detection))
        {
            // 无角标仍需连续两次确认，避免动画帧造成误清零。
            ApplyDetection(entry, detection);
            stable.Stable = detection;
            stable.Pending = null;
            stable.PendingSince = null;
        }
        else
        {
            stable.Pending = detection;
            stable.PendingSince = DateTime.UtcNow;
            if (entry.PendingClearAfterDetection || entry.HasMessageAlert)
                SchedulePendingClearConfirmation(entry);
            if (stable.Stable is null)
            {
                // 新账号首次无角标可直接作为基线，不需要等待第二次扫描。
                stable.Stable = detection;
                ApplyDetection(entry, detection);
            }
        }
    }

    /// <summary>
    /// 把识别结果映射到 entry 的可绑定属性：
    /// - NoBadge：无未读，隐藏角标
    /// - High：显示数字（MessageAlertCount = detection.Number）
    /// - Medium：显示红点（数字=0 但 HasMessageAlert=true）
    /// </summary>
    private void ApplyDetection(ClientWindowEntry entry, BadgeResult detection)
    {
        if (entry.PendingClearAfterDetection)
        {
            // 用户刚刚点击了账号：角标已经消失则清零；如果客户端仍显示未读，
            // 取消等待状态并继续同步真实数字，不能把结果永久拦截。
            if (detection.Confidence == BadgeConfidenceLevel.NoBadge)
            {
                entry.PendingClearAfterDetection = false;
                entry.MessageAlertCount = 0;
                entry.AlertDisplayMode = AlertDisplayMode.None;
                UpdateTaskbarUnreadOverlay();
                return;
            }

            entry.PendingClearAfterDetection = false;
        }

        switch (detection.Confidence)
        {
            case BadgeConfidenceLevel.High:
                entry.MessageAlertCount = Math.Clamp(detection.Number, 1, 100);
                entry.AlertDisplayMode = AlertDisplayMode.Count;
                break;
            case BadgeConfidenceLevel.Medium:
                entry.MessageAlertCount = 0;
                entry.AlertDisplayMode = AlertDisplayMode.Dot;
                break;
            default:
                entry.MessageAlertCount = 0;
                entry.AlertDisplayMode = AlertDisplayMode.None;
                break;
        }
        UpdateTaskbarUnreadOverlay();
    }

    /// <summary>比较两次识别结果是否一致（用于状态同步）。</summary>
    private static bool IsResultEqual(BadgeResult a, BadgeResult b)
    {
        return a.Confidence == b.Confidence && a.Number == b.Number;
    }

    /// <summary>单个账号的"稳定结果 + 暂定结果"状态。</summary>
    private sealed class BadgeStableState
    {
        public BadgeResult? Stable;
        public BadgeResult? Pending;
        public DateTime? PendingSince;
    }
}

