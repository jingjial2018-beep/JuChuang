using System.Runtime.InteropServices;
using System.Windows;
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
        if (code >= 0 && _guardedClient != IntPtr.Zero && !_isClosing)
        {
            var command = unchecked((int)msg.ToInt64());
            if (command is WmLButtonDown or WmLButtonUp or WmLButtonDblClk
                or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp
                or WmXButtonDown or WmXButtonUp)
            {
                var point = Marshal.PtrToStructure<MouseHookData>(data).Point;
                if (point.X >= _guardLeft && point.X < _guardRight
                    && point.Y >= _guardTop && point.Y < _guardBottom)
                {
                    var foreground = NativeMethods.GetForegroundWindow();
                    // Never block other apps or a popped-out client.
                    if (foreground == _guardedClient || foreground == _guardedOwner)
                        return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_captionMouseHook, code, msg, data);
    }

    private void StopNativeCaptionGuard()
    {
        _guardedClient = IntPtr.Zero;
        _captionGuardRefreshTimer.Stop();
        if (_captionMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_captionMouseHook);
            _captionMouseHook = IntPtr.Zero;
        }
        _captionHookCallback = null;
    }
}
