using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using JuChuang.Models;
using JuChuang.Services;
using Microsoft.Win32;

namespace JuChuang;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _scanTimer;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _profileReadLock = new(2, 2);
    private readonly AppSettings _settings;
    private readonly WeChatProfileCacheService _profileCache = new();
    private readonly Dictionary<string, WeChatLocalProfile> _profilesByIdentity =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, WeChatAccountIdentity> _resolvedWeChatAccounts = [];
    private readonly Dictionary<int, long> _resolvedAccountProcessStarts = [];
    private readonly Dictionary<int, long> _accountResolutionLoads = [];
    private long _accountResolutionSequence;
    private readonly Dictionary<int, DateTime> _accountResolutionRetryAfter = [];
    private readonly Dictionary<int, int> _accountResolutionFailures = [];
    private readonly HashSet<IntPtr> _dismissedWindowHandles = [];
    private ClientWindowEntry? _selectedEntry;
    private ClientKind? _selectNextKind;
    private ClientLaunchContext? _pendingLaunchContext;
    private int _wechatNumber;
    private bool _isClosing;
    private bool _isPreviewMode;
    private bool _isTitleBarInteraction;
    private bool _isClientLaunchInProgress;
    private readonly bool _isClientSelfTestMode;
    private readonly int? _selfTestTargetProcessId;
    private int _renderRefreshGeneration;
    private bool _overlayBoundsUpdatePending;
    private Point _accountDragStartPoint;
    private ClientWindowEntry? _accountDragEntry;
    private ClientWindowEntry? _selectionBeforeAccountDrag;

    // 登录页 -> 主界面、客户端自重建 HWND 时，进程通常仍然存活。
    // 给同一 PID 一个短暂重绑定窗口，避免账号身份、昵称和头像随 HWND 一起被丢弃。
    private static readonly TimeSpan WindowRebindGracePeriod = TimeSpan.FromSeconds(15);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var commandLineArguments = Environment.GetCommandLineArgs();
        _isClientSelfTestMode = commandLineArguments
            .Any(argument => argument.Equals("--client-selftest", StringComparison.OrdinalIgnoreCase));
        var selfTestTargetArgument = commandLineArguments.FirstOrDefault(argument =>
            argument.StartsWith("--selftest-pid=", StringComparison.OrdinalIgnoreCase));
        if (selfTestTargetArgument is not null
            && int.TryParse(selfTestTargetArgument[(selfTestTargetArgument.IndexOf('=') + 1)..], out var targetProcessId))
        {
            _selfTestTargetProcessId = targetProcessId;
        }
        if (_isClientSelfTestMode)
        {
            Title = "JNest窗口托管测试";
        }
        _settings = AppSettings.Load();
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _scanTimer.Tick += ScanTimer_Tick;
        _statusResetTimer.Tick += (_, _) => ClearTemporaryStatus();
        StateChanged += MainWindow_StateChanged;
        Activated += (_, _) => TriggerForegroundBadgeCalibration();
        LocationChanged += (_, _) => QueueOverlayBoundsUpdate();
        SizeChanged += (_, _) => QueueOverlayBoundsUpdate();
    }

    private readonly DispatcherTimer _statusResetTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _pendingStatusText;
    private ClientWindowEntry? _pendingMessageEntry;

    // Middle status text is temporary; the right-hand privacy statement is fixed.
    private void ShowTemporaryStatus(string message, TimeSpan? duration = null,
        ClientWindowEntry? messageEntry = null)
    {
        _statusResetTimer.Stop();
        StatusMessageText.Text = message;
        _pendingStatusText = message;
        _pendingMessageEntry = messageEntry;
        _statusResetTimer.Interval = duration ?? TimeSpan.FromSeconds(3);
        _statusResetTimer.Start();
    }

    private void ClearTemporaryStatus(ClientWindowEntry? viewedEntry = null)
    {
        if (viewedEntry is not null && !ReferenceEquals(viewedEntry, _pendingMessageEntry))
            return;
        _statusResetTimer.Stop();
        if (_pendingStatusText is not null && StatusMessageText.Text == _pendingStatusText)
            StatusMessageText.Text = string.Empty;
        _pendingStatusText = null;
        _pendingMessageEntry = null;
    }

    public ObservableCollection<ClientWindowEntry> Entries { get; } = [];

    public ClientWindowEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value))
            {
                return;
            }

            _selectedEntry = value;
            if (value is not null)
            {
                // 只有卡片原本已有提醒时，切换账号才进入“等待截图确认已读”。
                // 初次选中不能阻止 OCR 把客户端里原本存在的未读角标同步出来。
                value.PendingClearAfterDetection = value.HasMessageAlert;
                var managerHandle = new WindowInteropHelper(this).Handle;
                if (managerHandle != IntPtr.Zero)
                {
                    var stopInfo = new NativeMethods.FLASHWINFO
                    {
                        Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
                        Window = managerHandle,
                        Flags = NativeMethods.FLASHW_STOP,
                        Count = 0,
                        Timeout = 0
                    };
                    NativeMethods.FlashWindowEx(ref stopInfo);
                }
            }
            OnPropertyChanged();
            ShowSelectedEntry();
            if (value is not null && _pendingMessageEntry is not null
                && ReferenceEquals(value, _pendingMessageEntry))
                ClearTemporaryStatus(value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReleaseHostedWindowsForShutdown() => WindowHost.DetachAll();

    private void QueueOverlayBoundsUpdate()
    {
        if (_overlayBoundsUpdatePending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _overlayBoundsUpdatePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _overlayBoundsUpdatePending = false;
            WindowHost.UpdateOverlayBounds();
        }));
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
        var isMinimized = WindowState == WindowState.Minimized;
        WindowHost.HandleOwnerWindowStateChanged(isMinimized);
        EnsureMainWindowTaskbarButton();

        // 最小化时保持JNest主窗口可见状态，让 Windows 继续在任务栏显示应用图标。
        // 只由 WindowHost 隐藏/停放被托管的微信和 WhatsApp 窗口，避免它们浮到桌面。
        // 系统托盘图标仍然保留，作为额外入口，而不是“最小化到托盘”的替代任务栏行为。
        if (isMinimized)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            WindowHost.UpdateOverlayBounds();
            if (!_isTitleBarInteraction) WindowHost.ActivateCurrentWindow();
        }));
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeGlyph.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M 4,1 H 9 V 6 H 4 Z M 1,4 H 6 V 9 H 1 Z"
                : "M 1,1 H 9 V 9 H 1 Z");
    }

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowWidth is { } windowWidth && windowWidth > 0)
        {
            Width = windowWidth;
        }

        if (_settings.WindowHeight is { } windowHeight && windowHeight > 0)
        {
            Height = windowHeight;
        }

        if (_settings.WindowLeft is { } savedLeft && _settings.WindowTop is { } savedTop)
        {
            // Only restore the position when the window would be at least partially
            // visible on the current virtual screen, so it never opens off-screen
            // after a monitor has been unplugged.
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            if (savedLeft < virtualRight && savedLeft + Width > virtualLeft
                && savedTop < virtualBottom && savedTop + Height > virtualTop)
            {
                Left = savedLeft;
                Top = savedTop;
            }
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (_settings.SidebarWidth is { } sidebarWidth && sidebarWidth >= SidebarColumn.MinWidth)
        {
            SidebarColumn.Width = new GridLength(sidebarWidth);
        }
    }

    private void SaveWindowPlacement()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.SidebarWidth = SidebarColumn.Width.Value;
        _settings.Save();
    }

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // 拖动分隔条时实时调整侧栏宽度，Clamp 到 MinWidth/MaxWidth。
        var target = SidebarColumn.Width.Value + e.HorizontalChange;
        target = Math.Max(SidebarColumn.MinWidth, Math.Min(SidebarColumn.MaxWidth, target));
        SidebarColumn.Width = new GridLength(target);
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // 拖动结束即时持久化，避免仅依赖窗口关闭时保存。
        _settings.SidebarWidth = SidebarColumn.Width.Value;
        _settings.Save();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        RestoreWindowPlacement();
        _isPreviewMode = Environment.GetCommandLineArgs()
            .Any(argument => argument.Equals("--preview", StringComparison.OrdinalIgnoreCase));

        if (_isPreviewMode)
        {
            LoadPreviewEntries();
            StatusMessageText.Text = "界面预览模式：不会扫描或接管本地客户端。";
            if (_isClientSelfTestMode)
            {
                ShowClientSelfTestPopup();
            }
            return;
        }

        StatusMessageText.Text = "JNest已启动，正在后台发现本地客户端…";
        // 预热窗口托管宿主：HwndHost 在 Visibility=Collapsed 时可能不会调用
        // BuildWindowCore，导致首次 AttachWindow 因 _hostHandle 未创建而失败
        // （IsReady=false）。这里先设为可见并等待布局完成，确保宿主就绪后再扫描。
        // EmptyState 覆盖在其上，视觉上无变化。
        WindowHost.Visibility = Visibility.Visible;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Background);
        await ScanAndAttachAsync();
        // 首次启动时某些 WebView/Store 客户端（尤其 WhatsApp）窗口已经可枚举，
        // 但第一次跨进程托管可能正好撞上客户端自身的布局/恢复过程而失败。
        // 只对“已经发现但尚未接入”的窗口做两次短暂重试，不启动新进程，
        // 不改变已成功接入的窗口，避免用户必须手动点“嵌入窗口”。
        for (var startupRetry = 0;
             startupRetry < 2 && Entries.Any(entry => !entry.IsAttached && NativeMethods.IsWindow(entry.Handle));
             startupRetry++)
        {
            await Task.Delay(350);
            await ScanAndAttachAsync();
        }

        if (Entries.Count > 0 && SelectedEntry is null)
        {
            SelectedEntry = Entries.FirstOrDefault(entry => entry.IsAttached) ?? Entries[0];
        }

        ShowTemporaryStatus(Entries.Count == 0
            ? "未发现正在运行的客户端，可从顶部新建。"
            : "客户端接入检查完成");
        _scanTimer.Start();
        StartBadgePolling();
        if (_isClientSelfTestMode)
        {
            ShowClientSelfTestPopup();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        SaveWindowPlacement();
        _scanTimer.Stop();
        _statusResetTimer.Stop();
        StopBadgePolling();
        StopClientActivityMonitoring();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
        }

        if (_shellHookRegistered)
        {
            NativeMethods.DeregisterShellHookWindow(new WindowInteropHelper(this).Handle);
            _shellHookRegistered = false;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        ReleaseJNestTaskbarIcon();

        WindowHost.DetachAll();
    }

    private async void ScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isClosing)
        {
            await ScanAndAttachAsync();
        }
    }

    private void RemoveEntry(ClientWindowEntry entry)
    {
        DiagnosticLogService.WriteAccountEvent(entry, "entry-removed");
        entry.SessionState = ClientSessionState.Exited;

        if (entry.IsAttached)
        {
            WindowHost.DetachWindow(entry.Handle, bringToFront: false);
        }

        var wasSelected = ReferenceEquals(SelectedEntry, entry);
        var processId = entry.ProcessId;
        _queuedImmediateBadgeScans.Remove(entry);
        _badgeStableState.Remove(entry);
        _lastClientActivityScan.Remove(entry);
        _activityRetryGeneration.Remove(entry);
        Entries.Remove(entry);
        UpdateTaskbarUnreadOverlay();

        if (entry.Kind == ClientKind.WeChat
            && !Entries.Any(candidate => candidate.Kind == ClientKind.WeChat && candidate.ProcessId == processId))
        {
            _resolvedWeChatAccounts.Remove(processId);
            _resolvedAccountProcessStarts.Remove(processId);
            _accountResolutionLoads.Remove(processId);
            _accountResolutionRetryAfter.Remove(processId);
            _accountResolutionFailures.Remove(processId);
        }

        if (wasSelected)
        {
            SelectedEntry = Entries.FirstOrDefault();
        }

        UpdateWindowCount();
    }

    private static long? TryGetProcessStartUtcTicks(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // Some processes deny access to their start time; keep legacy discovery working.
            return null;
        }
    }

    private static bool IsClientProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static ClientSessionState MapSessionState(ClientWindowRole role)
        => role switch
        {
            ClientWindowRole.Login => ClientSessionState.Login,
            ClientWindowRole.Main => ClientSessionState.Main,
            _ => ClientSessionState.Starting
        };

    private static void UpdateEntryFromCandidate(
        ClientWindowEntry entry,
        WindowCandidate candidate)
    {
        entry.Handle = candidate.Handle;
        entry.ProcessId = candidate.ProcessId;
        entry.ProcessName = candidate.ProcessName;
        entry.WindowTitle = candidate.WindowTitle;
        entry.ClientVersion = candidate.ClientVersion;
        entry.SessionState = MapSessionState(candidate.Role);
        entry.LastSeenUtc = DateTime.UtcNow;
        entry.WindowMissingSinceUtc = null;
        entry.IsOnline = true;
    }

    private void PrepareEntryForCandidate(
        ClientWindowEntry entry,
        WindowCandidate candidate)
    {
        // 同一微信进程从 Main/Rebinding 回到 Login，视为可能发生账号切换。
        // 先结束上一账号代际，再接收新的 Login/Main 资料，避免昵称、头像、
        // 自定义名称和未读状态串到下一账号。
        if (candidate.Kind == ClientKind.WeChat
            && candidate.Role == ClientWindowRole.Login
            && entry.SessionState != ClientSessionState.Login
            && !string.IsNullOrWhiteSpace(entry.IdentityKey))
        {
            ResetWeChatAccountBinding(entry);
        }

        UpdateEntryFromCandidate(entry, candidate);
    }

    private void ResetWeChatAccountBinding(ClientWindowEntry entry)
    {
        var processId = entry.ProcessId;
        DiagnosticLogService.WriteAccountEvent(entry, "account-generation-reset");
        entry.AccountGeneration++;
        _resolvedWeChatAccounts.Remove(processId);
        _resolvedAccountProcessStarts.Remove(processId);
        // Invalidate in-flight resolution before the next account is allowed to start.
        // Old tasks may finish later, but their request tokens can no longer apply.
        _accountResolutionLoads.Remove(processId);
        _accountResolutionRetryAfter.Remove(processId);
        _accountResolutionFailures.Remove(processId);
        ClientMetadataService.InvalidateLoginAccountCache();

        entry.IdentityKey = null;
        entry.AccountId = null;
        entry.HasCustomName = false;
        entry.AvatarPath = null;
        entry.ProfileState = AccountProfileState.Pending;
        entry.ProfileDiagnosticCode = null;
        entry.ProfileDiagnosticDetail = null;
        entry.MessageAlertCount = 0;
        entry.AlertDisplayMode = AlertDisplayMode.None;
        UpdateTaskbarUnreadOverlay();
        entry.PendingClearAfterDetection = false;
        _queuedImmediateBadgeScans.Remove(entry);
        _badgeStableState.Remove(entry);
        _lastClientActivityScan.Remove(entry);
        _activityRetryGeneration.Remove(entry);

        var index = Entries.IndexOf(entry);
        entry.DisplayName = index >= 0 ? $"微信 {index + 1}" : "微信";

        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
            RefreshSelectedProfileStatus(entry);
        }
    }

    private void QueueOrApplyWeChatIdentity(
        ClientWindowEntry entry,
        WindowCandidate candidate,
        IReadOnlyDictionary<int, WeChatAccountIdentity> accounts)
    {
        if (candidate.Kind != ClientKind.WeChat)
        {
            return;
        }

        if (_resolvedWeChatAccounts.TryGetValue(candidate.ProcessId, out var resolvedAccount))
        {
            var currentStart = TryGetProcessStartUtcTicks(candidate.ProcessId);
            if (_resolvedAccountProcessStarts.TryGetValue(candidate.ProcessId, out var knownStart)
                && currentStart.HasValue && knownStart != currentStart.Value)
            {
                ResetWeChatAccountBinding(entry);
            }
            else
            {
                ApplyWeChatAccountIdentity(entry, resolvedAccount);
                return;
            }
        }

        accounts.TryGetValue(candidate.ProcessId, out var preferredAccount);
        QueueWeChatAccountResolution(entry, preferredAccount);
    }

    private void TryCompletePendingLaunch(
        WindowCandidate candidate,
        ClientWindowEntry entry)
    {
        if (_selectNextKind != candidate.Kind)
        {
            return;
        }

        if (_pendingLaunchContext is not null)
        {
            if (!_pendingLaunchContext.CanClaim(candidate.Kind, candidate.ProcessId))
            {
                return;
            }

            _pendingLaunchContext.Claim(candidate.ProcessId, candidate.Handle);
        }

        _pendingLaunchContext = null;
        _selectNextKind = null;
        SelectedEntry = entry;
        AccountsList.SelectedItem = entry;
        ShowTemporaryStatus($"已接入 {entry.DisplayName}。");
    }

    private async Task<ClientLaunchContext> CaptureLaunchContextAsync(ClientKind kind)
    {
        var knownProcessIds = Entries
            .Where(entry => entry.Kind == kind && entry.ProcessId > 0)
            .Select(entry => entry.ProcessId)
            .ToHashSet();

        try
        {
            var discoveredProcessIds = await Task.Run(() =>
                ClientWindowDiscovery.FindClientWindows()
                    .Where(candidate => candidate.Kind == kind)
                    .Select(candidate => candidate.ProcessId)
                    .Where(processId => processId > 0)
                    .Distinct()
                    .ToArray());

            knownProcessIds.UnionWith(discoveredProcessIds);
        }
        catch
        {
            // 启动上下文只是用于提高“新窗口”匹配准确率；快照失败时仍允许正常启动。
        }

        return new ClientLaunchContext(kind, knownProcessIds);
    }

    private void ClearPendingLaunch(ClientKind kind)
    {
        if (_pendingLaunchContext?.Kind == kind)
        {
            _pendingLaunchContext = null;
        }

        if (_selectNextKind == kind)
        {
            _selectNextKind = null;
        }
    }

    private async Task ScanAndAttachAsync()
    {
        if (!await _scanLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var scanUtc = DateTime.UtcNow;
            foreach (var entry in Entries.Where(entry => entry.Handle != IntPtr.Zero).ToArray())
            {
                var exists = NativeMethods.IsWindow(entry.Handle);
                if (exists)
                {
                    entry.IsOnline = true;
                    entry.LastSeenUtc = scanUtc;
                    entry.WindowMissingSinceUtc = null;
                    if (entry.SessionState == ClientSessionState.Rebinding)
                    {
                        entry.SessionState = ClientSessionState.Main;
                    }

                    entry.Status = entry.IsAttached ? "已嵌入" : "独立窗口";
                    continue;
                }

                // HWND 消失不等于账号进程已经退出。微信在 Login -> Main 或内部重建
                // 顶层窗口时会短暂更换 HWND；先从 Host 中忘掉旧句柄，再给同一 PID
                // 一个短暂重绑定窗口，保留账号身份、昵称、头像和用户自定义名称。
                if (entry.IsAttached)
                {
                    WindowHost.DetachWindow(entry.Handle, bringToFront: false);
                    entry.IsAttached = false;
                }

                var processAlive = IsClientProcessAlive(entry.ProcessId);
                entry.WindowMissingSinceUtc ??= scanUtc;
                if (processAlive
                    && scanUtc - entry.WindowMissingSinceUtc.Value < WindowRebindGracePeriod)
                {
                    entry.IsOnline = true;
                    entry.SessionState = ClientSessionState.Rebinding;
                    entry.Status = "窗口切换中";
                    continue;
                }

                entry.IsOnline = false;
                RemoveEntry(entry);
            }

            _dismissedWindowHandles.RemoveWhere(handle => !NativeMethods.IsWindow(handle));

            var dismissedHandles = _dismissedWindowHandles.ToHashSet();
            var selfTestTargetProcessId = _selfTestTargetProcessId;
            var scanResult = await Task.Run(() =>
            {
                var discoveredCandidates = ClientWindowDiscovery.FindClientWindows(selfTestTargetProcessId)
                    .Where(candidate => !dismissedHandles.Contains(candidate.Handle))
                    .ToArray();
                if (selfTestTargetProcessId.HasValue)
                {
                    discoveredCandidates = discoveredCandidates
                        .Where(candidate => candidate.ProcessId == selfTestTargetProcessId.Value)
                        .ToArray();
                }

                var resolvedAccounts = ClientMetadataService.ResolveWeChatAccounts(discoveredCandidates);
                return (Candidates: discoveredCandidates, Accounts: resolvedAccounts);
            });

            if (_isClosing)
            {
                return;
            }

            var candidates = scanResult.Candidates
                .Where(candidate => !_dismissedWindowHandles.Contains(candidate.Handle))
                .ToArray();
            var weChatAccounts = scanResult.Accounts;

            foreach (var candidate in candidates)
            {
                var existingEntry = Entries.FirstOrDefault(entry => entry.Handle == candidate.Handle);
                if (existingEntry is not null)
                {
                    PrepareEntryForCandidate(existingEntry, candidate);
                    QueueOrApplyWeChatIdentity(existingEntry, candidate, weChatAccounts);

                    // 自动扫描只重试“接入失败”的窗口。用户主动“弹出窗口”会把句柄
                    // 放进 _dismissedWindowHandles，因此不会被后台扫描重新抓回容器。
                    if (!existingEntry.IsAttached
                        && !existingEntry.IsManuallyDetached
                        && !_dismissedWindowHandles.Contains(existingEntry.Handle))
                    {
                        existingEntry.IsAttached = WindowHost.AttachWindow(
                            existingEntry.Handle,
                            makeActive: false);
                        existingEntry.Status = existingEntry.IsAttached ? "已嵌入" : "接入失败";
                    }

                    TryCompletePendingLaunch(candidate, existingEntry);
                    continue;
                }

                // 同一进程的 HWND 被客户端重建时，重新绑定到原账号 Entry，
                // 不创建第二张账号卡片，也不清空已经解析出的 Identity/Profile。
                var rebindingEntry = Entries.FirstOrDefault(entry =>
                    entry.Kind == candidate.Kind
                    && entry.ProcessId == candidate.ProcessId
                    && (entry.SessionState == ClientSessionState.Rebinding
                        || entry.Handle == IntPtr.Zero
                        || !NativeMethods.IsWindow(entry.Handle)));

                if (rebindingEntry is not null)
                {
                    PrepareEntryForCandidate(rebindingEntry, candidate);
                    QueueOrApplyWeChatIdentity(rebindingEntry, candidate, weChatAccounts);
                    if (rebindingEntry.IsManuallyDetached)
                    {
                        rebindingEntry.IsAttached = false;
                        rebindingEntry.Status = "独立窗口";
                    }
                    else
                    {
                        var makeActive = ReferenceEquals(rebindingEntry, SelectedEntry);
                        rebindingEntry.IsAttached = WindowHost.AttachWindow(
                            candidate.Handle,
                            makeActive);
                        rebindingEntry.Status = rebindingEntry.IsAttached ? "已嵌入" : "接入失败";
                        if (makeActive && rebindingEntry.IsAttached)
                        {
                            QueueHostedWindowRefresh(rebindingEntry);
                        }
                    }

                    DiagnosticLogService.WriteAccountEvent(rebindingEntry, "window-rebound");
                    TryCompletePendingLaunch(candidate, rebindingEntry);
                    if (ReferenceEquals(rebindingEntry, SelectedEntry))
                    {
                        ShowSelectedEntry();
                    }
                    continue;
                }

                // A media viewer can be a separate top-level window in the same WeChat
                // UI process. Never promote it to another account slot.
                if (Entries.Any(entry =>
                        entry.Kind == candidate.Kind
                        && entry.ProcessId == candidate.ProcessId
                        && entry.Handle != IntPtr.Zero
                        && NativeMethods.IsWindow(entry.Handle)))
                {
                    continue;
                }

                var entry = CreateEntry(candidate);
                Entries.Add(entry);
                QueueOrApplyWeChatIdentity(entry, candidate, weChatAccounts);
                ApplySavedAccountOrder();

                entry.IsAttached = WindowHost.AttachWindow(candidate.Handle, makeActive: false);
                entry.Status = entry.IsAttached ? "已嵌入" : "接入失败";
                TryCompletePendingLaunch(candidate, entry);
            }

            if (SelectedEntry is not null
                && SelectedEntry.Handle != IntPtr.Zero
                && !NativeMethods.IsWindow(SelectedEntry.Handle))
            {
                ShowSelectedEntry();
            }

            UpdateWindowCount();
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private ClientWindowEntry CreateEntry(WindowCandidate candidate)
    {
        string displayName;
        if (candidate.Kind == ClientKind.WeChat)
        {
            _wechatNumber++;
            displayName = $"微信 {_wechatNumber}";
        }
        else
        {
            displayName = "WhatsApp";
        }

        return new ClientWindowEntry(
            candidate.Handle,
            candidate.ProcessId,
            candidate.ProcessName,
            candidate.WindowTitle,
            candidate.Kind,
            displayName,
            candidate.ClientVersion,
            MapSessionState(candidate.Role));
    }

    private async void NewWeChat_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode || !TryBeginClientLaunch())
        {
            if (_isPreviewMode)
            {
                ShowTemporaryStatus("预览模式不会启动本地客户端。");
            }
            return;
        }

        try
        {
            ShowTemporaryStatus("正在准备微信客户端…");
            await Dispatcher.Yield(DispatcherPriority.Input);

            // Process/registry probing can be slow on machines with many client
            // processes. Keep it off the UI thread so the click feedback is instant.
            var executablePath = await Task.Run(() =>
                ClientLauncher.FindWeChatExecutable(_settings.WeChatExecutablePath));
            if (executablePath is null)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择微信客户端",
                    Filter = "微信客户端 (Weixin.exe;WeChat.exe)|Weixin.exe;WeChat.exe|可执行文件 (*.exe)|*.exe",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(this) != true)
                {
                    ShowTemporaryStatus("未选择微信客户端。");
                    return;
                }

                executablePath = dialog.FileName;
                _settings.WeChatExecutablePath = executablePath;
                _settings.Save();
            }

            _dismissedWindowHandles.Clear();
            _pendingLaunchContext = await CaptureLaunchContextAsync(ClientKind.WeChat);
            ClientMetadataService.InvalidateLoginAccountCache();
            _selectNextKind = ClientKind.WeChat;

            var launchResult = await Task.Run(() => ClientLauncher.StartWeChat(executablePath));
            if (_pendingLaunchContext is not null
                && launchResult.ProcessId is { } createdProcessId
                && (launchResult.UsedMultiInstanceBypass
                    || launchResult.Strategy.Equals("CreateProcessW-Normal", StringComparison.Ordinal)))
            {
                // EV 式多开或首个正常实例由 CreateProcessW 直接返回权威 PID；
                // 后续窗口扫描只接受该 PID，避免多微信环境下认错实例。
                _pendingLaunchContext.ExpectProcess(createdProcessId);
            }

            ShowTemporaryStatus(launchResult.UsedMultiInstanceBypass
                ? $"微信多开进程已启动（PID {launchResult.ProcessId}），正在等待窗口…"
                : "微信已启动，正在等待新窗口…");
            await WaitForExpectedWindowAsync(ClientKind.WeChat);
        }
        catch (Exception exception)
        {
            ClearPendingLaunch(ClientKind.WeChat);
            ShowOwnedMessageBox($"无法启动微信：\n{exception.Message}", "JNest",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndClientLaunch();
        }
    }

    private async void AddWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode || !TryBeginClientLaunch())
        {
            if (_isPreviewMode)
            {
                ShowTemporaryStatus("预览模式不会启动本地客户端。");
            }
            return;
        }

        try
        {
            var existing = Entries.FirstOrDefault(entry =>
                entry.Kind == ClientKind.WhatsApp
                && entry.Handle != IntPtr.Zero
                && NativeMethods.IsWindow(entry.Handle));
            if (existing is not null)
            {
                SelectedEntry = existing;
                AccountsList.SelectedItem = existing;
                ShowTemporaryStatus("WhatsApp 已经在管理器中。");
                return;
            }

            ShowTemporaryStatus("正在准备 WhatsApp 客户端…");
            await Dispatcher.Yield(DispatcherPriority.Input);
            _dismissedWindowHandles.Clear();
            _pendingLaunchContext = await CaptureLaunchContextAsync(ClientKind.WhatsApp);
            _selectNextKind = ClientKind.WhatsApp;
            await Task.Run(ClientLauncher.StartWhatsApp);
            ShowTemporaryStatus("WhatsApp 已启动，正在等待窗口…");
            await WaitForExpectedWindowAsync(ClientKind.WhatsApp);
        }
        catch (Exception exception)
        {
            ClearPendingLaunch(ClientKind.WhatsApp);
            ShowOwnedMessageBox($"无法启动 WhatsApp：\n{exception.Message}", "JNest",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndClientLaunch();
        }
    }

    private bool TryBeginClientLaunch()
    {
        if (_isClosing || _isClientLaunchInProgress)
        {
            return false;
        }

        _isClientLaunchInProgress = true;
        AddWeChatButton.IsEnabled = false;
        AddWhatsAppMenuItem.IsEnabled = false;
        return true;
    }

    private void EndClientLaunch()
    {
        _isClientLaunchInProgress = false;
        AddWeChatButton.IsEnabled = true;
        AddWhatsAppMenuItem.IsEnabled = true;
    }

    private async Task WaitForExpectedWindowAsync(ClientKind kind)
    {
        for (var attempt = 0; attempt < 30 && _selectNextKind == kind; attempt++)
        {
            await Task.Delay(600);
            await ScanAndAttachAsync();
        }

        if (_selectNextKind == kind)
        {
            ClearPendingLaunch(kind);
            ShowTemporaryStatus("客户端可能仍在启动；窗口出现后会自动接入。");
        }
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsList.SelectedItem is ClientWindowEntry entry)
        {
            SelectedEntry = entry;
        }
    }

    private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _accountDragEntry = null;
        _selectionBeforeAccountDrag = SelectedEntry;
        _accountDragStartPoint = e.GetPosition(AccountsList);

        var source = e.OriginalSource as DependencyObject;
        if (source is null || IsAccountDragInteractiveElement(source))
        {
            return;
        }

        var item = ItemsControl.ContainerFromElement(AccountsList, source) as ListBoxItem;
        _accountDragEntry = item?.DataContext as ClientWindowEntry;
    }

    private void AccountsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _accountDragEntry is null)
        {
            return;
        }

        var point = e.GetPosition(AccountsList);
        if (Math.Abs(point.X - _accountDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _accountDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _accountDragEntry;
        try
        {
            DragDrop.DoDragDrop(AccountsList, dragged, DragDropEffects.Move);
        }
        finally
        {
            _accountDragEntry = null;
            if (_selectionBeforeAccountDrag is not null && Entries.Contains(_selectionBeforeAccountDrag))
            {
                AccountsList.SelectedItem = _selectionBeforeAccountDrag;
                SelectedEntry = _selectionBeforeAccountDrag;
            }
            _selectionBeforeAccountDrag = null;
        }
    }

    private void AccountsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ClientWindowEntry))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AccountsList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ClientWindowEntry))
            || e.Data.GetData(typeof(ClientWindowEntry)) is not ClientWindowEntry dragged
            || !Entries.Contains(dragged))
        {
            return;
        }

        var oldIndex = Entries.IndexOf(dragged);
        var source = e.OriginalSource as DependencyObject;
        var targetContainer = source is null
            ? null
            : ItemsControl.ContainerFromElement(AccountsList, source) as ListBoxItem;

        var insertionIndex = Entries.Count;
        if (targetContainer?.DataContext is ClientWindowEntry target && Entries.Contains(target))
        {
            var targetIndex = Entries.IndexOf(target);
            var point = e.GetPosition(targetContainer);
            insertionIndex = targetIndex + (point.Y > targetContainer.ActualHeight / 2 ? 1 : 0);
        }

        // Removing the old item shifts every later insertion point one slot left.
        if (oldIndex < insertionIndex)
        {
            insertionIndex--;
        }

        insertionIndex = Math.Clamp(insertionIndex, 0, Math.Max(0, Entries.Count - 1));
        if (oldIndex != insertionIndex)
        {
            Entries.Move(oldIndex, insertionIndex);
            SaveAccountOrder();
            ShowTemporaryStatus("账号顺序已保存。");
        }

        if (_selectionBeforeAccountDrag is not null && Entries.Contains(_selectionBeforeAccountDrag))
        {
            AccountsList.SelectedItem = _selectionBeforeAccountDrag;
            SelectedEntry = _selectionBeforeAccountDrag;
        }

        e.Handled = true;
    }

    private static bool IsAccountDragInteractiveElement(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            if (source is ButtonBase or MenuItem or TextBoxBase or ScrollBar)
            {
                return true;
            }

            source = source is Visual || source is Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private string? GetAccountOrderKey(ClientWindowEntry entry)
    {
        if (entry.Kind == ClientKind.WhatsApp)
        {
            return "whatsapp";
        }

        if (!string.IsNullOrWhiteSpace(entry.IdentityKey))
        {
            return "wechat:" + entry.IdentityKey;
        }

        if (!string.IsNullOrWhiteSpace(entry.AccountId))
        {
            return "wechat-account:" + entry.AccountId;
        }

        return null;
    }

    private void SaveAccountOrder()
    {
        if (_isPreviewMode)
        {
            return;
        }

        var visibleKeys = Entries
            .Select(GetAccountOrderKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Keep stable identities that are temporarily offline. A drag operation should
        // not erase another account's saved position merely because that client is not
        // running in this session.
        var known = new HashSet<string>(visibleKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var savedKey in _settings.AccountOrder)
        {
            if (!string.IsNullOrWhiteSpace(savedKey) && known.Add(savedKey))
            {
                visibleKeys.Add(savedKey);
            }
        }

        _settings.AccountOrder = visibleKeys;
        _settings.Save();
    }

    private void ApplySavedAccountOrder()
    {
        if (_settings.AccountOrder.Count == 0 || Entries.Count < 2)
        {
            return;
        }

        var rank = _settings.AccountOrder
            .Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);

        var desired = Entries
            .Select((entry, originalIndex) =>
            {
                var key = GetAccountOrderKey(entry);
                var order = key is not null && rank.TryGetValue(key, out var savedIndex)
                    ? savedIndex
                    : int.MaxValue;
                return (entry, order, originalIndex);
            })
            .OrderBy(item => item.order)
            .ThenBy(item => item.originalIndex)
            .Select(item => item.entry)
            .ToArray();

        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var currentIndex = Entries.IndexOf(desired[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Entries.Move(currentIndex, targetIndex);
            }
        }
    }

    private string BuildCurrentStatusText(ClientWindowEntry entry, string baseText)
    {
        if (entry.Kind != ClientKind.WeChat
            || entry.ProfileState == AccountProfileState.NotApplicable
            || string.IsNullOrWhiteSpace(entry.ProfileStatusText))
        {
            return baseText;
        }

        return $"{baseText} · {entry.ProfileStatusText}";
    }

    private void RefreshSelectedProfileStatus(ClientWindowEntry entry)
    {
        if (_isPreviewMode
            || !ReferenceEquals(entry, SelectedEntry)
            || entry.Handle == IntPtr.Zero
            || !NativeMethods.IsWindow(entry.Handle))
        {
            return;
        }

        CurrentStatusText.Text = entry.IsAttached
            ? BuildCurrentStatusText(entry, "已嵌入 · 运行中")
            : BuildCurrentStatusText(entry, "当前为独立窗口");
    }

    private void ShowSelectedEntry()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            SetEmptyState("还没有接入客户端窗口", "点击顶部“添加微信”或“添加 WhatsApp”，程序会自动发现并嵌入本地客户端。");
            CurrentTitleText.Text = "等待客户端";
            CurrentStatusText.Text = "尚未接入";
            CurrentStatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            PopOutButton.IsEnabled = false;
            return;
        }

        CurrentTitleText.Text = entry.DisplayName;
        CurrentStatusDot.Fill = entry.StatusBrush;

        if (_isPreviewMode || entry.Handle == IntPtr.Zero)
        {
            CurrentStatusText.Text = "界面预览";
            PopOutButton.IsEnabled = false;
            SetEmptyState(
                $"{entry.DisplayName} · 预览",
                "正式运行时，这里会显示被嵌入的本地客户端窗口。预览模式不会读取或控制任何客户端。");
            return;
        }

        if (!NativeMethods.IsWindow(entry.Handle))
        {
            CurrentStatusText.Text = "窗口已关闭";
            PopOutButton.IsEnabled = false;
            SetEmptyState("客户端窗口已关闭", "重新打开客户端后，JNest会尝试把它接回原来的位置。");
            return;
        }

        PopOutButton.IsEnabled = true;
        if (!entry.IsAttached)
        {
            CurrentStatusText.Text = BuildCurrentStatusText(entry, "当前为独立窗口");
            PopOutButtonText.Text = "嵌入窗口";
            WindowHost.HideAll();
            SetEmptyState("窗口已弹出到桌面", "点击顶部“嵌入窗口”即可把它放回JNest。");
            return;
        }

        CurrentStatusText.Text = BuildCurrentStatusText(entry, "已嵌入 · 运行中");
        PopOutButtonText.Text = "弹出窗口";
        EmptyState.Visibility = Visibility.Collapsed;
        WindowHost.Visibility = Visibility.Visible;
        WindowHost.ShowOnly(entry.Handle);
        QueueHostedWindowRefresh(entry);
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry is null || entry.Handle == IntPtr.Zero || !NativeMethods.IsWindow(entry.Handle))
        {
            return;
        }

        if (entry.IsAttached)
        {
            // 明确记录“用户主动弹出”，后台 5 秒扫描不得自动重新接入。
            _dismissedWindowHandles.Add(entry.Handle);
            entry.IsManuallyDetached = true;
            WindowHost.DetachWindow(entry.Handle);
            entry.IsAttached = false;
            entry.Status = "独立窗口";
            DiagnosticLogService.WriteAccountEvent(entry, "manual-detach");
            ShowTemporaryStatus($"{entry.DisplayName} 已弹出到桌面。");
        }
        else
        {
            // 用户明确要求重新嵌入时解除保护；若接入失败，后续后台扫描可以继续低频重试。
            _dismissedWindowHandles.Remove(entry.Handle);
            entry.IsManuallyDetached = false;
            entry.IsAttached = WindowHost.AttachWindow(entry.Handle, makeActive: true);
            entry.Status = entry.IsAttached ? "已嵌入" : "接入失败";
            DiagnosticLogService.WriteAccountEvent(
                entry,
                entry.IsAttached ? "manual-attach" : "manual-attach-failed");
            ShowTemporaryStatus(entry.IsAttached
                ? $"{entry.DisplayName} 已重新嵌入。"
                : "窗口接入失败，JNest会继续后台重试，也可使用“全部接入”。",
                entry.IsAttached ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(12));
        }

        UpdateWindowCount();
        ShowSelectedEntry();
    }

    private async void ReconnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode)
        {
            return;
        }

        // “全部接入”是用户显式请求把容器外所有受支持的客户端都接入，
        // 包括之前从列表移除的窗口。
        ShowTemporaryStatus("正在接入容器外的客户端窗口…");
        var attachedCount = await ScanAndReattachAsync(clearDismissed: true);
        ShowTemporaryStatus(attachedCount > 0
            ? "接入检查完成"
            : "未发现可接入的客户端窗口");
    }

    private async void ScanClients_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode)
        {
            return;
        }

        ShowTemporaryStatus("正在扫描并接入本地客户端…");
        var attachedCount = await ScanAndReattachAsync(clearDismissed: false);
        ShowTemporaryStatus(attachedCount > 0
            ? "扫描完成"
            : "未发现新的客户端窗口");
    }

    /// <summary>
    /// 扫描本地客户端并把容器外的窗口接入。区别于后台定时扫描（只做被动发现、
    /// 不打扰用户主动弹出的窗口），这里会显式重试所有未接入的窗口，用于修复
    /// 启动时因宿主未就绪而导致的“接入失败”状态。
    /// </summary>
    private async Task<int> ScanAndReattachAsync(bool clearDismissed)
    {
        if (clearDismissed)
        {
            _dismissedWindowHandles.Clear();
            foreach (var entry in Entries)
            {
                entry.IsManuallyDetached = false;
            }
        }

        await ScanAndAttachAsync();

        var attachedCount = 0;
        foreach (var entry in Entries.Where(entry =>
                     entry.Handle != IntPtr.Zero && NativeMethods.IsWindow(entry.Handle)))
        {
            // “扫描客户端/全部接入”是用户显式重新接入动作，因此解除账号级弹出保护。
            entry.IsManuallyDetached = false;

            if (entry.IsAttached)
            {
                // 已接入的窗口跳过：避免 AttachWindow(makeActive:false) 把正在显示的
                // 窗口停靠到屏幕外，造成闪烁/窗口消失。
                attachedCount++;
                continue;
            }

            entry.IsAttached = WindowHost.AttachWindow(entry.Handle, makeActive: false);
            entry.Status = entry.IsAttached ? "已嵌入" : "接入失败";
            if (entry.IsAttached)
            {
                attachedCount++;
            }
        }

        if (SelectedEntry is null || !SelectedEntry.IsAttached)
        {
            SelectedEntry = Entries.FirstOrDefault(entry => entry.IsAttached);
            AccountsList.SelectedItem = SelectedEntry;
        }

        UpdateWindowCount();
        if (SelectedEntry is not null)
        {
            ShowSelectedEntry();
        }

        return attachedCount;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        WindowHost.SuspendOverlayZOrder();
        try
        {
            new AboutDialog { Owner = this }.ShowDialog();
        }
        finally
        {
            WindowHost.ResumeOverlayZOrder();
        }
    }

    private MessageBoxResult ShowOwnedMessageBox(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        WindowHost.SuspendForModalDialog();
        try
        {
            return MessageBox.Show(this, message, caption, buttons, image, defaultResult);
        }
        finally
        {
            WindowHost.ResumeAfterModalDialog();
        }
    }

    private void SetEmptyState(string title, string description)
    {
        _renderRefreshGeneration++;
        WindowHost.HideAll();
        WindowHost.Visibility = Visibility.Collapsed;
        EmptyStateTitle.Text = title;
        EmptyStateDescription.Text = description;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void QueueHostedWindowRefresh(ClientWindowEntry entry)
    {
        var generation = ++_renderRefreshGeneration;
        _ = RefreshHostedWindowAsync(entry, generation);
    }

    private async Task RefreshHostedWindowAsync(ClientWindowEntry entry, int generation)
    {
        // Let WPF complete layout, then perform one delayed native refresh. More
        // synchronous redraws noticeably stall the UI without improving rendering.
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_isClosing
                || generation != _renderRefreshGeneration
                || !ReferenceEquals(entry, SelectedEntry)
                || !entry.IsAttached
                || !NativeMethods.IsWindow(entry.Handle))
            {
                return;
            }

            WindowHost.RefreshHostedWindow(entry.Handle);
            if (attempt == 0)
            {
                await Task.Delay(80);
            }
        }
    }

    private void UpdateWindowCount()
    {
        var online = Entries.Count(entry =>
            entry.Handle != IntPtr.Zero && NativeMethods.IsWindow(entry.Handle));
        WindowCountText.Text = $"已连接 {online} 个窗口";
    }

    private void LoadPreviewEntries()
    {
        var firstWeChat = CreatePreviewEntry(ClientKind.WeChat, "示例账号 A", "4.1.13.12");
        firstWeChat.MessageAlertCount = 1;
        firstWeChat.AlertDisplayMode = AlertDisplayMode.Count;
        Entries.Add(firstWeChat);
        Entries.Add(CreatePreviewEntry(ClientKind.WeChat, "示例账号 B", "4.1.13.12"));
        Entries.Add(CreatePreviewEntry(ClientKind.WhatsApp, "WhatsApp"));
        WindowCountText.Text = "已连接 3 个窗口";
        AccountsList.SelectedIndex = 1;
    }

    private static ClientWindowEntry CreatePreviewEntry(
        ClientKind kind,
        string name,
        string? clientVersion = null)
        => new(
            IntPtr.Zero,
            0,
            string.Empty,
            string.Empty,
            kind,
            name,
            clientVersion,
            ClientSessionState.Main)
        {
            Status = "已嵌入",
            IsOnline = true,
            IsAttached = true
        };

    private void ApplyWeChatAccountIdentity(ClientWindowEntry entry, WeChatAccountIdentity account)
    {
        var previousIdentity = entry.IdentityKey;
        var identityChanged = !string.Equals(previousIdentity, account.Key, StringComparison.OrdinalIgnoreCase);
        entry.IdentityKey = account.Key;
        entry.AccountId = account.AccountId;

        // 同一 PID 退出旧账号再登录新账号时，HWND/进程可能复用。
        // 身份已经变化就先清掉旧头像，避免新资料尚未解析完成时显示上一个账号。
        if (identityChanged && previousIdentity is not null)
        {
            entry.AvatarPath = null;
            entry.MessageAlertCount = 0;
            entry.AlertDisplayMode = AlertDisplayMode.None;
            entry.PendingClearAfterDetection = false;
            _queuedImmediateBadgeScans.Remove(entry);
            _badgeStableState.Remove(entry);
        }

        if (_settings.AccountNamesByIdentity.TryGetValue(account.Key, out var savedName)
            && !string.IsNullOrWhiteSpace(savedName))
        {
            entry.DisplayName = savedName;
            entry.HasCustomName = true;
        }
        else if (identityChanged)
        {
            if (entry.HasCustomName && previousIdentity is null)
            {
                _settings.AccountNamesByIdentity[account.Key] = entry.DisplayName;
                _settings.Save();
            }
            else
            {
            // A recycled window can previously have displayed another account.
            // Always reset its name when the resolved identity changes so a stale
            // nickname never leaks into the next login slot.
                entry.HasCustomName = false;
                entry.DisplayName = account.DisplayName;
            }
        }
        else if (!entry.HasCustomName)
        {
            entry.DisplayName = account.DisplayName;
        }

        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
        }

        ApplySavedAccountOrder();

        if (_profilesByIdentity.TryGetValue(account.Key, out var cachedProfile))
        {
            ApplyWeChatProfile(entry, account, cachedProfile);
        }
        else if (_profileCache.TryGet(account.Key, account.AccountId, out var persistedProfile))
        {
            // 磁盘缓存仅作为“上次已确认资料”的快速回退。后台仍会继续读取
            // 当前微信本地资料，成功后用最新结果刷新缓存。
            _profilesByIdentity[account.Key] = persistedProfile;
            ApplyWeChatProfile(entry, account, persistedProfile);
            entry.ProfileState = AccountProfileState.ReadyFromCache;
            entry.ProfileDiagnosticCode = "ProfileCache";
            entry.ProfileDiagnosticDetail = "当前显示的是JNest上一次确认过的本地账号资料，后台会继续尝试读取最新资料。";
            RefreshSelectedProfileStatus(entry);
            DiagnosticLogService.WriteAccountEvent(entry, "profile-cache-restored");
        }
    }

    private static AccountProfileState MapProfileFailureState(WeChatProfileFailureStage stage)
        => stage switch
        {
            WeChatProfileFailureStage.AccountDirectoryNotFound
                or WeChatProfileFailureStage.ContactDatabaseUnreadable
                or WeChatProfileFailureStage.SelfRowMissing
                => AccountProfileState.WaitingForLocalData,

            WeChatProfileFailureStage.KeyExtractionFailed
                or WeChatProfileFailureStage.ContactTableMissing
                => AccountProfileState.CompatibilityIssue,

            WeChatProfileFailureStage.AvatarCacheMissing
                => AccountProfileState.ReadyWithoutAvatar,

            WeChatProfileFailureStage.DatabaseOpenFailed
                or WeChatProfileFailureStage.WalMergeInconsistent
                => AccountProfileState.Error,

            _ => AccountProfileState.Pending
        };

    private static string GetProfileDiagnosticLabel(WeChatProfileFailureStage stage)
        => stage switch
        {
            WeChatProfileFailureStage.AccountDirectoryNotFound => "AccountDirectoryNotFound",
            WeChatProfileFailureStage.ContactDatabaseUnreadable => "ContactDatabaseUnreadable",
            WeChatProfileFailureStage.KeyExtractionFailed => "KeyExtractionFailed",
            WeChatProfileFailureStage.DatabaseOpenFailed => "DatabaseOpenFailed",
            WeChatProfileFailureStage.ContactTableMissing => "ContactTableMissing",
            WeChatProfileFailureStage.SelfRowMissing => "SelfRowMissing",
            WeChatProfileFailureStage.AvatarCacheMissing => "AvatarCacheMissing",
            WeChatProfileFailureStage.WalMergeInconsistent => "WalMergeInconsistent",
            _ => string.Empty
        };

    private void ApplyProfileDiagnostic(
        ClientWindowEntry entry,
        WeChatProfileFailureStage stage,
        string? detail)
    {
        entry.ProfileState = MapProfileFailureState(stage);
        entry.ProfileDiagnosticCode = stage == WeChatProfileFailureStage.None
            ? null
            : GetProfileDiagnosticLabel(stage);
        entry.ProfileDiagnosticDetail = detail;
        RefreshSelectedProfileStatus(entry);
        DiagnosticLogService.WriteAccountEvent(entry, "profile-diagnostic");
    }

    private bool IsCurrentAccountResolution(
        ClientWindowEntry entry, int processId, int accountGeneration,
        IntPtr windowHandle, long? processStartTicks, long requestToken)
        => !_isClosing
           && Entries.Contains(entry)
           && entry.ProcessId == processId
           && entry.AccountGeneration == accountGeneration
           && entry.Handle == windowHandle
           && (!processStartTicks.HasValue
               || TryGetProcessStartUtcTicks(processId) == processStartTicks)
           && NativeMethods.IsWindow(windowHandle)
           && _accountResolutionLoads.TryGetValue(processId, out var activeToken)
           && activeToken == requestToken;

    private async void QueueWeChatAccountResolution(
        ClientWindowEntry entry,
        WeChatAccountIdentity? preferredAccount)
    {
        var processId = entry.ProcessId;
        var accountGeneration = entry.AccountGeneration;
        var windowHandle = entry.Handle;
        var processStartTicks = TryGetProcessStartUtcTicks(processId);
        if (_resolvedWeChatAccounts.TryGetValue(processId, out var resolvedAccount))
        {
            // The discovery path validates process lifetime before scheduling this read.
            ApplyWeChatAccountIdentity(entry, resolvedAccount);
            return;
        }

        if (_accountResolutionLoads.ContainsKey(processId)
            || (_accountResolutionRetryAfter.TryGetValue(processId, out var retryAfter)
                && retryAfter > DateTime.UtcNow))
        {
            return;
        }

        var resolutionToken = ++_accountResolutionSequence;
        _accountResolutionLoads[processId] = resolutionToken;
        if (entry.ProfileState is not AccountProfileState.Ready
            and not AccountProfileState.ReadyFromCache
            and not AccountProfileState.ReadyWithoutAvatar)
        {
            entry.ProfileState = AccountProfileState.Pending;
            entry.ProfileDiagnosticCode = null;
            entry.ProfileDiagnosticDetail = null;
        }

        try
        {
            await Task.Delay(300);
            if (!IsCurrentAccountResolution(entry, processId, accountGeneration,
                    windowHandle, processStartTicks, resolutionToken))
            {
                return;
            }

            await _profileReadLock.WaitAsync();
            (WeChatAccountIdentity? Account, WeChatProfileReadResult Result) resolution;
            try
            {
                if (!IsCurrentAccountResolution(entry, processId, accountGeneration,
                        windowHandle, processStartTicks, resolutionToken))
                {
                    return;
                }

                var unavailableAccountKeys = _resolvedWeChatAccounts
                    .Where(pair => pair.Key != processId)
                    .Select(pair => pair.Value.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                resolution = await Task.Run(() =>
                {
                    var candidates = ClientMetadataService.RankWeChatAccountsForProcess(processId)
                        .OrderBy(account => preferredAccount is not null
                            && account.Key.Equals(preferredAccount.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenByDescending(account => account.LastLoginUtc)
                        .ThenBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                        .Where(account => !unavailableAccountKeys.Contains(account.Key))
                        .ToArray();

                    var readResult = WeChatProfileService.TryReadMatchingProfileDetailed(
                        processId,
                        candidates.Select(account => account.AccountId));
                    var profile = readResult.Profile;
                    var account = profile is null
                        ? null
                        : candidates.FirstOrDefault(candidate =>
                            candidate.AccountId.Equals(
                                profile.AccountId,
                                StringComparison.OrdinalIgnoreCase));
                    return (Account: account, Result: readResult);
                });
            }
            finally
            {
                _profileReadLock.Release();
            }

            if (resolution.Result.Profile is null || resolution.Account is null)
            {
                if (IsCurrentAccountResolution(entry, processId, accountGeneration,
                        windowHandle, processStartTicks, resolutionToken))
                {
                    ApplyProfileDiagnostic(
                        entry,
                        resolution.Result.Stage,
                        resolution.Result.Detail);
                    ScheduleAccountResolutionRetry(processId);
                }
                return;
            }

            if (!IsCurrentAccountResolution(entry, processId, accountGeneration,
                    windowHandle, processStartTicks, resolutionToken))
            {
                return;
            }

            var account = resolution.Account;
            var profile = resolution.Result.Profile;
            _resolvedWeChatAccounts[processId] = account;
            if (processStartTicks.HasValue)
                _resolvedAccountProcessStarts[processId] = processStartTicks.Value;
            else
                _resolvedAccountProcessStarts.Remove(processId);

            // 把已经从微信本地资料成功读取到的昵称/头像稳定到JNest自己的缓存。
            // 如果微信本轮没有头像缓存，Store 会保留同一账号上一次有效头像。
            var stableProfile = _profileCache.Store(account.Key, profile);
            _profilesByIdentity[account.Key] = stableProfile;

            if (resolution.Result.Stage == WeChatProfileFailureStage.AvatarCacheMissing)
            {
                entry.ProfileState = stableProfile.AvatarPath is not null
                    ? AccountProfileState.ReadyFromCache
                    : AccountProfileState.ReadyWithoutAvatar;
                entry.ProfileDiagnosticCode = GetProfileDiagnosticLabel(resolution.Result.Stage);
                entry.ProfileDiagnosticDetail = stableProfile.AvatarPath is not null
                    ? "微信当前头像缓存暂不可用，已继续使用JNest上一次确认过的头像。"
                    : resolution.Result.Detail;
            }
            else
            {
                entry.ProfileState = AccountProfileState.Ready;
                entry.ProfileDiagnosticCode = null;
                entry.ProfileDiagnosticDetail = resolution.Result.Detail;
            }

            _accountResolutionRetryAfter.Remove(processId);
            _accountResolutionFailures.Remove(processId);
            ApplyWeChatAccountIdentity(entry, account);
            RefreshSelectedProfileStatus(entry);
            DiagnosticLogService.WriteAccountEvent(entry, "profile-live-resolved");
        }
        catch
        {
            if (IsCurrentAccountResolution(entry, processId, accountGeneration,
                    windowHandle, processStartTicks, resolutionToken))
            {
                ScheduleAccountResolutionRetry(processId);
            }
        }
        finally
        {
            // A previous request must not clear a newer request for a reused PID.
            if (_accountResolutionLoads.TryGetValue(processId, out var currentToken)
                && currentToken == resolutionToken)
            {
                _accountResolutionLoads.Remove(processId);
            }
        }
    }

    private void ScheduleAccountResolutionRetry(int processId)
    {
        var failures = _accountResolutionFailures.TryGetValue(processId, out var current)
            ? current + 1
            : 1;
        _accountResolutionFailures[processId] = failures;

        // 新登录时 xwechat\login 下的 key_info.dat 可能在第一次扫描之后才出现。
        // 失败后主动清掉 15 秒目录缓存，让下一轮快速重试看到最新账号集合。
        ClientMetadataService.InvalidateLoginAccountCache();

        // 新登录账号的 contact.db/self row/头像缓存常常比主窗口晚几秒落盘。
        // 前几次快速重试用于补齐刚启动资料，随后逐步退避，避免长期高频扫描进程内存。
        var delay = failures switch
        {
            1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(15),
            3 => TimeSpan.FromMinutes(1),
            4 => TimeSpan.FromMinutes(3),
            5 => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(30)
        };
        _accountResolutionRetryAfter[processId] = DateTime.UtcNow.Add(delay);
    }

    private void ApplyWeChatProfile(
        ClientWindowEntry entry,
        WeChatAccountIdentity account,
        WeChatLocalProfile profile)
    {
        if (!string.Equals(entry.IdentityKey, account.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!entry.HasCustomName && !string.IsNullOrWhiteSpace(profile.Nickname))
        {
            entry.DisplayName = profile.Nickname;
        }
        entry.AvatarPath = profile.AvatarPath;

        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
            RefreshSelectedProfileStatus(entry);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClientWindowEntry entry })
        {
            return;
        }

        try
        {
            Clipboard.SetText(DiagnosticLogService.BuildClipboardReport(entry));
            DiagnosticLogService.WriteAccountEvent(entry, "diagnostic-copied");
            ShowTemporaryStatus($"{entry.DisplayName} 的诊断信息已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            StatusMessageText.Text = $"复制诊断信息失败：{exception.Message}";
        }
    }

    private void RenameEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ClientWindowEntry entry })
        {
            RenameAccount(entry);
        }
    }

    private void RenameAccount(ClientWindowEntry entry)
    {
        var dialog = new RenameAccountDialog(entry.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        entry.DisplayName = dialog.AccountName;
        entry.HasCustomName = true;
        if (!string.IsNullOrWhiteSpace(entry.IdentityKey))
        {
            _settings.AccountNamesByIdentity[entry.IdentityKey] = entry.DisplayName;
            _settings.Save();
        }

        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
        }

        ShowTemporaryStatus($"账号名称已更新为“{entry.DisplayName}”。");
    }

    private void CloseEntry_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var entry = sender switch
        {
            Button { CommandParameter: ClientWindowEntry buttonEntry } => buttonEntry,
            MenuItem { CommandParameter: ClientWindowEntry menuEntry } => menuEntry,
            FrameworkElement { DataContext: ClientWindowEntry dataEntry } => dataEntry,
            _ => null
        };

        if (entry is null)
        {
            return;
        }

        if (_isPreviewMode)
        {
            ShowTemporaryStatus("预览模式不会关闭本地客户端。");
            return;
        }

        if (entry.Handle == IntPtr.Zero || !NativeMethods.IsWindow(entry.Handle))
        {
            RemoveEntry(entry);
            return;
        }

        var answer = ShowOwnedMessageBox(
            $"确定关闭“{entry.DisplayName}”的客户端窗口吗？\n\n这不会关闭JNest，也不会影响其他账号。",
            "关闭客户端",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var handle = entry.Handle;
        _dismissedWindowHandles.Add(handle);
        if (entry.IsAttached)
        {
            WindowHost.DetachWindow(handle, bringToFront: false);
            entry.IsAttached = false;
        }

        var closeRequested = NativeMethods.PostMessage(
            handle,
            NativeMethods.WM_CLOSE,
            IntPtr.Zero,
            IntPtr.Zero);
        var displayName = entry.DisplayName;
        RemoveEntry(entry);
        ShowTemporaryStatus(closeRequested
            ? $"已关闭并移除 {displayName}。"
            : $"已从列表移除 {displayName}；客户端未响应关闭请求。",
            closeRequested ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(12));
    }

    private void ShowClientSelfTestPopup()
    {
        var popup = new Window
        {
            Owner = this,
            Title = "图片查看",
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            Content = new TextBlock
            {
                Text = "图片查看器自检窗口",
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        popup.Show();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || IsInteractiveTitleBarElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isTitleBarInteraction = true;
        try
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                try { DragMove(); }
                catch { /* DragMove may be interrupted if the mouse button is released quickly. */ }
            }
        }
        finally
        {
            _isTitleBarInteraction = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                WindowHost.UpdateOverlayBounds();
            }));
        }
    }

    private static bool IsInteractiveTitleBarElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or MenuItem or TextBoxBase)
            {
                return true;
            }

            source = source is Visual || source is Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

