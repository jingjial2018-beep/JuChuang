using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    // A translucent *input* surface above the independently owned native
    // WeChat/WhatsApp HWND, used only while an owned JNest modal is visible.
    // This is an ordinary owned popup (never HWND_TOPMOST or WS_EX_NOACTIVATE).
    // The real click lands in JNest's process instead of being discarded by
    // WH_MOUSE_LL before Windows has a chance to activate JNest.
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivateAndEat = 4;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private HwndSource? _modalClickShield;
    private IntPtr _modalClickShieldHwnd;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hwnd, bool enabled);

    private void EnsureModalClickShield()
    {
        if (_modalClickShieldHwnd != IntPtr.Zero || _modalGuardDialogHwnd == IntPtr.Zero
            || _isClosing || WindowState == WindowState.Minimized || !WindowHost.IsReady)
            return;

        try
        {
            var parent = new WindowInteropHelper(this).Handle;
            if (parent == IntPtr.Zero || GetTopmostHostedModal() is null) return;

            // The window is deliberately created after ShowDialog has disabled
            // the owner's existing thread windows. It is a non-topmost popup
            // owned by JNest and can receive normal Win32 mouse activation.
            var p = new HwndSourceParameters("JNest modal input shield")
            {
                ParentWindow = parent,
                WindowStyle = unchecked((int)(NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE)),
                ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW | WsExLayered,
                PositionX = 0,
                PositionY = 0,
                Width = 1,
                Height = 1
            };
            _modalClickShield = new HwndSource(p);
            _modalClickShield.AddHook(ModalShieldWindowProc);
            _modalClickShieldHwnd = _modalClickShield.Handle;
            if (!SetLayeredWindowAttributes(_modalClickShieldHwnd, 0, 1, LwaAlpha))
            {
                ReconnectDiagnostics.Write("modal-shield failed to set alpha");
                DisposeModalClickShield();
                return;
            }
            RefreshModalClickShield();
            // Keep actual Settings/About/password dialog *above* the shield.
            var active = GetTopmostHostedModal();
            if (active is not null)
            {
                var modalHwnd = new WindowInteropHelper(active).Handle;
                if (modalHwnd != IntPtr.Zero)
                    NativeMethods.SetWindowPos(modalHwnd, NativeMethods.HWND_TOP,
                        0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                            | NativeMethods.SWP_NOACTIVATE);
            }
            ReconnectDiagnostics.Write($"modal-shield created hwnd=0x{_modalClickShieldHwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            ReconnectDiagnostics.Write($"modal-shield create failed {ex.GetType().Name}");
            DisposeModalClickShield();
            // The low-level mouse hook remains as the fallback.
        }
    }

    private void RefreshModalClickShield()
    {
        if (_modalClickShieldHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_modalClickShieldHwnd))
            return;

        if (_modalGuardDialogHwnd == IntPtr.Zero || _isClosing || !IsVisible
            || WindowState == WindowState.Minimized || !WindowHost.IsVisible
            || WindowHost.ActualWidth < 1 || WindowHost.ActualHeight < 1)
        {
            NativeMethods.ShowWindow(_modalClickShieldHwnd, NativeMethods.SW_HIDE);
            return;
        }

        try
        {
            var topLeft = WindowHost.PointToScreen(new Point(0, 0));
            var bottomRight = WindowHost.PointToScreen(
                new Point(WindowHost.ActualWidth, WindowHost.ActualHeight));
            int x = (int)Math.Round(topLeft.X);
            int y = (int)Math.Round(topLeft.Y);
            int width = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
            int height = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));

            // Nested ShowDialog may disable other UI-thread HWNDs. Re-enable
            // the shield so the user can always click back from another app.
            if (!IsWindowEnabled(_modalClickShieldHwnd))
                EnableWindow(_modalClickShieldHwnd, true);

            // Never promote its Z order from a background timer: doing so
            // could put a click shield above Chrome or another application.
            NativeMethods.SetWindowPos(_modalClickShieldHwnd, NativeMethods.HWND_TOP,
                x, y, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
        }
        catch (InvalidOperationException)
        {
            // Host was detached during a window/layout transition.
        }
    }

    private IntPtr ModalShieldWindowProc(IntPtr hwnd, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            var modal = GetTopmostHostedModal();
            if (modal is not null && _modalGuardDialogHwnd != IntPtr.Zero)
            {
                var target = new WindowInteropHelper(modal).Handle;
                if (target != IntPtr.Zero && NativeMethods.IsWindow(target))
                {
                    // This callback is running on the receiving process's
                    // actual mouse event, not in a delayed low-level hook.
                    var success = NativeMethods.SetForegroundWindow(target);
                    if (ReconnectDiagnostics.Active)
                        ReconnectDiagnostics.Write($"modal-shield-mouseactivate target=0x{target.ToInt64():X} result={success}");
                    RestoreHostedModalFromUserClick();
                }
            }
            handled = true;
            return new IntPtr(MaNoActivateAndEat); // no chat operation
        }
        if (message is WmLButtonDown or WmLButtonUp or WmLButtonDblClk
            or WmRButtonDown or WmRButtonUp or WmRButtonDblClk
            or WmMButtonDown or WmMButtonUp or WmMButtonDblClk
            or WmXButtonDown or WmXButtonUp or WmXButtonDblClk
            or WmMouseWheel or WmMouseHWheel)
        {
            if (IsModalActivationButtonDown(message))
                RestoreHostedModalFromUserClick();
            handled = true;
            return IntPtr.Zero; // never forward to native client
        }
        return IntPtr.Zero;
    }

    private void DisposeModalClickShield()
    {
        _modalClickShieldHwnd = IntPtr.Zero;
        var shield = _modalClickShield;
        _modalClickShield = null;
        if (shield is null) return;
        try { shield.RemoveHook(ModalShieldWindowProc); shield.Dispose(); }
        catch (InvalidOperationException) { }
    }
}
