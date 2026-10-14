using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JuChuang.Services;

namespace JuChuang.Controls;

/// <summary>
/// Keeps native client windows visually inside the WPF surface while leaving
/// them as top-level windows.  WeChat 4.x changes to its inactive palette when
/// re-parented as WS_CHILD, so visual hosting preserves the native appearance.
/// </summary>
public sealed class ExternalWindowHost : HwndHost
{
    private readonly ConcurrentDictionary<IntPtr, HostedWindowState> _hostedWindows = [];
    private readonly ConcurrentDictionary<IntPtr, long> _lastLocationEventTicks = [];
    private readonly DispatcherTimer _windowLockTimer;
    private readonly NativeMethods.WinEventDelegate _winEventCallback;
    private IntPtr _hostHandle;
    private IntPtr _currentHandle;
    private IntPtr _moveSizeHook;
    private IntPtr _locationHook;
    private bool _ownerMinimized;
    private int _modalDialogSuppressionCount;
    private int _zOrderSuppressionCount;

    public ExternalWindowHost()
    {
        _winEventCallback = OnHostedWindowEvent;
        _windowLockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _windowLockTimer.Tick += (_, _) => EnforceHostedWindows();
    }

    public bool IsReady => _hostHandle != IntPtr.Zero;
    public bool Contains(IntPtr handle) => _hostedWindows.ContainsKey(handle);

    /// <summary>
    /// True only while the manager itself or its displayed native client owns
    /// the foreground. Background overlays must not compete with Chrome or
    /// another unrelated application for global Z order.
    /// </summary>
    public bool IsManagerContextForeground
        => WindowActivityPolicy.IsManagerContextForeground(
            NativeMethods.GetForegroundWindow(),
            GetOwnerRoot(),
            _currentHandle);

