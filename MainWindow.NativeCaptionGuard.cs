using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JuChuang.Services;

namespace JuChuang;

// Experimental beta: suppress physical mouse actions over the embedded
// client's caption controls, without modifying or injecting into its HWND.
// A WH_MOUSE_LL hook is process-owned; all other input passes through.
public partial class MainWindow
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmRButtonDblClk = 0x0206;
    private const int WmMButtonDblClk = 0x0209;
    private const int WmXButtonDblClk = 0x020D;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;

    [StructLayout(LayoutKind.Sequential)]
    private struct HookPoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public HookPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    private delegate IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookCallback proc, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr message, IntPtr data);

    private readonly DispatcherTimer _captionGuardRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private MouseHookCallback? _captionHookCallback;
    private IntPtr _captionMouseHook;
    private IntPtr _guardedClient;
    private IntPtr _guardedOwner;
    private IntPtr _modalGuardDialogHwnd = IntPtr.Zero;
    private Window? _modalRootDialog;
    private bool _modalActivationQueued;

    private void BeginModalInputGuard(Window dialog)
    {
        StartNativeCaptionGuard();
        _modalRootDialog = dialog;
        _modalGuardDialogHwnd = new WindowInteropHelper(dialog).Handle;
        ReconnectDiagnostics.Start();
    }

    private void EndModalInputGuard()
    {
        DisposeModalClickShield();
        _modalRootDialog = null;
        _modalGuardDialogHwnd = IntPtr.Zero;
    }

    // Settings -> change password -> removal confirmation forms a nested owner
    // chain. Always return to the deepest currently visible dialog, not to an
    // outer disabled owner. Every dialog retains its normal modal behavior.
    private Window? GetTopmostHostedModal()
    {
        var current = _modalRootDialog;
        if (current?.IsVisible != true) return null;
        for (var depth = 0; depth < 8; depth++)
        {
            var child = current.OwnedWindows.OfType<Window>()
                .LastOrDefault(candidate => candidate.IsVisible);
            if (child is null) break;
            current = child;
        }
        return current;
    }

    // The low-level mouse hook runs before Windows can deliver the first click.
    // Keep the hook short; finish the foreground/z-order transfer in a queued
    // dispatcher callback. Showing both owner and active dialog is necessary:
    // a WPF ShowDialog disables its owner, and activating just the child may not
    // raise JNest's native hosted-window group over another app.
    private void RestoreHostedModalFromUserClick()
    {
        if (_modalActivationQueued) return;
        _modalActivationQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try
            {
                var dialog = GetTopmostHostedModal();
                if (dialog is null || _isClosing || WindowState == WindowState.Minimized) return;
                var dialogHwnd = new WindowInteropHelper(dialog).Handle;
                var ownerHwnd = new WindowInteropHelper(this).Handle;
                if (dialogHwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero
                    || !NativeMethods.IsWindow(dialogHwnd) || !NativeMethods.IsWindow(ownerHwnd))
                    return;

                const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                                   | NativeMethods.SWP_NOACTIVATE;
                // HWND_TOP is not HWND_TOPMOST. This is a one-shot user-click
                // response, never an always-on-top timer or background promotion.
                NativeMethods.SetWindowPos(ownerHwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, flags);
                RaiseShieldWithinModalGroup();
                NativeMethods.SetWindowPos(dialogHwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, flags);
                var setForeground = NativeMethods.SetForegroundWindow(dialogHwnd);
                var foreground = NativeMethods.GetForegroundWindow();
                var wpfActivated = false;
                if (!setForeground)
                    wpfActivated = dialog.Activate();
                if (ReconnectDiagnostics.Active)
                    ReconnectDiagnostics.Write($"modal-click-restore owner=0x{ownerHwnd.ToInt64():X} dialog=0x{dialogHwnd.ToInt64():X} setForeground={setForeground} immediateForeground=0x{foreground.ToInt64():X} wpfActivate={wpfActivated} {ModalClickSnapshot()}");
            }
            finally { _modalActivationQueued = false; }
        }));
    }

    private static bool IsModalActivationButtonDown(int message)
        => message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private static bool IsModalGuardMessage(int message) => message is
        WmLButtonDown or WmLButtonUp or WmLButtonDblClk or
        WmRButtonDown or WmRButtonUp or WmRButtonDblClk or
        WmMButtonDown or WmMButtonUp or WmMButtonDblClk or
        WmXButtonDown or WmXButtonUp or WmXButtonDblClk or
        WmMouseWheel or WmMouseHWheel;

    private int _guardLeft, _guardTop, _guardRight, _guardBottom;
    private int _queuedHookDiagnostics;

    private void QueueNativeHookDiagnostic(Func<string> messageFactory, bool mouse = false)
    {
        if (!ReconnectDiagnostics.Active) return;
        if (Interlocked.Increment(ref _queuedHookDiagnostics) > 32)
        {
            Interlocked.Decrement(ref _queuedHookDiagnostics);
            return;
        }
        try
        {
            // Never write a file or inspect modal WPF state inside WH_MOUSE_LL.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (!ReconnectDiagnostics.Active) return;
                    var message = messageFactory();
                    if (mouse) ReconnectDiagnostics.Mouse(message);
                    else ReconnectDiagnostics.Write(message);
                }
                catch (Exception exception)
                {
                    DiagnosticLogService.WriteErrorEvent("native-hook-diagnostics", exception);
                }
                finally
                {
                    Interlocked.Decrement(ref _queuedHookDiagnostics);
                }
            }));
        }
        catch
        {
            Interlocked.Decrement(ref _queuedHookDiagnostics);
        }
    }

    private void StartNativeCaptionGuard()
    {
        if (_captionMouseHook != IntPtr.Zero) return;
        _captionHookCallback = OnNativeCaptionMouse;
        _captionMouseHook = SetWindowsHookEx(WhMouseLl, _captionHookCallback, IntPtr.Zero, 0);
        if (_captionMouseHook == IntPtr.Zero)
        {
            _captionHookCallback = null;
            ShowTemporaryStatus("窗口按钮保护测试未启用");
            return;
        }
        // A named handler can be removed on shutdown/restart; an anonymous
        // subscription accumulated handlers after repeated Start/Stop cycles.
        _captionGuardRefreshTimer.Tick -= CaptionGuardRefreshTimer_Tick;
        _captionGuardRefreshTimer.Tick += CaptionGuardRefreshTimer_Tick;
        _captionGuardRefreshTimer.Start();
        CaptionGuardRefreshTimer_Tick(this, EventArgs.Empty);
    }

    private void CaptionGuardRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        try
        {
            RefreshNativeCaptionGuard();
            RefreshModalClickShield();
        }
        catch (Exception exception)
        {
            _guardedClient = IntPtr.Zero; // Fail open for input.
            DiagnosticLogService.WriteErrorEvent("caption-guard-refresh", exception);
        }
    }

    private void RefreshNativeCaptionGuard()
    {
        // Snapshot only on the dispatcher timer; hook callback must stay quick.
        _guardedClient = IntPtr.Zero;
        if (_isClosing || _isPreviewMode || WindowState == WindowState.Minimized
            || !IsVisible || !WindowHost.IsReady || !WindowHost.IsVisible
            || SelectedEntry is not { IsAttached: true, Handle: var hwnd }
            || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)
            || WindowHost.ActualWidth < 180 || WindowHost.ActualHeight < 60)
            return;

        // The host is a WPF HwndHost while the client is a separate top-level HWND.
        // PointToScreen transforms DIP coordinates to physical screen pixels.
        var topRight = WindowHost.PointToScreen(
            new Point(WindowHost.ActualWidth, 0));
        var left = WindowHost.PointToScreen(
            new Point(WindowHost.ActualWidth - 144, 0));
        var bottom = WindowHost.PointToScreen(
            new Point(WindowHost.ActualWidth, 38));
        _guardLeft = (int)Math.Floor(left.X);
        _guardTop = (int)Math.Floor(topRight.Y);
        _guardRight = (int)Math.Ceiling(topRight.X);
        _guardBottom = (int)Math.Ceiling(bottom.Y);
        _guardedOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _guardedClient = hwnd;
    }

    private IntPtr OnNativeCaptionMouse(int code, IntPtr msg, IntPtr data)
    {
        try
        {
            return ProcessNativeCaptionMouse(code, msg, data);
        }
        catch (Exception exception)
        {
            // Never let a managed exception escape a global low-level mouse
            // hook. Do not perform file I/O or logging on the hook callback.
            try
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => DiagnosticLogService.WriteErrorEvent("caption-mouse-hook", exception)));
            }
            catch { /* A shutting-down dispatcher cannot accept diagnostics. */ }
            return CallNextHookEx(_captionMouseHook, code, msg, data);
        }
    }

    private IntPtr ProcessNativeCaptionMouse(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && !_isClosing)
        {
            var command = unchecked((int)msg.ToInt64());
            if (_modalGuardDialogHwnd != IntPtr.Zero && IsModalGuardMessage(command))
            {
                var point = Marshal.PtrToStructure<MouseHookData>(data).Point;
                var target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y });
                var root = target == IntPtr.Zero ? IntPtr.Zero
                    : NativeMethods.GetAncestor(target, NativeMethods.GA_ROOT);
                var managerRoot = new WindowInteropHelper(this).Handle;
                // A physical click may target a child HWND of an attached client.
                // The root may also be the manager's disabled owner, depending
                // on native hit-test behavior during ShowDialog.
                var isHostedTarget = WindowHost.IsHostedWindow(root)
                    || WindowHost.IsHostedWindow(target);
                var isManagerTarget = root == managerRoot;
                var isShieldTarget = root == _modalClickShieldHwnd;
                if (ReconnectDiagnostics.Active && IsModalActivationButtonDown(command)
                    && (isHostedTarget || isManagerTarget || isShieldTarget))
                    QueueNativeHookDiagnostic(() => $"modal-click-before msg=0x{command:X} point={point.X},{point.Y} hit=0x{target.ToInt64():X} root=0x{root.ToInt64():X} {ModalClickSnapshot()}", mouse: true);
                // WPF's disabled modal owner can be returned by WindowFromPoint
                // instead of the separate embedded client top-level HWND.
                // Beta.2 only checked IsHostedWindow(root), missing that path.
                if (isHostedTarget || isManagerTarget)
                {
                    var current = GetTopmostHostedModal();
                    var currentHwnd = current is null
                        ? IntPtr.Zero : new WindowInteropHelper(current).Handle;
                    if (WindowActivityPolicy.ShouldRestoreModalFromHostedClick(
                            modalAvailable: currentHwnd != IntPtr.Zero,
                            isHostedTarget: isHostedTarget,
                            isButtonDown: IsModalActivationButtonDown(command),
                            isAlreadyForeground: NativeMethods.GetForegroundWindow() == currentHwnd,
                            isManagerTarget: isManagerTarget))
                    {
                        // In the shield's absence, do this while the genuine
                        // user gesture is being processed. Deferring *all*
                        // activation until after swallowing the click loses the
                        // normal Windows foreground opportunity.
                        // If another window got above the shield, this click
                        // cannot safely pass to the chat. Repair the owned
                        // surface in response to this user gesture and try to
                        // activate it before consuming the native click.
                        var shieldActivated = false;
                        if (_modalClickShieldHwnd != IntPtr.Zero
                            && NativeMethods.IsWindow(_modalClickShieldHwnd))
                        {
                            RaiseShieldWithinModalGroup();
                            shieldActivated = NativeMethods.SetForegroundWindow(_modalClickShieldHwnd);
                        }
                        var direct = NativeMethods.SetForegroundWindow(currentHwnd);
                        if (ReconnectDiagnostics.Active)
                            QueueNativeHookDiagnostic(() => $"modal-direct-click shieldActivate={shieldActivated} dialogActivate={direct} dialog=0x{currentHwnd.ToInt64():X} {ModalClickSnapshot()}");
                        RestoreHostedModalFromUserClick();
                    }

                    // Native clients must not receive modal-blocked input.
                    // For WPF owner clicks let OS/WPF process the normal event.
                    if (isHostedTarget)
                    {
                        if (ReconnectDiagnostics.Active)
                            QueueNativeHookDiagnostic(() => $"BLOCK modal mouse=0x{command:X} root=0x{root.ToInt64():X}");
                        return new IntPtr(1);
                    }
                }
            }

            if (_guardedClient != IntPtr.Zero
                && command is (WmLButtonDown or WmLButtonUp or WmLButtonDblClk
                    or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp
                    or WmXButtonDown or WmXButtonUp))
            {
                var point = Marshal.PtrToStructure<MouseHookData>(data).Point;
                if (point.X >= _guardLeft && point.X < _guardRight
                    && point.Y >= _guardTop && point.Y < _guardBottom)
                {
                    var foreground = NativeMethods.GetForegroundWindow();
                    if (foreground == _guardedClient || foreground == _guardedOwner)
                    {
                        if (ReconnectDiagnostics.Active) QueueNativeHookDiagnostic(() => $"BLOCK caption mouse=0x{command:X} foreground=0x{foreground.ToInt64():X}");
                        return new IntPtr(1);
                    }
                }
            }
        }
        return CallNextHookEx(_captionMouseHook, code, msg, data);
    }

    private void StopNativeCaptionGuard()
    {
        _guardedClient = IntPtr.Zero;
        DisposeModalClickShield();
        _modalRootDialog = null;
        _modalGuardDialogHwnd = IntPtr.Zero;
        _modalActivationQueued = false;
        _captionGuardRefreshTimer.Stop();
        _captionGuardRefreshTimer.Tick -= CaptionGuardRefreshTimer_Tick;
        if (_captionMouseHook != IntPtr.Zero)
        {
            if (UnhookWindowsHookEx(_captionMouseHook))
                _captionMouseHook = IntPtr.Zero;
            else
                DiagnosticLogService.WriteActivityEvent(
                    "caption-guard-unhook-failed-" + Marshal.GetLastWin32Error());
        }
        // If UnhookWindowsHookEx fails, keep the delegate rooted until the
        // process ends; releasing it could crash another mouse-hook callback.
        if (_captionMouseHook == IntPtr.Zero)
            _captionHookCallback = null;
    }
}
