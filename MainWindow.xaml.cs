using System.Collections.ObjectModel;
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
    private readonly Dictionary<string, WeChatLocalProfile> _profilesByIdentity =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, WeChatAccountIdentity> _resolvedWeChatAccounts = [];
    private readonly HashSet<int> _accountResolutionLoads = [];
    private readonly Dictionary<int, DateTime> _accountResolutionRetryAfter = [];
    private readonly Dictionary<int, int> _accountResolutionFailures = [];
    private readonly HashSet<IntPtr> _dismissedWindowHandles = [];
    private ClientWindowEntry? _selectedEntry;
    private ClientKind? _selectNextKind;
    private int _wechatNumber;
    private bool _isClosing;
    private bool _isPreviewMode;
    private bool _isTitleBarInteraction;
    private bool _isClientLaunchInProgress;
    private readonly bool _isClientSelfTestMode;
    private readonly int? _selfTestTargetProcessId;
    private int _renderRefreshGeneration;
    private bool _overlayBoundsUpdatePending;

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
            Title = "聚窗窗口托管测试";
        }
        _settings = AppSettings.Load();
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _scanTimer.Tick += ScanTimer_Tick;
        StateChanged += MainWindow_StateChanged;
        Activated += (_, _) => TriggerForegroundBadgeCalibration();
        LocationChanged += (_, _) => QueueOverlayBoundsUpdate();
        SizeChanged += (_, _) => QueueOverlayBoundsUpdate();
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
        WindowHost.HandleOwnerWindowStateChanged(WindowState == WindowState.Minimized);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                WindowHost.UpdateOverlayBounds();
                if (!_isTitleBarInteraction) WindowHost.ActivateCurrentWindow();
            }));
        }
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

        StatusMessageText.Text = "聚窗已启动，正在后台发现本地客户端…";
        // 预热窗口托管宿主：HwndHost 在 Visibility=Collapsed 时可能不会调用
        // BuildWindowCore，导致首次 AttachWindow 因 _hostHandle 未创建而失败
        // （IsReady=false）。这里先设为可见并等待布局完成，确保宿主就绪后再扫描。
        // EmptyState 覆盖在其上，视觉上无变化。
        WindowHost.Visibility = Visibility.Visible;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Background);
        await ScanAndAttachAsync();
        if (Entries.Count > 0 && SelectedEntry is null)
        {
            SelectedEntry = Entries[0];
        }

        StatusMessageText.Text = Entries.Count == 0
            ? "未发现正在运行的客户端，可从顶部新建。"
            : $"已自动接入 {Entries.Count(entry => entry.IsAttached)} 个窗口。";
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
        StopBadgePolling();
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
        if (entry.IsAttached)
        {
            WindowHost.DetachWindow(entry.Handle, bringToFront: false);
        }

        var wasSelected = ReferenceEquals(SelectedEntry, entry);
        var processId = entry.ProcessId;
        _queuedImmediateBadgeScans.Remove(entry);
        _badgeStableState.Remove(entry);
        Entries.Remove(entry);

        if (entry.Kind == ClientKind.WeChat
            && !Entries.Any(candidate => candidate.Kind == ClientKind.WeChat && candidate.ProcessId == processId))
        {
            _resolvedWeChatAccounts.Remove(processId);
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

    private async Task ScanAndAttachAsync()
    {
        if (!await _scanLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            foreach (var entry in Entries.Where(entry => entry.Handle != IntPtr.Zero).ToArray())
            {
                var exists = NativeMethods.IsWindow(entry.Handle);
                entry.IsOnline = exists;
                if (!exists)
                {
                    RemoveEntry(entry);
                }
                else if (entry.IsAttached)
                {
                    entry.Status = "已嵌入";
                }
                else
                {
                    entry.Status = "独立窗口";
                }
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
                    existingEntry.ClientVersion = candidate.ClientVersion;
                    if (candidate.Kind == ClientKind.WeChat)
                    {
                        if (_resolvedWeChatAccounts.TryGetValue(candidate.ProcessId, out var resolvedAccount))
                        {
                            ApplyWeChatAccountIdentity(existingEntry, resolvedAccount);
                        }
                        else
                        {
                            weChatAccounts.TryGetValue(candidate.ProcessId, out var preferredAccount);
                            QueueWeChatAccountResolution(existingEntry, preferredAccount);
                        }
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

                var reusableEntry = Entries.FirstOrDefault(entry =>
                    entry.Kind == candidate.Kind
                    && entry.Handle != IntPtr.Zero
                    && !NativeMethods.IsWindow(entry.Handle));

                ClientWindowEntry entry;
                if (reusableEntry is not null)
                {
                    entry = reusableEntry;
                    var entryIndex = Entries.IndexOf(entry);
                    entry.Handle = candidate.Handle;
                    entry.ProcessId = candidate.ProcessId;
                    entry.ProcessName = candidate.ProcessName;
                    entry.WindowTitle = candidate.WindowTitle;
                    entry.ClientVersion = candidate.ClientVersion;
                    entry.IsOnline = true;
                    entry.IdentityKey = null;
                    entry.HasCustomName = false;
                    entry.AvatarPath = null;
                    entry.DisplayName = candidate.Kind == ClientKind.WeChat
                        ? $"微信 {entryIndex + 1}"
                        : "WhatsApp";
                }
                else
                {
                    entry = CreateEntry(candidate);
                    Entries.Add(entry);
                }

                if (candidate.Kind == ClientKind.WeChat)
                {
                    if (_resolvedWeChatAccounts.TryGetValue(candidate.ProcessId, out var resolvedAccount))
                    {
                        ApplyWeChatAccountIdentity(entry, resolvedAccount);
                    }
                    else
                    {
                        weChatAccounts.TryGetValue(candidate.ProcessId, out var preferredAccount);
                        QueueWeChatAccountResolution(entry, preferredAccount);
                    }
                }

                entry.IsAttached = WindowHost.AttachWindow(candidate.Handle, makeActive: false);
                entry.Status = entry.IsAttached ? "已嵌入" : "接入失败";

                if (_selectNextKind == candidate.Kind)
                {
                    _selectNextKind = null;
                    SelectedEntry = entry;
                    AccountsList.SelectedItem = entry;
                    StatusMessageText.Text = $"已接入 {entry.DisplayName}。";
                }
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
            candidate.ClientVersion);
    }

    private async void NewWeChat_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode || !TryBeginClientLaunch())
        {
            if (_isPreviewMode)
            {
                StatusMessageText.Text = "预览模式不会启动本地客户端。";
            }
            return;
        }

        try
        {
            StatusMessageText.Text = "正在准备微信客户端…";
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
                    StatusMessageText.Text = "未选择微信客户端。";
                    return;
                }

                executablePath = dialog.FileName;
                _settings.WeChatExecutablePath = executablePath;
                _settings.Save();
            }

            _dismissedWindowHandles.Clear();
            _selectNextKind = ClientKind.WeChat;
            await Task.Run(() => ClientLauncher.StartWeChat(executablePath));
            StatusMessageText.Text = "微信已启动，正在等待新窗口…";
            await WaitForExpectedWindowAsync(ClientKind.WeChat);
        }
        catch (Exception exception)
        {
            _selectNextKind = null;
            ShowOwnedMessageBox($"无法启动微信：\n{exception.Message}", "聚窗",
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
                StatusMessageText.Text = "预览模式不会启动本地客户端。";
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
                StatusMessageText.Text = "WhatsApp 已经在管理器中。";
                return;
            }

            StatusMessageText.Text = "正在准备 WhatsApp 客户端…";
            await Dispatcher.Yield(DispatcherPriority.Input);
            _dismissedWindowHandles.Clear();
            _selectNextKind = ClientKind.WhatsApp;
            await Task.Run(ClientLauncher.StartWhatsApp);
            StatusMessageText.Text = "WhatsApp 已启动，正在等待窗口…";
            await WaitForExpectedWindowAsync(ClientKind.WhatsApp);
        }
        catch (Exception exception)
        {
            _selectNextKind = null;
            ShowOwnedMessageBox($"无法启动 WhatsApp：\n{exception.Message}", "聚窗",
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
            _selectNextKind = null;
            StatusMessageText.Text = "客户端可能仍在启动；窗口出现后会自动接入。";
        }
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsList.SelectedItem is ClientWindowEntry entry)
        {
            SelectedEntry = entry;
        }
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
            SetEmptyState("客户端窗口已关闭", "重新打开客户端后，聚窗会尝试把它接回原来的位置。");
            return;
        }

        PopOutButton.IsEnabled = true;
        if (!entry.IsAttached)
        {
            CurrentStatusText.Text = "当前为独立窗口";
            PopOutButtonText.Text = "嵌入窗口";
            WindowHost.HideAll();
            SetEmptyState("窗口已弹出到桌面", "点击顶部“嵌入窗口”即可把它放回聚窗。");
            return;
        }

        CurrentStatusText.Text = "已嵌入 · 运行中";
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
            WindowHost.DetachWindow(entry.Handle);
            entry.IsAttached = false;
            entry.Status = "独立窗口";
            StatusMessageText.Text = $"{entry.DisplayName} 已弹出到桌面。";
        }
        else
        {
            entry.IsAttached = WindowHost.AttachWindow(entry.Handle, makeActive: true);
            entry.Status = entry.IsAttached ? "已嵌入" : "接入失败";
            StatusMessageText.Text = entry.IsAttached
                ? $"{entry.DisplayName} 已重新嵌入。"
                : "窗口接入失败，请尝试“重新接入全部窗口”。";
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
        StatusMessageText.Text = "正在接入容器外的客户端窗口…";
        var attachedCount = await ScanAndReattachAsync(clearDismissed: true);
        StatusMessageText.Text = attachedCount > 0
            ? $"全部接入完成：{attachedCount} 个窗口。"
            : "没有发现可接入的微信或 WhatsApp 主窗口。";
    }

    private async void ScanClients_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMode)
        {
            return;
        }

        StatusMessageText.Text = "正在扫描并接入本地客户端…";
        var attachedCount = await ScanAndReattachAsync(clearDismissed: false);
        StatusMessageText.Text = attachedCount > 0
            ? $"扫描完成：已接入 {attachedCount} 个窗口。"
            : "没有发现新的客户端窗口。";
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
        }

        await ScanAndAttachAsync();

        var attachedCount = 0;
        foreach (var entry in Entries.Where(entry =>
                     entry.Handle != IntPtr.Zero && NativeMethods.IsWindow(entry.Handle)))
        {
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
        // 弹窗期间抑制嵌入窗口置顶/激活：否则窗口锁定定时器会反复把客户端
        // SetWindowPos(HWND_TOP)，把"关于"对话框顶到嵌入窗口之下。
        WindowHost.SuspendOverlayZOrder();
        try
        {
            // 版本号从程序集元数据动态读取，避免与 csproj 的 <Version> 脱节。
            var version = typeof(MainWindow).Assembly.GetName().Version;
            var displayVersion = version is null
                ? "未知"
                : $"{version.Major}.{version.Minor}.{version.Build}";
            MessageBox.Show(
                this,
                $"聚窗 {displayVersion}\n一窗聚合多媒，矩阵高效出海\n\n" +
                "统一管理本地微信与 WhatsApp 窗口。\n" +
                "微信版本从当前本地客户端自动读取。\n" +
                "嵌入窗口固定在容器内，只能通过顶部“弹出窗口”恢复独立窗口。\n" +
                "嵌入时保留客户端完整原生样式（标题栏、边框、圆角与阴影）。\n" +
                "仅在本机只读获取当前微信账号的昵称与头像缓存，不读取聊天数据或登录凭据。\n" +
                "客户端发出 Windows 注意请求时会显示消息提示，最小化后由托盘图标接管。",
                "关于聚窗",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
        => new(IntPtr.Zero, 0, string.Empty, string.Empty, kind, name, clientVersion)
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

        if (_profilesByIdentity.TryGetValue(account.Key, out var cachedProfile))
        {
            ApplyWeChatProfile(entry, account, cachedProfile);
        }
    }

    private async void QueueWeChatAccountResolution(
        ClientWindowEntry entry,
        WeChatAccountIdentity? preferredAccount)
    {
        var processId = entry.ProcessId;
        if (_resolvedWeChatAccounts.TryGetValue(processId, out var resolvedAccount))
        {
            ApplyWeChatAccountIdentity(entry, resolvedAccount);
            return;
        }

        if (_accountResolutionLoads.Contains(processId)
            || (_accountResolutionRetryAfter.TryGetValue(processId, out var retryAfter)
                && retryAfter > DateTime.UtcNow))
        {
            return;
        }

        _accountResolutionLoads.Add(processId);
        try
        {
            await Task.Delay(300);
            if (_isClosing
                || !Entries.Contains(entry)
                || entry.ProcessId != processId
                || !NativeMethods.IsWindow(entry.Handle))
            {
                return;
            }

            await _profileReadLock.WaitAsync();
            (WeChatAccountIdentity Account, WeChatLocalProfile Profile)? resolution;
            try
            {
                if (_isClosing
                    || !Entries.Contains(entry)
                    || entry.ProcessId != processId)
                {
                    return;
                }

                var unavailableAccountKeys = _resolvedWeChatAccounts
                    .Where(pair => pair.Key != processId)
                    .Select(pair => pair.Value.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                resolution = await Task.Run<(WeChatAccountIdentity Account, WeChatLocalProfile Profile)?>(() =>
                {
                    var candidates = ClientMetadataService.RankWeChatAccountsForProcess(processId)
                        .OrderBy(account => preferredAccount is not null
                            && account.Key.Equals(preferredAccount.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(account => account.LastLoginUtc)
                        .ThenBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                        .Where(account => !unavailableAccountKeys.Contains(account.Key))
                        .ToArray();
                    var profile = WeChatProfileService.TryReadMatchingProfile(
                        processId,
                        candidates.Select(account => account.AccountId));
                    if (profile is null)
                    {
                        return null;
                    }

                    var account = candidates.FirstOrDefault(candidate =>
                        candidate.AccountId.Equals(profile.AccountId, StringComparison.OrdinalIgnoreCase));
                    return account is null ? null : (account, profile);
                });
            }
            finally
            {
                _profileReadLock.Release();
            }

            if (resolution is null)
            {
                ScheduleAccountResolutionRetry(processId);
                return;
            }

            if (_isClosing
                || !Entries.Contains(entry)
                || entry.ProcessId != processId
                || !NativeMethods.IsWindow(entry.Handle))
            {
                return;
            }

            var (account, profile) = resolution.Value;
            _resolvedWeChatAccounts[processId] = account;
            _profilesByIdentity[account.Key] = profile;
            _accountResolutionRetryAfter.Remove(processId);
            _accountResolutionFailures.Remove(processId);
            ApplyWeChatAccountIdentity(entry, account);
        }
        catch
        {
            ScheduleAccountResolutionRetry(processId);
        }
        finally
        {
            _accountResolutionLoads.Remove(processId);
        }
    }

    private void ScheduleAccountResolutionRetry(int processId)
    {
        var failures = _accountResolutionFailures.TryGetValue(processId, out var current)
            ? current + 1
            : 1;
        _accountResolutionFailures[processId] = failures;
        var delay = failures switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(3),
            3 => TimeSpan.FromMinutes(10),
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

        StatusMessageText.Text = $"账号名称已更新为“{entry.DisplayName}”。";
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
            StatusMessageText.Text = "预览模式不会关闭本地客户端。";
            return;
        }

        if (entry.Handle == IntPtr.Zero || !NativeMethods.IsWindow(entry.Handle))
        {
            RemoveEntry(entry);
            return;
        }

        var answer = ShowOwnedMessageBox(
            $"确定关闭“{entry.DisplayName}”的客户端窗口吗？\n\n这不会关闭聚窗，也不会影响其他账号。",
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
        StatusMessageText.Text = closeRequested
            ? $"已关闭并移除 {displayName}。"
            : $"已从列表移除 {displayName}；客户端未响应关闭请求。";
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