    public bool AttachWindow(IntPtr handle, bool makeActive)
    {
        if (!IsReady || handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
        {
            return false;
        }

        if (!_hostedWindows.ContainsKey(handle))
        {
            NativeMethods.GetWindowRect(handle, out var originalRect);
            var nativeChrome = NativeMethods.CaptureNativeChromeState(handle);
            var state = new HostedWindowState(
                NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER),
                NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_STYLE),
                NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE),
                originalRect,
                CalculateHostInsets(handle, originalRect),
                nativeChrome);

            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
            NativeMethods.ApplyNativeChromeState(handle, nativeChrome);
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GWL_STYLE,
                CreateOverlayStyle(state.Style, isVisible: true));
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GWL_EXSTYLE,
                CreateOverlayExtendedStyle(state.ExtendedStyle));
            var ownerRoot = GetOwnerRoot();
            if (ownerRoot != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWLP_HWNDPARENT, ownerRoot);
            }
            var attachZOrderFlags = WindowActivityPolicy.ShouldPromoteHostedWindow(
                IsZOrderSuppressed,
                NativeMethods.GetForegroundWindow(),
                ownerRoot,
                handle)
                ? 0u
                : NativeMethods.SWP_NOZORDER;
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HWND_TOP,
                0,
                0,
                1,
                1,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | attachZOrderFlags);
            // 保留客户端完整原生样式：不清除标题栏/边框/圆角，仅在接入时清掉
            // 可能残留的旧裁剪区域，后续由 ApplyClipRegion 重新设定圆角裁剪。
            NativeMethods.SetWindowRgn(handle, IntPtr.Zero, true);
            _hostedWindows.TryAdd(handle, state);
        }

        if (makeActive)
        {
            ShowOnly(handle);
        }
        else
        {
            ParkWindowOffScreen(handle);
        }
        return true;
    }

    public bool ShowOnly(IntPtr handle)
    {
        if (!_hostedWindows.ContainsKey(handle) || !NativeMethods.IsWindow(handle))
        {
            return false;
        }

        foreach (var hostedHandle in _hostedWindows.Keys)
        {
            if (hostedHandle != handle && NativeMethods.IsWindow(hostedHandle))
            {
                ParkWindowOffScreen(hostedHandle);
            }
        }

        _currentHandle = handle;
        if (!_ownerMinimized && _modalDialogSuppressionCount == 0)
        {
            ResizeCurrentWindow();
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
            var zOrderFlags = ShouldPromoteCurrentWindow()
                ? 0u
                : NativeMethods.SWP_NOZORDER;
            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | zOrderFlags);
            ScheduleActivation(handle);
        }
        RefreshHostedWindow(handle);
        return true;
    }

    public bool RefreshHostedWindow(IntPtr handle)
    {
        if (!_hostedWindows.ContainsKey(handle) || !NativeMethods.IsWindow(handle)
            || _hostHandle == IntPtr.Zero || _currentHandle != handle)
        {
            return false;
        }

        if (!_ownerMinimized && _modalDialogSuppressionCount == 0)
        {
            ResizeCurrentWindow();
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
            NativeMethods.RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE
                | NativeMethods.RDW_FRAME);
        }
        return true;
    }

    public void ActivateCurrentWindow()
    {
        if (_currentHandle != IntPtr.Zero && !_ownerMinimized
            && _modalDialogSuppressionCount == 0
            && _hostedWindows.ContainsKey(_currentHandle)
            && NativeMethods.IsWindow(_currentHandle))
        {
            ActivateHostedWindow(_currentHandle);
        }
    }

    public void UpdateOverlayBounds() => ResizeCurrentWindow();

    public void HandleOwnerWindowStateChanged(bool minimized)
    {
        _ownerMinimized = minimized;
        if (minimized)
        {
            foreach (var handle in _hostedWindows.Keys)
            {
                ParkWindowOffScreen(handle);
            }
            return;
        }

        if (_modalDialogSuppressionCount == 0
            && _currentHandle != IntPtr.Zero && NativeMethods.IsWindow(_currentHandle))
        {
            ResizeCurrentWindow();
            NativeMethods.ShowWindow(_currentHandle, NativeMethods.SW_SHOW);
            ScheduleActivation(_currentHandle);
        }
    }

    public void HideAll()
    {
        foreach (var handle in _hostedWindows.Keys)
        {
            ParkWindowOffScreen(handle);
        }
        _currentHandle = IntPtr.Zero;
    }

    public void SuspendForModalDialog()
    {
        _modalDialogSuppressionCount++;
        if (_modalDialogSuppressionCount != 1) return;

        foreach (var handle in _hostedWindows.Keys)
        {
            ParkWindowOffScreen(handle);
        }
    }

    public void ResumeAfterModalDialog()
    {
        if (_modalDialogSuppressionCount == 0) return;

        _modalDialogSuppressionCount--;
        if (_modalDialogSuppressionCount != 0 || _ownerMinimized
            || _currentHandle == IntPtr.Zero || !NativeMethods.IsWindow(_currentHandle)) return;

        ResizeCurrentWindow();
        NativeMethods.ShowWindow(_currentHandle, NativeMethods.SW_SHOW);
        var zOrderFlags = ShouldPromoteCurrentWindow()
            ? 0u
            : NativeMethods.SWP_NOZORDER;
        NativeMethods.SetWindowPos(_currentHandle, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | zOrderFlags);
        ScheduleActivation(_currentHandle);
    }

    /// <summary>
    /// 临时抑制嵌入窗口的置顶与激活（用于宿主弹出自有对话框的场景）。
    /// 若不抑制，窗口锁定定时器会在对话框弹出期间反复把客户端窗口 SetWindowPos(HWND_TOP)，
    /// 导致对话框被顶到嵌入窗口之下。
    /// </summary>
    public void SuspendOverlayZOrder() => _zOrderSuppressionCount++;

    public void ResumeOverlayZOrder()
    {
        if (_zOrderSuppressionCount == 0) return;
        _zOrderSuppressionCount--;
    }

    private bool IsZOrderSuppressed => _zOrderSuppressionCount > 0;

    private bool ShouldPromoteCurrentWindow()
        => WindowActivityPolicy.ShouldPromoteHostedWindow(
            IsZOrderSuppressed,
            NativeMethods.GetForegroundWindow(),
            GetOwnerRoot(),
            _currentHandle);

    public bool DetachWindow(IntPtr handle, bool bringToFront = true)
    {
        if (!_hostedWindows.TryRemove(handle, out var state)) return false;
        if (_currentHandle == handle) _currentHandle = IntPtr.Zero;
        if (!NativeMethods.IsWindow(handle)) return true;

        NativeMethods.ShowWindow(handle, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowRgn(handle, IntPtr.Zero, true);
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWLP_HWNDPARENT, state.Owner);
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_STYLE, state.Style);
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, state.ExtendedStyle);
        NativeMethods.ApplyNativeChromeState(handle, state.ChromeState);

        var width = Math.Max(720, state.Rect.Width);
        var height = Math.Max(560, state.Rect.Height);
        var left = state.Rect.Left < -10000 ? 120 : state.Rect.Left;
        var top = state.Rect.Top < -10000 ? 120 : state.Rect.Top;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED
            | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        if (bringToFront) NativeMethods.SetForegroundWindow(handle);
        return true;
    }

    public void DetachAll()
    {
        foreach (var handle in _hostedWindows.Keys.ToArray()) DetachWindow(handle, false);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var style = unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE
            | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS));
        _hostHandle = NativeMethods.CreateWindowEx(0, "static", string.Empty, style,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (_hostHandle == IntPtr.Zero) throw new InvalidOperationException("无法创建本地窗口容器。");
        StartWindowLockMonitoring();
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        StopWindowLockMonitoring();
        DetachAll();
        if (hwnd.Handle != IntPtr.Zero) NativeMethods.DestroyWindow(hwnd.Handle);
        _hostHandle = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeCurrentWindow();
    }

    private void ResizeCurrentWindow()
    {
        if (_modalDialogSuppressionCount != 0 || _ownerMinimized
            || _hostHandle == IntPtr.Zero || _currentHandle == IntPtr.Zero
            || !NativeMethods.IsWindow(_currentHandle)
            || !NativeMethods.GetClientRect(_hostHandle, out var hostRect)
            || !_hostedWindows.TryGetValue(_currentHandle, out var state)) return;

        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hostHandle, ref origin)) return;
        // 边界对齐（旧版基线）：窗口矩形 = 容器客户区 + 四周 insets，位置 = 容器原点 - insets。
        // 顶层窗口带 WS_THICKFRAME 时，GetWindowRect 比可见客户区多出一圈不可见的 DWM 缩放
        // 边框（Extended Frame Bounds），insets 即这圈边框的宽度。放大并反向偏移后，客户端
        // 的可见内容才能精确填满容器，否则会出现悬浮间隙或内容越界。
        var insets = state.Insets;
        var width = Math.Max(1, hostRect.Width + insets.Left + insets.Right);
        var height = Math.Max(1, hostRect.Height + insets.Top + insets.Bottom);
        var dpi = VisualTreeHelper.GetDpi(this);
        // 通铺式嵌入：不做圆角裁剪（cornerRadius=0 → CreateRoundRectRgn 退化为矩形），
        // 原生窗口以直角原样铺满工作区，左右紧贴侧栏与外壳，视觉上是界面的一部分。
        // 但仍用 SetWindowRgn 裁掉超出客户区的不可见缩放边框与 DWM 阴影，避免溢出。
        var cornerRadius = 0;
        var zOrderFlags = ShouldPromoteCurrentWindow()
            ? 0u
            : NativeMethods.SWP_NOZORDER;
        NativeMethods.SetWindowPos(_currentHandle, NativeMethods.HWND_TOP,
            origin.X - insets.Left, origin.Y - insets.Top, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED
            | NativeMethods.SWP_SHOWWINDOW | zOrderFlags);
        ApplyClipRegion(_currentHandle, hostRect.Width, hostRect.Height, insets, cornerRadius);
    }

    /// <summary>
    /// 裁剪窗口区域：裁掉超出容器客户区的不可见缩放边框与 DWM 阴影，避免溢出。
    /// cornerRadius &gt; 0 时用圆角矩形（卡片式）；= 0 时退化为直角矩形（通铺式）。
    /// </summary>
    private static void ApplyClipRegion(
        IntPtr handle,
        int clientWidth,
        int clientHeight,
        HostInsets insets,
        int cornerRadius)
    {
        var width = Math.Max(1, clientWidth + insets.Left + insets.Right);
        var height = Math.Max(1, clientHeight + insets.Top + insets.Bottom);
        var right = Math.Max(insets.Left + 1, width - insets.Right);
        var bottom = Math.Max(insets.Top + 1, height - insets.Bottom);
        var region = cornerRadius > 0
            ? NativeMethods.CreateRoundRectRgn(
                insets.Left,
                insets.Top,
                right,
                bottom,
                Math.Min(cornerRadius * 2, Math.Max(2, right - insets.Left)),
                Math.Min(cornerRadius * 2, Math.Max(2, bottom - insets.Top)))
            : NativeMethods.CreateRectRgn(insets.Left, insets.Top, right, bottom);
        if (region == IntPtr.Zero || !NativeMethods.SetWindowRgn(handle, region, true))
        {
            if (region != IntPtr.Zero) NativeMethods.DeleteObject(region);
        }
    }

    private void EnforceHostedWindows()
    {
        if (_hostHandle == IntPtr.Zero) return;
        foreach (var handle in _hostedWindows.Keys)
        {
            if (!NativeMethods.IsWindow(handle))
            {
                _hostedWindows.TryRemove(handle, out _);
                if (_currentHandle == handle) _currentHandle = IntPtr.Zero;
                continue;
            }
            EnforceHostedWindow(handle);
        }
    }

    private void EnforceHostedWindow(IntPtr handle)
    {
        if (!_hostedWindows.TryGetValue(handle, out var state) || !NativeMethods.IsWindow(handle)) return;
        var selected = _currentHandle == handle && !_ownerMinimized
            && _modalDialogSuppressionCount == 0;
        // 托管窗口统一保持 WS_VISIBLE：选中的在容器内显示，非选中的停靠到屏幕外。
        // 若按 selected 清除 WS_VISIBLE，非选中窗口会被"隐藏"，其标题栏无法闪烁，
        // FlashWindowEx/HSHELL_FLASH 来消息通知会失效。真正的显隐区分交给停靠位置。
        var expectedStyle = CreateOverlayStyle(state.Style, isVisible: true);
        if (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_STYLE) != expectedStyle)
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_STYLE, expectedStyle);
        var expectedExStyle = CreateOverlayExtendedStyle(state.ExtendedStyle);
        if (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE) != expectedExStyle)
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, expectedExStyle);
        var ownerRoot = GetOwnerRoot();
        if (ownerRoot != IntPtr.Zero && NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) != ownerRoot)
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWLP_HWNDPARENT, ownerRoot);
        if (!selected)
        {
            // 非选中窗口不 SW_HIDE，而是停靠到屏幕外并保持显示：SW_HIDE 会让窗口
            // 失去标题栏与任务栏按钮，客户端调用 FlashWindowEx 时无法闪烁，也就收不到
            // HSHELL_FLASH 来消息通知。屏幕外停靠后窗口对系统仍是"可见"的，标题栏闪烁
            // 能正常触发通知，而用户看不到它（屏幕外 + 最小化窗口栏外）。
            ParkWindowOffScreen(handle);
            return;
        }
        if (!NativeMethods.IsWindowVisible(handle)) NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
        // 仅在窗口位置/尺寸偏离期望时强制归位（防微信自绘或拖拽破坏布局）；
        // ResizeCurrentWindow 内部会同步重算 insets 并重设圆角裁剪区域。
        if (!TryGetExpectedBounds(state, out var expected) || !NativeMethods.GetWindowRect(handle, out var actual)
            || actual.Left != expected.Left || actual.Top != expected.Top
            || actual.Width != expected.Width || actual.Height != expected.Height)
        {
            ResizeCurrentWindow();
        }
    }

    private bool TryGetExpectedBounds(HostedWindowState state, out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (!NativeMethods.GetClientRect(_hostHandle, out var hostRect)) return false;
        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hostHandle, ref origin)) return false;
        // 期望矩形 = 容器客户区向外扩展 insets（与 ResizeCurrentWindow 一致）。
        bounds = new NativeMethods.RECT
        {
            Left = origin.X - state.Insets.Left,
            Top = origin.Y - state.Insets.Top,
            Right = origin.X + hostRect.Width + state.Insets.Right,
            Bottom = origin.Y + hostRect.Height + state.Insets.Bottom
        };
        return true;
    }

    private void StartWindowLockMonitoring()
    {
        _moveSizeHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MOVESIZESTART,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _winEventCallback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _locationHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventCallback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _windowLockTimer.Start();
    }

    private void StopWindowLockMonitoring()
    {
        _windowLockTimer.Stop();
        if (_moveSizeHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_moveSizeHook); _moveSizeHook = IntPtr.Zero; }
        if (_locationHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_locationHook); _locationHook = IntPtr.Zero; }
    }

    private void OnHostedWindowEvent(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (window == IntPtr.Zero || !_hostedWindows.ContainsKey(window)) return;
        if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZESTART)
        {
            NativeMethods.PostMessage(window, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.PostMessage(window, NativeMethods.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        }

        // EVENT_OBJECT_LOCATIONCHANGE 是全局高频事件，客户端窗口内部的控件动画、
        // 重绘、光标移动都会触发。对同一窗口做 200ms 节流，避免把 UI 线程淹没在
        // BeginInvoke + EnforceHostedWindow 的队列里（窗口重排时 GC 压力的主要来源）。
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            var now = Environment.TickCount64;
            if (_lastLocationEventTicks.TryGetValue(window, out var last) && now - last < 200)
            {
                return;
            }
            _lastLocationEventTicks[window] = now;
        }

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => EnforceHostedWindow(window)));
    }

    private static IntPtr CreateOverlayStyle(IntPtr originalStyle, bool isVisible)
    {
        var style = unchecked((long)(uint)originalStyle.ToInt64());
        // 保留 WS_CAPTION/WS_THICKFRAME：微信 4.x 与 WhatsApp 都是自绘窗口，原生标题栏
        // 并不会渲染出来，但 DWM 依赖这些位来计算 Extended Frame Bounds 与阴影。移除后
        // 反而会导致内容尺寸错位（悬浮感）与阴影溢出。真正的边界对齐交给 insets + SetWindowRgn。
        style &= ~NativeMethods.WS_CHILD;
        if (isVisible) style |= NativeMethods.WS_VISIBLE;
        else style &= ~NativeMethods.WS_VISIBLE;
        return new IntPtr(style);
    }

    private static IntPtr CreateOverlayExtendedStyle(IntPtr originalExtendedStyle)
    {
        var style = unchecked((long)(uint)originalExtendedStyle.ToInt64());
        return new IntPtr(style & ~NativeMethods.WS_EX_APPWINDOW);
    }

    private void ScheduleActivation(IntPtr handle)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (_currentHandle == handle && !_ownerMinimized
                    && _modalDialogSuppressionCount == 0 && !IsZOrderSuppressed) ActivateHostedWindow(handle);
            }));
    }

    private void ActivateHostedWindow(IntPtr handle)
    {
        if (IsZOrderSuppressed) return;
        var root = GetOwnerRoot();
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != root && foreground != handle) return;
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(handle, out _);
        if (targetThread != 0 && targetThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true))
        {
            try
            {
                NativeMethods.SetForegroundWindow(handle);
                NativeMethods.SetActiveWindow(handle);
                NativeMethods.SetFocus(handle);
            }
            finally { NativeMethods.AttachThreadInput(currentThread, targetThread, false); }
        }
        else
        {
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.SetActiveWindow(handle);
            NativeMethods.SetFocus(handle);
        }
    }

    private IntPtr GetOwnerRoot() => _hostHandle == IntPtr.Zero
        ? IntPtr.Zero : NativeMethods.GetAncestor(_hostHandle, NativeMethods.GA_ROOT);

    // 把非选中窗口"停靠"到屏幕外（-32000,-32000）并保持显示，而不是 SW_HIDE。
    // SW_HIDE 会移除窗口的标题栏与任务栏按钮，导致客户端 FlashWindowEx 无法闪烁、
    // HSHELL_FLASH 永远不触发；停靠后窗口对系统仍"可见"，标题栏闪烁能正常产生来消息通知。
    private static void ParkWindowOffScreen(IntPtr handle)
    {
        if (!NativeMethods.IsWindow(handle) || IsParkedOffScreen(handle)) return;
        // 先移动到屏幕外再显示，避免窗口在原始（屏幕内）位置短暂闪现。
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, -32000, -32000, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private static bool IsParkedOffScreen(IntPtr handle)
    {
        return NativeMethods.GetWindowRect(handle, out var rect) && rect.Left <= -32000 && rect.Top <= -32000;
    }

    // 计算顶层窗口四周不可见的 DWM 缩放边框宽度（Extended Frame Bounds 与窗口矩形之差）。
    // 这是窗口视觉托管时对齐内容与容器、消除边界溢出的核心数据。
    private static HostInsets CalculateHostInsets(IntPtr handle, NativeMethods.RECT originalRect)
    {
        if (!NativeMethods.TryGetExtendedFrameBounds(handle, out var frame)) return new HostInsets(0, 0, 0, 0);
        return new HostInsets(
            Math.Clamp(frame.Left - originalRect.Left, 0, 20),
            Math.Clamp(frame.Top - originalRect.Top, 0, 20),
            Math.Clamp(originalRect.Right - frame.Right, 0, 20),
            Math.Clamp(originalRect.Bottom - frame.Bottom, 0, 20));
    }

    private readonly record struct HostInsets(int Left, int Top, int Right, int Bottom);

    private sealed record HostedWindowState(
        IntPtr Owner,
        IntPtr Style,
        IntPtr ExtendedStyle,
        NativeMethods.RECT Rect,
        HostInsets Insets,
        NativeMethods.DwmChromeState ChromeState);
}
