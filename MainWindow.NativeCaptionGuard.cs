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

    private void BeginModalInputGuard(Window dialog)
    {
        StartNativeCaptionGuard();
        _modalRootDialog = dialog;
        _modalGuardDialogHwnd = new WindowInteropHelper(dialog).Handle;
    }

    private void EndModalInputGuard()
    {
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

    private void RestoreHostedModalFromUserClick()
    {
        var dialog = GetTopmostHostedModal();
        if (dialog is null) return;
        var hwnd = new WindowInteropHelper(dialog).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        // This happens only in response to a real click on JNest's hosted
        // client. No global Topmost, no forcing focus from background timers.
        if (ReconnectDiagnostics.Active)
            ReconnectDiagnostics.Write($"modal-restore hwnd=0x{hwnd.ToInt64():X} previous=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");

        NativeMethods.SetForegroundWindow(hwnd);
        // Some foreground-lock configurations defer activation. WPF's normal
        // Activate is a secondary best-effort on the current dispatcher.
        if (NativeMethods.GetForegroundWindow() != hwnd)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (ReferenceEquals(GetTopmostHostedModal(), dialog)
                    && NativeMethods.IsWindow(hwnd))
                    dialog.Activate();
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
        _captionGuardRefreshTimer.Tick += (_, _) => RefreshNativeCaptionGuard();
        _captionGuardRefreshTimer.Start();
        RefreshNativeCaptionGuard();
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
        if (code >= 0 && !_isClosing)
        {
            var command = unchecked((int)msg.ToInt64());
            if (ReconnectDiagnostics.Active && command is (WmLButtonDown or WmLButtonUp or WmLButtonDblClk))
            {
                var pt = Marshal.PtrToStructure<MouseHookData>(data).Point;
                var target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = pt.X, Y = pt.Y });
                var root = target == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(target, NativeMethods.GA_ROOT);
                if (WindowHost.IsHostedWindow(root))
                    ReconnectDiagnostics.Mouse($"msg=0x{command:X} point={pt.X},{pt.Y} target=0x{target.ToInt64():X} root=0x{root.ToInt64():X} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X} modal=0x{_modalGuardDialogHwnd.ToInt64():X}");
            }
            if (_modalGuardDialogHwnd != IntPtr.Zero && IsModalGuardMessage(command))
            {
                var point = Marshal.PtrToStructure<MouseHookData>(data).Point;
                var target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y });
                var root = target == IntPtr.Zero ? IntPtr.Zero
                    : NativeMethods.GetAncestor(target, NativeMethods.GA_ROOT);
                if (root != _modalGuardDialogHwnd && WindowHost.IsHostedWindow(root))
                {
                    // Swallow clicks intended for native WeChat/WhatsApp while
                    // modal. On a button-down, first treat the user's gesture
                    // as a request to bring the active JNest dialog forward.
                    // Without this the WH_MOUSE_LL swallow also prevents the
                    // normal Windows foreground switch after alt-tab.
                    var foreground = NativeMethods.GetForegroundWindow();
                    var activeModal = GetTopmostHostedModal();
                    var activeModalHwnd = activeModal is null
                        ? IntPtr.Zero : new WindowInteropHelper(activeModal).Handle;
                    if (WindowActivityPolicy.ShouldRestoreModalFromHostedClick(
                            modalAvailable: activeModalHwnd != IntPtr.Zero,
                            isHostedTarget: true,
                            isButtonDown: IsModalActivationButtonDown(command),
                            isAlreadyForeground: foreground == activeModalHwnd))
                        RestoreHostedModalFromUserClick();
                    if (ReconnectDiagnostics.Active) ReconnectDiagnostics.Write($"BLOCK modal mouse=0x{command:X} root=0x{root.ToInt64():X}");
                    return new IntPtr(1);
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
                        if (ReconnectDiagnostics.Active) ReconnectDiagnostics.Write($"BLOCK caption mouse=0x{command:X} foreground=0x{foreground.ToInt64():X}");
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
        _modalRootDialog = null;
        _modalGuardDialogHwnd = IntPtr.Zero;
        _captionGuardRefreshTimer.Stop();
        if (_captionMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_captionMouseHook);
            _captionMouseHook = IntPtr.Zero;
        }
        _captionHookCallback = null;
    }
}
