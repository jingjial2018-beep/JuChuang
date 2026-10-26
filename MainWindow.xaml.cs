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
    // Serialize multi-instance identity reads so occupied account reservations are refreshed
    // after the previous PID commits its verified identity, avoiding concurrent claims.
    private readonly SemaphoreSlim _profileReadLock = new(1, 1);
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
    private bool _exitChoiceApproved;
    private bool _exitAllClients;
    private bool _exitAllCompleted;
    private bool _isPreviewMode;
    private bool _isTitleBarInteraction;
    private bool _isSidebarCollapsed;
    private bool _sidebarCollapsedBeforeMaximize;
    private bool _wasMaximized;
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        private set
        {
            if (_isSidebarCollapsed == value) return;
            _isSidebarCollapsed = value;
            OnPropertyChanged();
        }
    }

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
        SourceInitialized += (_, _) => ApplyTaskbarLargeIcon();
        Closed += (_, _) => { _taskbarLargeIcon?.Dispose(); _titlebarSmallIcon?.Dispose(); };
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
        AppSettings.SaveProblem += OnSettingsSaveProblem;
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _scanTimer.Tick += ScanTimer_Tick;
        _statusResetTimer.Tick += (_, _) => ClearTemporaryStatus();
        _clockTimer.Tick += (_, _) => UpdateLocalClock();
        UpdateLocalClock();
        _clockTimer.Start();
        StateChanged += MainWindow_StateChanged;
        Activated += (_, _) =>
        {
            TriggerForegroundBadgeCalibration();
            if (SelectedEntry is { } entry && WindowState != WindowState.Minimized)
                DismissAccountToast(entry);
        };
        LocationChanged += (_, _) => QueueOverlayBoundsUpdate();
        SizeChanged += (_, _) => QueueOverlayBoundsUpdate();
    }

    private readonly DispatcherTimer _statusResetTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private void UpdateLocalClock()
    {
        var now = DateTimeOffset.Now;
        LocalDateText.Text = now.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
        LocalTimeText.Text = now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        LocalTimeZoneText.Text = now.ToString("dddd", System.Globalization.CultureInfo.CurrentUICulture);
    }
    private string? _pendingStatusText;
    private ClientWindowEntry? _pendingMessageEntry;

    // Middle status text is temporary; the right-hand privacy statement is fixed.
    private void ShowTemporaryStatus(string message, TimeSpan? duration = null,
        ClientWindowEntry? messageEntry = null)
    {
        _statusResetTimer.Stop();
        // Only trim sentence punctuation at the end of UI feedback.
        // Never alter account names, version numbers or embedded periods.
        message = message.TrimEnd().TrimEnd('。');
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
            if (value is not null && value.PendingClearAfterDetection)
                RequestSelectedBadgeCalibration(value);
            if (value is not null && _pendingMessageEntry is not null
                && ReferenceEquals(value, _pendingMessageEntry))
                ClearTemporaryStatus(value);
            if (value is not null && WindowState != WindowState.Minimized
                && (IsActive || WindowHost.IsManagerContextForeground))
                DismissAccountToast(value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReleaseHostedWindowsForShutdown()
    {
        // XAML initialization can fail before WindowHost is assigned.
        WindowHost?.DetachAll();
    }

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

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) return;
        SetSidebarCollapsed(!IsSidebarCollapsed, resizeNormalWindow: true);
    }

    private void SetSidebarCollapsed(bool collapsed, bool resizeNormalWindow)
    {
        if (IsSidebarCollapsed == collapsed) return;
        const double difference = 216; // 280 DIP account list to 64 DIP avatar rail.
        IsSidebarCollapsed = collapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 64 : 280);
        // Expanded avatar center: 42 DIP. Collapsed rail center: 32 DIP.
        // Keep the 30-DIP logo centered on the visible avatar column.
        BrandHeaderPanel.Margin = new Thickness(collapsed ? 17 : 27, 0, 0, 0);
        SidebarHeading.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // No tooltip: it could overflow the window when the sidebar is collapsed.
        SidebarToggleButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggleButton.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        if (resizeNormalWindow && WindowState == WindowState.Normal)
        {
            var delta = collapsed ? -difference : difference;
            Width += delta;
            // Preserve the window's right edge and the hosted client's screen coordinates.
            Left -= delta;
        }
        QueueOverlayBoundsUpdate();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (!_wasMaximized)
                _sidebarCollapsedBeforeMaximize = IsSidebarCollapsed;
            _wasMaximized = true;
            SetSidebarCollapsed(false, resizeNormalWindow: false);
            SidebarToggleButton.IsEnabled = false;
            SidebarToggleButton.Visibility = Visibility.Collapsed;
        }
        else if (WindowState == WindowState.Normal)
        {
            SidebarToggleButton.IsEnabled = true;
            SidebarToggleButton.Visibility = Visibility.Visible;
            if (_wasMaximized)
            {
                _wasMaximized = false;
                SetSidebarCollapsed(_sidebarCollapsedBeforeMaximize, resizeNormalWindow: false);
            }
        }
        UpdateMaximizeGlyph();
        var isMinimized = WindowState == WindowState.Minimized;

        // 最小化时看不到时钟，暂停每秒刷新；恢复时立即补一次。
        if (isMinimized)
        {
            _clockTimer.Stop();
        }
        else if (!_isClosing)
        {
            UpdateLocalClock();
            _clockTimer.Start();
        }

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
                ? "M 7,2 H 11 Q 12,2 12,3 V 8 Q 12,9 11,9 H 10 M 4,5 H 9 Q 10,5 10,6 V 11 Q 10,12 9,12 H 4 Q 3,12 3,11 V 6 Q 3,5 4,5 Z"
                : "M 4,3 H 10 Q 11,3 11,4 V 10 Q 11,11 10,11 H 4 Q 3,11 3,10 V 4 Q 3,3 4,3 Z");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        RestoreSessionLockIfNeeded();
        // Fixed startup size and centered position come from MainWindow.xaml.
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

        if (IsJNestProtected)
        {
            _scanTimer.Start();
            StartBadgePolling();
            return;
        }

        ShowTemporaryStatus("JNest已启动，正在后台发现本地客户端…");
        // 预热窗口托管宿主：HwndHost 在 Visibility=Collapsed 时可能不会调用
        // BuildWindowCore，导致首次 AttachWindow 因 _hostHandle 未创建而失败
        // （IsReady=false）。这里先设为可见并等待布局完成，确保宿主就绪后再扫描。
        // EmptyState 覆盖在其上，视觉上无变化。
        WindowHost.Visibility = Visibility.Visible;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        await TryScanAndAttachAsync();
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        SyncWeChatLoginPrompts();
        // 首次启动时某些 WebView/Store 客户端（尤其 WhatsApp）窗口已经可枚举，
        // 但第一次跨进程托管可能正好撞上客户端自身的布局/恢复过程而失败。
        // 只对“已经发现但尚未接入”的窗口做两次短暂重试，不启动新进程，
        // 不改变已成功接入的窗口，避免用户必须手动点“嵌入窗口”。
        for (var startupRetry = 0;
             startupRetry < 2 && Entries.Any(entry => !entry.IsAttached && NativeMethods.IsWindow(entry.Handle));
             startupRetry++)
        {
            await Task.Delay(350);
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            await TryScanAndAttachAsync();
        }

        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        if (Entries.Count > 0 && SelectedEntry is null)
        {
            SelectedEntry = Entries.FirstOrDefault(entry => entry.IsAttached) ?? Entries[0];
        }

        ShowTemporaryStatus(Entries.Count == 0
            ? "未发现正在运行的客户端，可从顶部新建。"
            : "客户端接入检查完成");
        _scanTimer.Start();
        StartNativeCaptionGuard();
        StartBadgePolling();
        if (_isClientSelfTestMode)
        {
            ShowClientSelfTestPopup();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsJNestProtected && !_trayExitRequested)
        {
            // A normal close cannot bypass the lock. In particular, never
            // call Show/RestoreFromTray from inside the WPF Closing event.
            e.Cancel = true;
            LockPasswordInput.Focus();
            return;
        }
        if (_clientExitInProgress || _allExitInProgress)
        {
            e.Cancel = true;
            ShowTemporaryStatus("客户端正在退出，请稍后再关闭 JNest。");
            return;
        }
        if (ExitClosePolicy.ShouldPromptForChoice(_exitAllCompleted, _exitChoiceApproved,
                WindowHost is not null && _scanTimer is not null))
        {
            ExitChoice choice;
            try { choice = ShowExitChoice(); }
            catch (Exception exception)
            {
                DiagnosticLogService.WriteErrorEvent("shutdown-choice", exception);
                e.Cancel = true;
                _trayExitRequested = false;
                return;
            }
            if (choice == ExitChoice.Cancel)
            {
                e.Cancel = true;
                _trayExitRequested = false;
                return;
            }
            _exitAllClients = choice == ExitChoice.All;
            _exitChoiceApproved = true;
        }

        if (ExitClosePolicy.ShouldBeginExitAll(_exitAllCompleted, _exitAllClients))
        {
            e.Cancel = true;
            _allExitInProgress = true;
            _ = CompleteExitAllAsync();
            return;
        }

        DiagnosticLogService.WriteActivityEvent("shutdown-cleanup-start");
        RestoreWeChatLoginPrompts();
        _isClosing = true;
        // A XAML/constructor startup failure can close this window before its
        // hosted controls and timer fields have been initialized.
        if (WindowHost is null || _scanTimer is null)
            return;
        AppSettings.SaveProblem -= OnSettingsSaveProblem;
        StopNativeCaptionGuard();
        // Fixed window sizing: do not remember prior position, size or maximized state.
        _scanTimer.Stop();
        _statusResetTimer.Stop();
        _clockTimer.Stop();
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

        try
        {
            WindowHost.DetachAll();
            DiagnosticLogService.WriteActivityEvent("shutdown-cleanup-completed");
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent("shutdown-detach", exception);
            throw;
        }
    }

    private async Task CompleteExitAllAsync()
    {
        try
        {
            if (await TryExitAllHostedClientsAsync())
            {
                _exitAllCompleted = true;
                _exitAllClients = false;
                _allExitInProgress = false;
                Close();
            }
            else
            {
                _exitChoiceApproved = false;
                _exitAllClients = false;
                _allExitInProgress = false;
                _trayExitRequested = false;
                await TryScanAndAttachAsync();
            }
        }
        catch (Exception exception)
        {
            ReconnectDiagnostics.Write($"exit all unexpected error={exception.GetType().Name} {exception.Message}");
            _exitChoiceApproved = false;
            _exitAllClients = false;
            _exitAllCompleted = false;
            _trayExitRequested = false;
            ShowOwnedMessageBox("全部退出未完成。JNest 将保持运行，请检查客户端状态。",
                "全部退出未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _allExitInProgress = false;
        }
    }

    private async void ScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isClosing)
        {
            await TryScanAndAttachAsync();
            SyncWeChatLoginPrompts();
        }
    }

    /// <summary>
    /// 后台扫描的兜底入口。定时器、Shell 事件和启动阶段都是 async void 或匿名回调，
    /// 异常一旦逃逸会进入 DispatcherUnhandledException 并让整个程序退出，所有被托管的
    /// 客户端窗口也会被放回桌面。扫描是周期性的，单次失败记录后等下一轮即可。
    /// </summary>
    private async Task TryScanAndAttachAsync()
    {
        if (IsJNestProtected) return;
        // 失败后不再追加“补扫”：ScanAndAttachAsync 在 finally 中释放扫描锁，5 秒周期的
        // 扫描定时器会自然重新发现窗口状态；额外补扫在持续失败时只会让失败次数和负载翻倍。
        await GuardedRunner.RunAsync(
            () => ScanAndAttachAsync(),
            exception => ReportBackgroundFailure(nameof(ScanAndAttachAsync), exception));
    }

    private readonly FailureThrottle _backgroundFailureThrottle = new(TimeSpan.FromSeconds(30));

    private void ReportBackgroundFailure(string source, Exception exception)
    {
        try
        {
            // 同一来源反复失败（例如每 5 秒一次的扫描）时限流，避免日志和状态栏被刷屏。
            if (!_backgroundFailureThrottle.ShouldReport(source, DateTime.UtcNow))
            {
                return;
            }

            DiagnosticLogService.WriteErrorEvent(source, exception);
            if (!_isClosing && !Dispatcher.HasShutdownStarted)
            {
                ShowTemporaryStatus("操作出现异常，已记录诊断日志");
            }
        }
        catch
        {
            // 兜底路径本身不能再抛出异常。
        }
    }

    private bool TrySaveSettings()
    {
        if (_settings.Save()) return true;
        // Do not rely only on the throttled SaveProblem event: every explicit
        // action must observe failure, including a second attempt within a minute.
        ShowTemporaryStatus("设置保存失败，本次修改仅在当前运行中生效；请打开设置重试。",
            TimeSpan.FromSeconds(10));
        return false;
    }

    private void OnSettingsSaveProblem(string message)
    {
        // 保存可能发生在任意调用线程；状态栏只能在 UI 线程更新。
        if (_isClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                ShowTemporaryStatus(message);
            }
        }));
    }

    private void RemoveEntry(ClientWindowEntry entry)
    {
        DiagnosticLogService.WriteAccountEvent(entry, "entry-removed");
        entry.SessionState = ClientSessionState.Exited;

        if (entry.IsAttached && WindowBelongsToProcess(entry.Handle, entry.ProcessId))
        {
            WindowHost.DetachWindow(entry.Handle, bringToFront: false);
        }
        else if (entry.IsAttached)
        {
            WindowHost.ForgetWindow(entry.Handle);
        }

        var wasSelected = ReferenceEquals(SelectedEntry, entry);
        var processId = entry.ProcessId;
        DismissAccountToast(entry);
        _queuedImmediateBadgeScans.Remove(entry);
        _pendingClearRechecks.Remove(entry);
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

    private static bool IsVerifiedClientWindow(ClientWindowEntry entry)
        => WindowBelongsToProcess(entry.Handle, entry.ProcessId)
           && (entry.ProcessStartUtcTicks is not { } expectedStart
               || TryGetProcessStartUtcTicks(entry.ProcessId) == expectedStart);

    private static bool IsVerifiedCandidate(WindowCandidate candidate)
        => WindowBelongsToProcess(candidate.Handle, candidate.ProcessId)
           && (candidate.ProcessStartUtcTicks is not { } expectedStart
               || TryGetProcessStartUtcTicks(candidate.ProcessId) == expectedStart);

    private static bool CanRebindProcess(ClientWindowEntry entry, WindowCandidate candidate)
        => ClientWindowDiscovery.IsSameProcessInstance(entry, candidate)
           && !IsVerifiedClientWindow(entry);

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
        if (candidate.ProcessStartUtcTicks.HasValue)
            entry.ProcessStartUtcTicks = candidate.ProcessStartUtcTicks;
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
        _pendingClearRechecks.Remove(entry);
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

    // A HWND can be recycled or rebound after a WeChat window transition.
    // Do not attach a saved identity to a window that no longer belongs to that PID.
    private static bool WindowBelongsToProcess(IntPtr handle, int processId)
    {
        if (handle == IntPtr.Zero || processId <= 0 || !NativeMethods.IsWindow(handle))
            return false;
        NativeMethods.GetWindowThreadProcessId(handle, out var ownerProcessId);
        return ownerProcessId == (uint)processId;
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

        if (entry.ProcessId != candidate.ProcessId
            || !WindowBelongsToProcess(candidate.Handle, candidate.ProcessId))
        {
            DiagnosticLogService.WriteActivityEvent("wechat-identity-window-owner-mismatch", candidate.ProcessId);
            return;
        }

        // A login-screen transition invalidates the active identity even when
        // WeChat reuses the same PID/HWND. Never reapply an old account to login UI.
        if (candidate.Role == ClientWindowRole.Login)
            return;

        if (_resolvedWeChatAccounts.TryGetValue(candidate.ProcessId, out var resolvedAccount))
        {
            var currentStart = TryGetProcessStartUtcTicks(candidate.ProcessId);
            if (!currentStart.HasValue
                || !_resolvedAccountProcessStarts.TryGetValue(candidate.ProcessId, out var knownStart)
                || knownStart != currentStart.Value)
            {
                ResetWeChatAccountBinding(entry);
            }
            else
            {
                ApplyWeChatAccountIdentity(entry, resolvedAccount);
                return;
            }
        }

        // Match the running process through its live profile rather than binding
        // a candidate solely by its most recently modified login directory.
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

    private async Task ScanAndAttachAsync(bool scanLockHeld = false)
    {
        if (_allExitInProgress) return;
        if (!scanLockHeld && !await _scanLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var scanUtc = DateTime.UtcNow;
            foreach (var entry in Entries.Where(entry => entry.Handle != IntPtr.Zero).ToArray())
            {
                var currentStart = TryGetProcessStartUtcTicks(entry.ProcessId);
                if (entry.ProcessStartUtcTicks.HasValue
                    && currentStart != entry.ProcessStartUtcTicks)
                {
                    entry.IsAttached = false;
                    RemoveEntry(entry);
                    continue;
                }
                var exists = WindowBelongsToProcess(entry.Handle, entry.ProcessId);
                if (!exists && NativeMethods.IsWindow(entry.Handle))
                {
                    entry.IsAttached = false; // HWND was reused by another process.
                    RemoveEntry(entry);
                    continue;
                }
                if (exists)
                {
                    entry.IsOnline = true;
                    entry.LastSeenUtc = scanUtc;
                    entry.WindowMissingSinceUtc = null;
                    if (entry.SessionState == ClientSessionState.Rebinding)
                    {
                        entry.SessionState = ClientSessionState.Main;
                    }

                    entry.Status = entry.IsAttached ? "已接入" : "独立窗口";
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
            var managerOwner = new WindowInteropHelper(this).Handle;
            var managedWindows = CaptureManagedDiscoveryWindows();
            var scanResult = await Task.Run(() =>
            {
                var discoveredCandidates = ClientWindowDiscovery.FindClientWindows(
                        selfTestTargetProcessId, managerOwner, managedWindows)
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

            if (_isClosing || _allExitInProgress || IsJNestProtected)
            {
                return;
            }

            var candidates = scanResult.Candidates
                .Where(candidate => !_dismissedWindowHandles.Contains(candidate.Handle))
                .ToArray();
            var weChatAccounts = scanResult.Accounts;

            foreach (var discoveredCandidate in candidates)
            {
                if (IsJNestProtected) return;
                var candidate = discoveredCandidate;
                var existingEntry = Entries.FirstOrDefault(entry => entry.Handle == candidate.Handle);
                // Current window size is not a reliable login signal after WPF
                // hosting resized a previously confirmed chat window.
                if (existingEntry is not null
                    && candidate.Kind == ClientKind.WeChat
                    && candidate.Role == ClientWindowRole.Login
                    && existingEntry.SessionState == ClientSessionState.Main
                    && !candidate.WindowTitle.Contains("登录", StringComparison.OrdinalIgnoreCase)
                    && !candidate.WindowClass.Contains("WeChatLoginWndForPC", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate with { Role = ClientWindowRole.Main };
                }
                if (existingEntry is not null)
                {
                    if (candidate.Kind == ClientKind.WeChat
                        && existingEntry.SessionState != MapSessionState(candidate.Role))
                        DiagnosticLogService.WriteActivityEvent(
                            $"wechat-role-transition {existingEntry.SessionState}->{candidate.Role} class={candidate.WindowClass} area={candidate.PixelArea}",
                            candidate.ProcessId);
                    var resumedFromLogin = existingEntry.SessionState == ClientSessionState.Login
                        && candidate.Role == ClientWindowRole.Main;
                    PrepareEntryForCandidate(existingEntry, candidate);
                    QueueOrApplyWeChatIdentity(existingEntry, candidate, weChatAccounts);
                    if (resumedFromLogin)
                    {
                        _lastClientActivityScan.Remove(existingEntry);
                        _activityRetryGeneration.Remove(existingEntry);
                        QueueClientActivityScan(existingEntry, "login-to-main");
                        DiagnosticLogService.WriteAccountEvent(existingEntry, "badge-monitor-rearmed");
                    }

                    // 自动扫描只重试“接入失败”的窗口。用户主动“弹出窗口”会把句柄
                    // 放进 _dismissedWindowHandles，因此不会被后台扫描重新抓回容器。
                    if (!existingEntry.IsAttached
                        && !existingEntry.IsManuallyDetached
                        && !_dismissedWindowHandles.Contains(existingEntry.Handle))
                    {
                        existingEntry.IsAttached = WindowHost.AttachWindow(
                            existingEntry.Handle,
                            makeActive: false,
                            disableMaximize: existingEntry.Kind == ClientKind.WhatsApp,
                            centerInHost: candidate.Kind == ClientKind.WeChat && candidate.Role == ClientWindowRole.Login);
                        existingEntry.Status = existingEntry.IsAttached ? "已接入" : "接入失败";
                    }

                    if (existingEntry.IsAttached && candidate.Kind == ClientKind.WeChat)
                        WindowHost.SetCenterInHost(existingEntry.Handle, candidate.Role == ClientWindowRole.Login);
                    TryCompletePendingLaunch(candidate, existingEntry);
                    continue;
                }

                // 同一进程的 HWND 被客户端重建时，重新绑定到原账号 Entry，
                // 不创建第二张账号卡片，也不清空已经解析出的 Identity/Profile。
                var rebindingEntry = Entries.FirstOrDefault(entry =>
                    entry.Kind == candidate.Kind
                    && entry.ProcessId == candidate.ProcessId
                    && entry.ProcessStartUtcTicks.HasValue
                    && entry.ProcessStartUtcTicks == TryGetProcessStartUtcTicks(candidate.ProcessId)
                    && (entry.SessionState == ClientSessionState.Rebinding
                        || entry.Handle == IntPtr.Zero
                        || !NativeMethods.IsWindow(entry.Handle)));

                if (rebindingEntry is not null)
                {
                    var rebindingIntoMain = candidate.Kind == ClientKind.WeChat
                        && candidate.Role == ClientWindowRole.Main;
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
                            makeActive,
                            disableMaximize: rebindingEntry.Kind == ClientKind.WhatsApp,
                            centerInHost: candidate.Kind == ClientKind.WeChat && candidate.Role == ClientWindowRole.Login);
                        rebindingEntry.Status = rebindingEntry.IsAttached ? "已接入" : "接入失败";
                        if (makeActive && rebindingEntry.IsAttached)
                        {
                            QueueHostedWindowRefresh(rebindingEntry);
                        }
                    }

                    if (rebindingIntoMain && rebindingEntry.IsAttached)
                    {
                        _lastClientActivityScan.Remove(rebindingEntry);
                        _activityRetryGeneration.Remove(rebindingEntry);
                        QueueClientActivityScan(rebindingEntry, "window-rebound-main");
                    }
                    DiagnosticLogService.WriteAccountEvent(rebindingEntry, "window-rebound");
                    if (rebindingEntry.IsAttached && candidate.Kind == ClientKind.WeChat)
                        WindowHost.SetCenterInHost(rebindingEntry.Handle, candidate.Role == ClientWindowRole.Login);
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

                if (candidate.Kind == ClientKind.WeChat)
                    DiagnosticLogService.WriteActivityEvent(
                        $"wechat-discovered role={candidate.Role} class={candidate.WindowClass} title={candidate.WindowTitle} area={candidate.PixelArea} center={candidate.Role == ClientWindowRole.Login}",
                        candidate.ProcessId);
                var entry = CreateEntry(candidate);
                Entries.Add(entry);
                QueueOrApplyWeChatIdentity(entry, candidate, weChatAccounts);
                ApplySavedAccountOrder();

                entry.IsAttached = WindowHost.AttachWindow(candidate.Handle, makeActive: false,
                    disableMaximize: entry.Kind == ClientKind.WhatsApp,
                    centerInHost: entry.Kind == ClientKind.WeChat && entry.SessionState == ClientSessionState.Login);
                ReconnectDiagnostics.Write($"attach result kind={entry.Kind} hwnd=0x{entry.Handle.ToInt64():X} attached={entry.IsAttached} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            entry.Status = entry.IsAttached ? "已接入" : "接入失败";
                if (entry.IsAttached && candidate.Kind == ClientKind.WeChat)
                    WindowHost.SetCenterInHost(entry.Handle, candidate.Role == ClientWindowRole.Login);
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
            if (!scanLockHeld) _scanLock.Release();
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
            MapSessionState(candidate.Role))
        {
            ProcessStartUtcTicks = candidate.ProcessStartUtcTicks
        };
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
                TrySaveSettings();
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

    // Right-click is not an account-selection gesture: intercept before
    // ListBoxItem's built-in mouse handling can change SelectedItem.
    private void AccountsList_IgnoreRightClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountSelectionInputPolicy.ShouldSuppress(e.ChangedButton))
            e.Handled = true;
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
            if (SaveAccountOrder())
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

    private bool SaveAccountOrder()
    {
        if (_isPreviewMode)
        {
            return false;
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
        return TrySaveSettings();
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
        if (entry.Kind != ClientKind.WeChat || baseText != "已接入")
            return baseText;
        if (entry.SessionState == ClientSessionState.Login)
            return "已接入 · 等待登录";
        if (entry.SessionState != ClientSessionState.Main
            || entry.ProfileState == AccountProfileState.NotApplicable
            || string.IsNullOrWhiteSpace(entry.ProfileStatusText))
            return baseText;
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
            ? BuildCurrentStatusText(entry, "已接入")
            : BuildCurrentStatusText(entry, "当前为独立窗口");
    }

    private void ShowSelectedEntry()
    {
        if (IsJNestProtected) return;
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

        CurrentStatusText.Text = BuildCurrentStatusText(entry, "已接入");
        PopOutButtonText.Text = "弹出窗口";
        EmptyState.Visibility = Visibility.Collapsed;
        WindowHost.Visibility = Visibility.Visible;
        WindowHost.ShowOnly(entry.Handle);
        SyncWeChatLoginPrompts();
        QueueHostedWindowRefresh(entry);
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry is null || !IsVerifiedClientWindow(entry))
        {
            return;
        }

        if (!entry.IsAttached && entry.ProcessStartUtcTicks is null)
        {
            ShowTemporaryStatus("无法确认客户端进程启动时间，请重新扫描后再接入。", TimeSpan.FromSeconds(8));
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
            entry.IsAttached = WindowHost.AttachWindow(entry.Handle, makeActive: true,
                disableMaximize: entry.Kind == ClientKind.WhatsApp,
                    centerInHost: entry.Kind == ClientKind.WeChat && entry.SessionState == ClientSessionState.Login);
            entry.Status = entry.IsAttached ? "已接入" : "接入失败";
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
        try
        {
            ReconnectDiagnostics.Start();
            ReconnectDiagnostics.Write($"reconnect click foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X} selected=0x{SelectedEntry?.Handle.ToInt64() ?? 0:X}");
            ShowTemporaryStatus("正在接入容器外的客户端窗口…");
            await _scanLock.WaitAsync();
            int attachedCount;
            try
            {
                ReconnectDiagnostics.Write("recover begin");
                attachedCount = await AttachAllDiscoveredClientsAsync();
                ReconnectDiagnostics.Write($"reconnect complete attached={attachedCount} selected=0x{SelectedEntry?.Handle.ToInt64() ?? 0:X} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            }
            finally
            {
                _scanLock.Release();
            }
            ShowTemporaryStatus(attachedCount > 0
                ? "接入检查完成"
                : "未发现可接入的客户端窗口");
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure(nameof(ReconnectAll_Click), exception);
        }
    }

    private async void ScanClients_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode || _allExitInProgress)
        {
            return;
        }

        try
        {
            ShowTemporaryStatus("正在扫描本地客户端…");
            await _scanLock.WaitAsync();
            (int DiscoveredCount, int NewCount) result;
            try
            {
                result = await ScanClientsCoreAsync();
            }
            finally
            {
                _scanLock.Release();
            }
            if (_isClosing) return;
            ShowTemporaryStatus(result.DiscoveredCount > 0
                ? $"扫描完成，发现 {result.DiscoveredCount} 个客户端（新增 {result.NewCount} 个）"
                : "未发现正在运行的客户端窗口");
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure(nameof(ScanClients_Click), exception);
        }
    }

    /// <summary>
    /// 调用方持有扫描锁。只更新发现结果和账号状态，不嵌入或恢复客户端窗口。
    /// </summary>
    private Dictionary<IntPtr, ClientWindowDiscovery.ManagedWindowIdentity> CaptureManagedDiscoveryWindows()
        => Entries.Where(entry => entry.IsAttached && WindowHost.Contains(entry.Handle)
                && entry.ProcessStartUtcTicks.HasValue && IsVerifiedClientWindow(entry))
            .ToDictionary(entry => entry.Handle, entry => new ClientWindowDiscovery.ManagedWindowIdentity(
                entry.ProcessId, entry.ProcessStartUtcTicks!.Value,
                entry.SessionState == ClientSessionState.Login ? ClientWindowRole.Login
                    : entry.SessionState == ClientSessionState.Main ? ClientWindowRole.Main : ClientWindowRole.Unknown));

    private async Task<(int DiscoveredCount, int NewCount)> ScanClientsCoreAsync()
    {
        if (_isClosing || _allExitInProgress) return (0, 0);

        var selfTestTargetProcessId = _selfTestTargetProcessId;
        var managerOwner = new WindowInteropHelper(this).Handle;
        var managedWindows = CaptureManagedDiscoveryWindows();
        var scanResult = await Task.Run(() =>
        {
            var candidates = ClientWindowDiscovery.FindClientWindows(selfTestTargetProcessId, managerOwner, managedWindows);
            var accounts = ClientMetadataService.ResolveWeChatAccounts(candidates);
            return (Candidates: candidates, Accounts: accounts);
        });

        if (_isClosing || _allExitInProgress) return (0, 0);
        var candidates = scanResult.Candidates.Where(IsVerifiedCandidate).ToArray();
        var scanUtc = DateTime.UtcNow;
        foreach (var entry in Entries.ToArray())
        {
            if (candidates.Any(candidate => candidate.Kind == entry.Kind
                && candidate.ProcessId == entry.ProcessId
                && (entry.Handle == candidate.Handle && IsVerifiedClientWindow(entry)
                    || CanRebindProcess(entry, candidate))))
                continue;

            if (IsVerifiedClientWindow(entry))
            {
                entry.IsOnline = true;
                entry.LastSeenUtc = scanUtc;
                entry.WindowMissingSinceUtc = null;
                entry.Status = entry.IsAttached ? "已接入" : "独立窗口";
                continue;
            }

            if (entry.ProcessStartUtcTicks is { } knownStart
                && TryGetProcessStartUtcTicks(entry.ProcessId) is { } currentStart
                && knownStart != currentStart)
            {
                RemoveEntry(entry);
                continue;
            }

            var processAlive = IsClientProcessAlive(entry.ProcessId);
            entry.WindowMissingSinceUtc ??= scanUtc;
            entry.IsOnline = processAlive;
            entry.SessionState = processAlive
                ? ClientSessionState.Rebinding
                : ClientSessionState.Exited;
            entry.Status = processAlive ? "窗口切换中" : "已离线";
        }

        var newCount = 0;
        foreach (var discoveredCandidate in candidates)
        {
            var candidate = discoveredCandidate;
            var entry = Entries.FirstOrDefault(existing => existing.Kind == candidate.Kind
                && existing.ProcessId == candidate.ProcessId
                && (existing.Handle == candidate.Handle && IsVerifiedClientWindow(existing)
                    || CanRebindProcess(existing, candidate)));
            // Hosting may resize a confirmed chat window to login-sized dimensions.
            // Keep the existing role unless its title or class identifies a login window.
            if (entry is not null
                && candidate.Kind == ClientKind.WeChat
                && candidate.Role == ClientWindowRole.Login
                && entry.SessionState == ClientSessionState.Main
                && !candidate.WindowTitle.Contains("登录", StringComparison.OrdinalIgnoreCase)
                && !candidate.WindowClass.Contains("WeChatLoginWndForPC", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate with { Role = ClientWindowRole.Main };
            }
            if (entry is null)
            {
                if (Entries.Any(existing => ClientWindowDiscovery.IsSameProcessInstance(existing, candidate)
                    && IsVerifiedClientWindow(existing)))
                    continue;

                entry = CreateEntry(candidate);
                entry.Status = "独立窗口";
                Entries.Add(entry);
                newCount++;
            }
            else
            {
                if (entry.Handle != candidate.Handle && entry.IsAttached)
                {
                    WindowHost.ForgetWindow(entry.Handle);
                    entry.IsAttached = false;
                }

                PrepareEntryForCandidate(entry, candidate);
                entry.Status = entry.IsAttached ? "已接入" : "独立窗口";
            }

            QueueOrApplyWeChatIdentity(entry, candidate, scanResult.Accounts);
        }

        if (newCount > 0) ApplySavedAccountOrder();
        UpdateWindowCount();
        if (SelectedEntry is { } selected)
        {
            CurrentStatusDot.Fill = selected.StatusBrush;
            if (!IsVerifiedClientWindow(selected))
                CurrentStatusText.Text = selected.IsOnline ? "窗口切换中" : "窗口已关闭";
            else
                RefreshSelectedProfileStatus(selected);
        }

        return (candidates.Length, newCount);
    }

    /// <summary>调用方持有扫描锁；仅“全部接入”执行窗口接入。</summary>
    private async Task<int> AttachAllDiscoveredClientsAsync()
    {
        await ScanClientsCoreAsync();
        if (_isClosing || _allExitInProgress) return 0;

        var attachedCount = 0;
        foreach (var entry in Entries.Where(IsVerifiedClientWindow))
        {
            if (!entry.IsAttached && entry.ProcessStartUtcTicks is null)
            {
                entry.Status = "无法验证进程";
                continue;
            }

            _dismissedWindowHandles.Remove(entry.Handle);
            entry.IsManuallyDetached = false;
            if (entry.IsAttached)
            {
                // 已接入的窗口跳过：避免 AttachWindow(makeActive:false) 把正在显示的
                // 窗口停靠到屏幕外，造成闪烁/窗口消失。
                attachedCount++;
                continue;
            }

            ReconnectDiagnostics.Write($"attach attempt kind={entry.Kind} pid={entry.ProcessId} hwnd=0x{entry.Handle.ToInt64():X} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            entry.IsAttached = WindowHost.AttachWindow(entry.Handle, makeActive: false,
                disableMaximize: entry.Kind == ClientKind.WhatsApp,
                    centerInHost: entry.Kind == ClientKind.WeChat && entry.SessionState == ClientSessionState.Login);
            entry.Status = entry.IsAttached ? "已接入" : "接入失败";
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

    private void OpenMainMenu_Click(object sender, RoutedEventArgs e)
    {
        // Both horizontal coordinates are relative to the close button,
        // whose right edge tracks the main window's right edge.
        MainMenuPopup.PlacementTarget = CloseWindowButton;
        MainMenuPopup.Placement = PlacementMode.Custom;
        MainMenuPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        [
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width - 8, targetSize.Height + 4),
                PopupPrimaryAxis.Horizontal)
        ];
        MainMenuPopup.HorizontalOffset = 0;
        MainMenuPopup.VerticalOffset = 0;
        MainMenuPopup.IsOpen = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowHostedModalDialog(new SettingsDialog(
            _settings, ApplyNotificationPreferences, RefreshStartupMenuState));
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        ShowHostedModalDialog(new AboutDialog());
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
        var online = Entries.Count(IsVerifiedClientWindow);
        var attached = Entries.Count(entry => entry.IsAttached && IsVerifiedClientWindow(entry));
        WindowCountText.Text = $"发现 {online} 个窗口，已接入 {attached} 个";
    }

    private void LoadPreviewEntries()
    {
        var firstWeChat = CreatePreviewEntry(ClientKind.WeChat, "示例账号 A", "4.1.13.12");
        firstWeChat.MessageAlertCount = 1;
        firstWeChat.AlertDisplayMode = AlertDisplayMode.Count;
        Entries.Add(firstWeChat);
        Entries.Add(CreatePreviewEntry(ClientKind.WeChat, "示例账号 B", "4.1.13.12"));
        Entries.Add(CreatePreviewEntry(ClientKind.WhatsApp, "WhatsApp"));
        WindowCountText.Text = "已接入 3 个窗口";
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
            Status = "已接入",
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
        if (identityChanged)
        {
            entry.AvatarPath = null;
            entry.MessageAlertCount = 0;
            entry.AlertDisplayMode = AlertDisplayMode.None;
            entry.PendingClearAfterDetection = false;
            _queuedImmediateBadgeScans.Remove(entry);
            _pendingClearRechecks.Remove(entry);
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
            // An unbound login slot is not evidence that its displayed name
            // belongs to this account. Do not save a stale/custom slot label
            // under a newly discovered identity.
            entry.HasCustomName = false;
            entry.DisplayName = account.DisplayName;
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
                or WeChatProfileFailureStage.AmbiguousAccountKey
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
            WeChatProfileFailureStage.AmbiguousAccountKey => "AmbiguousAccountKey",
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
        if (stage == WeChatProfileFailureStage.AmbiguousAccountKey)
        {
            // A terminal ambiguity is not an in-progress sync. Never display a
            // potentially stale nickname while more than one account is verified.
            entry.IdentityKey = null;
            entry.AccountId = null;
            entry.AvatarPath = null;
            entry.HasCustomName = false;
            entry.DisplayName = "微信 · 身份待验证";
            if (ReferenceEquals(entry, SelectedEntry))
                CurrentTitleText.Text = entry.DisplayName;
        }
        entry.ProfileDiagnosticCode = stage == WeChatProfileFailureStage.None
            ? null
            : GetProfileDiagnosticLabel(stage);
        entry.ProfileDiagnosticDetail = detail;
        RefreshSelectedProfileStatus(entry);
        DiagnosticLogService.WriteAccountEvent(entry, "profile-diagnostic");
    }

    private bool IsCurrentAccountResolution(
        ClientWindowEntry entry, int processId, int accountGeneration,
        IntPtr windowHandle, long? processStartTicks, long requestToken, int exitVersion)
        => !_isClosing
           && Entries.Contains(entry)
           && !IsProcessExiting(processId)
           && GetProcessExitVersion(processId) == exitVersion
           && entry.ProcessId == processId
           && entry.AccountGeneration == accountGeneration
           && entry.Handle == windowHandle
           && (!processStartTicks.HasValue
               || TryGetProcessStartUtcTicks(processId) == processStartTicks)
           && WindowBelongsToProcess(windowHandle, processId)
           && _accountResolutionLoads.TryGetValue(processId, out var activeToken)
           && activeToken == requestToken;

    private async void QueueWeChatAccountResolution(
        ClientWindowEntry entry,
        WeChatAccountIdentity? preferredAccount)
    {
        var processId = entry.ProcessId;
        if (_isClosing || IsProcessExiting(processId)) return;
        var accountGeneration = entry.AccountGeneration;
        var exitVersion = GetProcessExitVersion(processId);
        var windowHandle = entry.Handle;
        var processStartTicks = TryGetProcessStartUtcTicks(processId);
        if (entry.SessionState != ClientSessionState.Main)
            return;

        if (_resolvedWeChatAccounts.TryGetValue(processId, out var resolvedAccount))
        {
            var knownStart = _resolvedAccountProcessStarts.GetValueOrDefault(processId);
            if (!processStartTicks.HasValue || knownStart != processStartTicks.Value
                || !WindowBelongsToProcess(windowHandle, processId))
            {
                ResetWeChatAccountBinding(entry);
                return;
            }
            // Cached active identity is safe only within the current account
            // generation. Login resets remove this PID association.
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
        // Preserve a completed failure state during automatic retries; only the
        // first attempt should show the transient '资料读取中' label.
        if (entry.ProfileState == AccountProfileState.Pending
            && _accountResolutionFailures.GetValueOrDefault(processId) == 0)
        {
            entry.ProfileDiagnosticCode = null;
            entry.ProfileDiagnosticDetail = null;
        }

        try
        {
            await Task.Delay(300);
            if (!IsCurrentAccountResolution(entry, processId, accountGeneration,
                    windowHandle, processStartTicks, resolutionToken, exitVersion))
            {
                return;
            }

            await _profileReadLock.WaitAsync();
            (WeChatAccountIdentity? Account, WeChatProfileReadResult Result) resolution;
            try
            {
                if (!IsCurrentAccountResolution(entry, processId, accountGeneration,
                        windowHandle, processStartTicks, resolutionToken, exitVersion))
                {
                    return;
                }

                // Do not filter candidate accounts by another process' earlier claim.
            // Resolve ownership first, and quarantine conflicting live claims at commit.
            resolution = await Task.Run(() =>
                {
                    var candidates = ClientMetadataService.RankWeChatAccountsForProcess(processId)
                        .OrderBy(account => preferredAccount is not null
                            && account.Key.Equals(preferredAccount.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenByDescending(account => account.LastLoginUtc)
                        .ThenBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    // RC5: use file ownership independent of leftover memory keys.
                    // If RM cannot establish a unique owner, preserve RC4 safe fallback.
                    var ownership = WeChatDatabaseOwnershipService.Resolve(
                        processId, processStartTicks, candidates.Select(a => a.AccountId).ToArray());
                    var matched = ownership.AccountId is null ? null
                        : candidates.FirstOrDefault(a => a.AccountId.Equals(
                            ownership.AccountId, StringComparison.OrdinalIgnoreCase));
                    DiagnosticLogService.RecordRmSnapshot(processId, accountGeneration,
                        ownership.Status.ToString(), ownership.Detail,
                        matched?.Key, ownership.Detail);
                    DiagnosticLogService.WriteActivityEvent(
                        $"wechat-rm-ownership:{ownership.Status}:{ownership.Detail}", processId);
                    var scopedCandidates = ownership.Status == WeChatDatabaseOwnershipService.Verdict.Unique
                        ? candidates.Where(a => a.AccountId.Equals(ownership.AccountId,
                            StringComparison.OrdinalIgnoreCase)).ToArray()
                        : candidates;
                    var readResult = WeChatProfileService.TryReadMatchingProfileDetailed(
                        processId, scopedCandidates.Select(account => account.AccountId));
                    var profile = readResult.Profile;
                    var account = profile is null
                        ? null
                        : scopedCandidates.FirstOrDefault(candidate =>
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
                        windowHandle, processStartTicks, resolutionToken, exitVersion))
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
                    windowHandle, processStartTicks, resolutionToken, exitVersion))
            {
                return;
            }

            var account = resolution.Account;
            var profile = resolution.Result.Profile;
            // Startup can discover several already logged-in WeChat windows at once.
            // A profile candidate selected for one PID must never be committed to
            // another live PID. Keep the second entry unbound and retry rather
            // than showing somebody else's cached nickname, avatar or Toast label.
            // Conflicting live claims invalidate both identities until independently verified.
            var conflictingBinding = _resolvedWeChatAccounts.Any(pair =>
                pair.Key != processId
                && pair.Value.Key.Equals(account.Key, StringComparison.OrdinalIgnoreCase)
                && Entries.Any(other => other.Kind == ClientKind.WeChat
                    && other.ProcessId == pair.Key
                    && other.SessionState == ClientSessionState.Main
                    && WindowBelongsToProcess(other.Handle, other.ProcessId)));
            if (conflictingBinding)
            {
                // Both claimants are untrusted until each live process can be
                // independently checked. Do not let first-writer win the identity.
                foreach (var other in Entries.Where(other => other.Kind == ClientKind.WeChat
                    && other.ProcessId != processId
                    && other.SessionState == ClientSessionState.Main
                    && string.Equals(other.IdentityKey, account.Key, StringComparison.OrdinalIgnoreCase))
                    .ToArray())
                {
                    _resolvedWeChatAccounts.Remove(other.ProcessId);
                    _resolvedAccountProcessStarts.Remove(other.ProcessId);
                    _accountResolutionLoads.Remove(other.ProcessId);
                    other.AccountGeneration++;
                    other.IdentityKey = null;
                    other.AccountId = null;
                    other.AvatarPath = null;
                    other.HasCustomName = false;
                    other.DisplayName = "微信 · 身份待验证";
                    other.ProfileState = AccountProfileState.WaitingForLocalData;
                    other.ProfileDiagnosticCode = "IdentityConflict";
                    other.ProfileDiagnosticDetail = "不同微信进程声明同一个账号，已撤回双方身份绑定。";
                    RefreshSelectedProfileStatus(other);
                    ScheduleAccountResolutionRetry(other.ProcessId);
                }
                DiagnosticLogService.WriteActivityEvent("wechat-identity-conflict-both-isolated", processId);
                entry.IdentityKey = null;
                entry.AccountId = null;
                entry.AvatarPath = null;
                entry.HasCustomName = false;
                entry.DisplayName = "微信 · 身份待验证";
                entry.ProfileState = AccountProfileState.WaitingForLocalData;
                entry.ProfileDiagnosticCode = "IdentityConflict";
                entry.ProfileDiagnosticDetail = "两个活动微信进程声明了同一账号，等待独立验证。";
                RefreshSelectedProfileStatus(entry);
                ScheduleAccountResolutionRetry(processId);
                return;
            }
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
        catch (Exception ex)
        {
            if (IsCurrentAccountResolution(entry, processId, accountGeneration,
                    windowHandle, processStartTicks, resolutionToken, exitVersion))
            {
                entry.ProfileState = AccountProfileState.Error;
                entry.ProfileDiagnosticCode = "ProfileReadException";
                entry.ProfileDiagnosticDetail = ex.GetType().Name + ": " + ex.Message;
                RefreshSelectedProfileStatus(entry);
                DiagnosticLogService.WriteAccountEvent(entry, "profile-read-exception");
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
            3 => TimeSpan.FromSeconds(30),
            4 => TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromMinutes(2)
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

    private bool ForceRefreshWeChatProfile(ClientWindowEntry entry)
    {
        if (entry.Kind != ClientKind.WeChat || _isPreviewMode
            || entry.SessionState != ClientSessionState.Main
            || !WindowBelongsToProcess(entry.Handle, entry.ProcessId))
            return false;

        // Invalidate only this live account's identity resolution. Do not log out,
        // detach its HWND, clear unread badges, or erase its historical profile cache.
        // Generation/token guards prevent an old asynchronous read from writing
        // its result after this manual refresh starts.
        var pid = entry.ProcessId;
        DiagnosticLogService.ClearRmSnapshot(pid);
        entry.AccountGeneration++;
        _resolvedWeChatAccounts.Remove(pid);
        _resolvedAccountProcessStarts.Remove(pid);
        _accountResolutionLoads.Remove(pid);
        _accountResolutionRetryAfter.Remove(pid);
        _accountResolutionFailures.Remove(pid);
        ClientMetadataService.InvalidateLoginAccountCache();

        entry.IdentityKey = null;
        entry.AccountId = null;
        entry.AvatarPath = null;
        entry.HasCustomName = false;
        entry.ProfileState = AccountProfileState.Pending;
        entry.ProfileDiagnosticCode = null;
        entry.ProfileDiagnosticDetail = "手动同步已启动，正在验证当前微信进程的账号资料。";
        entry.DisplayName = "微信 · 资料同步中";
        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
            RefreshSelectedProfileStatus(entry);
        }

        DiagnosticLogService.WriteActivityEvent("wechat-profile-manual-resync-requested", pid);
        var preferred = ClientMetadataService.RankWeChatAccountsForProcess(pid).FirstOrDefault();
        QueueWeChatAccountResolution(entry, preferred);
        return true;
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
        var saved = false;
        if (!string.IsNullOrWhiteSpace(entry.IdentityKey))
        {
            _settings.AccountNamesByIdentity[entry.IdentityKey] = entry.DisplayName;
            saved = TrySaveSettings();
        }

        if (ReferenceEquals(entry, SelectedEntry))
        {
            CurrentTitleText.Text = entry.DisplayName;
        }

        if (saved)
            ShowTemporaryStatus($"账号名称已更新为“{entry.DisplayName}”。");
        else
            ShowTemporaryStatus("账号名称仅在本次运行中生效，尚未保存；请打开设置重试。", TimeSpan.FromSeconds(10));
    }

    private bool _clientExitInProgress;
    private int _exitingClientProcessId;

    private async void CloseEntry_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_clientExitInProgress) return;

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

        // WM_CLOSE may hide the application to tray; exit only the validated process.
        var affected = Entries.Count(other => other.ProcessId == entry.ProcessId);
        var answer = ShowExitConfirmation(entry.DisplayName, entry.ProcessId, affected);
        if (!answer) return;

        var handle = entry.Handle;
        var pid = entry.ProcessId;
        var expectedStart = entry.ProcessStartUtcTicks;
        if (!expectedStart.HasValue || !WindowBelongsToProcess(handle, pid)
            || TryGetProcessStartUtcTicks(pid) != expectedStart)
        {
            ShowTemporaryStatus("进程身份已变化或无法验证，取消退出以避免误关其他客户端。",
                TimeSpan.FromSeconds(8));
            return;
        }

        _clientExitInProgress = true;
        _exitingClientProcessId = pid;
        ShowTemporaryStatus($"正在退出 {entry.DisplayName}…", TimeSpan.FromHours(1));
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expectedStart.Value
                || !WindowBelongsToProcess(handle, pid))
            {
                ShowTemporaryStatus("客户端进程已变化，取消退出。", TimeSpan.FromSeconds(8));
                return;
            }

            // Only terminate this PID; do not kill other independent client processes.
            MarkProcessExitRequested(pid);
            var killTask = Task.Run(() =>
            {
                // The worker owns its own Process wrapper. Returning on timeout must
                // never dispose a wrapper that an unfinished worker is using.
                using var live = Process.GetProcessById(pid);
                if (live.HasExited || live.StartTime.ToUniversalTime().Ticks != expectedStart.Value
                    || !WindowBelongsToProcess(handle, pid))
                    throw new InvalidOperationException("Client process identity changed before exit.");
                live.Kill(entireProcessTree: false);
            });
            if (await Task.WhenAny(killTask, Task.Delay(TimeSpan.FromSeconds(5))) != killTask)
            {
                // A timed-out OS call can complete later. Its worker rechecks the
                // process generation before Kill, while future scans refresh UI.
                _ = killTask.ContinueWith(task =>
                    DiagnosticLogService.WriteErrorEvent("client-exit-late-worker", task.Exception!.GetBaseException()),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                DiagnosticLogService.WriteActivityEvent("client-exit-request-unconfirmed", pid);
                ShowTemporaryStatus("客户端退出请求超时，尚未确认；后台将继续检查实际进程状态。", TimeSpan.FromSeconds(8));
                return;
            }
            await killTask;
            ReconnectDiagnostics.Write($"sidebar exit requested kind={entry.Kind} pid={pid} hwnd=0x{handle.ToInt64():X}");
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                ReconnectDiagnostics.Write($"sidebar exit timeout pid={pid} confirmed=false");
                ShowTemporaryStatus("进程尚未确认退出，账号状态将由自动扫描继续核对。", TimeSpan.FromSeconds(8));
                return;
            }
            ReconnectDiagnostics.Write($"sidebar exit confirmed pid={pid}");
            foreach (var associated in Entries.Where(other => other.ProcessId == pid).ToArray())
                RemoveEntry(associated);
            ShowTemporaryStatus($"已退出 {entry.DisplayName}。");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or System.UnauthorizedAccessException)
        {
            ReconnectDiagnostics.Write($"sidebar exit failed pid={pid} error={exception.GetType().Name}");
            ShowTemporaryStatus("无法结束该客户端进程，已保留列表项；请检查权限。",
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            _exitingClientProcessId = 0;
            _clientExitInProgress = false;
        }
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

        // The in-window lock surface is fixed in place. Leave window-control
        // buttons interactive, but block dragging/double-click resizing.
        if (IsJNestProtected)
        {
            e.Handled = true;
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

    // The titlebar X hides the manager; menu exit retains its confirmation.
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        if (_isClosing || _allExitInProgress || _clientExitInProgress) return;
        WindowHost.HandleOwnerWindowStateChanged(true);
        _clockTimer.Stop();
        ShowInTaskbar = false;
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

