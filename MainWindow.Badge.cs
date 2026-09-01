using System.Windows;
using System.Windows.Threading;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

/// <summary>
/// 未读消息角标轮询：
/// 1) HSHELL_FLASH 触发时立即检测对应账号
/// 2) 同时按低频复查（~4s）所有账号，纠正漏报并确认已读后清零
/// 3) 发现未读立即更新；只有“角标消失”需要连续两次确认，避免动画帧误清
/// </summary>
public partial class MainWindow
{
    // 低频复查周期：HSHELL_FLASH 已是主触发源，这里只承担"纠漏+已读清零"职责。
    // 4 秒能在"及时纠错"与"截图开销"间取得平衡（每个嵌入窗口每 4 秒截一次）。
    private static readonly TimeSpan BadgePollInterval = TimeSpan.FromMilliseconds(4000);

    private DispatcherTimer? _badgePollTimer;
    private readonly BadgeDetector _badgeDetector = new();
    private int _badgeScanInProgress; // 0 = 空闲，1 = 扫描中（防止重入）
    private readonly HashSet<ClientWindowEntry> _queuedImmediateBadgeScans = [];
    private bool _foregroundCalibrationPending;

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
        _foregroundCalibrationPending = false;
    }

    /// <summary>
    /// 聚窗重新回到前台时补做一次校准。后台期间不再周期性调用
    /// PrintWindow(PW_RENDERFULLCONTENT)，避免打断 Chrome/DWM 的 GPU 合成。
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
    /// 由 HSHELL_FLASH 通知触发：立刻对单个账号做一次识别，跳过"连续两次一致"门槛，
    /// 直接进入"半稳定"状态——也就是说，这一次立刻反映到 UI，但需要下一轮复查
    /// 再确认才会"锁死"为稳定值。
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
                    await ScanSingleAsync(entry, confirmOnly: false);
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

        // PrintWindow(PW_RENDERFULLCONTENT) forces a synchronous read-back from
        // hardware-accelerated WeChat/WhatsApp surfaces. Repeating it while an
        // unrelated app is foreground can stall DWM and make Chromium windows
        // visibly flash. Shell attention events still use the immediate path;
        // periodic full calibration resumes as soon as the user returns.
        if (!WindowHost.IsManagerContextForeground)
        {
            _foregroundCalibrationPending = true;
            return;
        }

        if (Interlocked.CompareExchange(ref _badgeScanInProgress, 1, 0) != 0)
        {
            _foregroundCalibrationPending = true;
            return;
        }

        try
        {
            _foregroundCalibrationPending = false;
            var targets = Entries
                .Where(entry => entry.IsAttached && entry.Handle != IntPtr.Zero)
                .ToArray();

            if (targets.Length == 0)
            {
                return;
            }

            // 复查阶段（confirmOnly=true）：必须连续两次结果一致才更新 UI。
            foreach (var entry in targets)
            {
                if (!NativeMethods.IsWindow(entry.Handle)) continue;
                await ScanSingleAsync(entry, confirmOnly: true);
            }
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

    /// <summary>
    /// 对单个账号截图 + 识别 + 更新 UI。
    /// confirmOnly=true：未读数字立即更新；无角标需要连续两次确认后才清除。
    /// </summary>
    private async Task ScanSingleAsync(ClientWindowEntry entry, bool confirmOnly)
    {
        if (!NativeMethods.IsWindow(entry.Handle)) return;

        var result = await Task.Run(() => _badgeDetector.Detect(entry.Handle, entry.Kind))
            .ConfigureAwait(true);

        if (result is null || !NativeMethods.IsWindow(entry.Handle)) return;

        var detection = result.Value;
        if (!_badgeStableState.TryGetValue(entry, out var stable))
        {
            stable = new BadgeStableState();
            _badgeStableState[entry] = stable;
        }

        if (confirmOnly)
        {
            if (detection.Confidence != BadgeConfidenceLevel.NoBadge)
            {
                // 受限区域内发现角标后立即显示，保证一次 4 秒校准即可同步。
                ApplyDetection(entry, detection);
                stable.Stable = detection;
                stable.Pending = null;
                stable.PendingSince = null;
            }
            else if (stable.Pending.HasValue && IsResultEqual(stable.Pending.Value, detection))
            {
                // 连续两次都没有角标才清除，避免客户端重绘瞬间造成闪烁。
                ApplyDetection(entry, detection);
                stable.Stable = detection;
                stable.Pending = null;
                stable.PendingSince = null;
            }
            else
            {
                stable.Pending = detection;
                stable.PendingSince = DateTime.UtcNow;
            }
        }
        else
        {
            // HSHELL_FLASH 触发：立即更新 UI，但把当前值作为"暂定结果"，
            // 等下一轮复查验证后锁死（避免一闪即逝的误报长期停留在 UI 上）。
            ApplyDetection(entry, detection);
            stable.Pending = detection;
            stable.PendingSince = DateTime.UtcNow;
            stable.Stable = null; // 标记"未稳定"，下一轮复查需要再次匹配
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
