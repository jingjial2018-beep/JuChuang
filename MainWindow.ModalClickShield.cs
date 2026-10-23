using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private const int MaActivateAndEat = 2;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private IntPtr _modalClickShieldHwnd;
    private ShieldWindowProc? _modalShieldWindowProc;
    private IntPtr _modalShieldPreviousProc;
    private delegate IntPtr ShieldWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd,
        uint message, IntPtr wParam, IntPtr lParam);

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
            // HwndSource removes WS_EX_LAYERED during HWND creation. On beta.5
            // SetLayeredWindowAttributes therefore failed with ERROR_INVALID_PARAMETER
            // and the shield was always disposed. A native popup retains the
            // layered style. Use BUTTON rather than STATIC: Windows hit testing
            // skips static text windows even when they are visible and above
            // the client, which left beta.6 clicks landing in the chat HWND.
            _modalClickShieldHwnd = NativeMethods.CreateWindowEx(
                checked((int)(NativeMethods.WS_EX_TOOLWINDOW | WsExLayered)),
                "BUTTON", string.Empty, unchecked((int)NativeMethods.WS_POPUP),
                0, 0, 1, 1, parent, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            if (_modalClickShieldHwnd == IntPtr.Zero)
            {
                ReconnectDiagnostics.Write($"modal-shield create HWND failed error={Marshal.GetLastWin32Error()}");
                return;
            }
            if (!SetLayeredWindowAttributes(_modalClickShieldHwnd, 0, 1, LwaAlpha))
            {
                ReconnectDiagnostics.Write($"modal-shield failed to set alpha error={Marshal.GetLastWin32Error()} exstyle=0x{NativeMethods.GetWindowLongPtr(_modalClickShieldHwnd, NativeMethods.GWL_EXSTYLE).ToInt64():X}");
                DisposeModalClickShield();
                return;
            }
            _modalShieldWindowProc = ModalShieldWindowProc;
            _modalShieldPreviousProc = NativeMethods.SetWindowLongPtr(_modalClickShieldHwnd,
                NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_modalShieldWindowProc));
            if (_modalShieldPreviousProc == IntPtr.Zero)
            {
                ReconnectDiagnostics.Write($"modal-shield subclass failed error={Marshal.GetLastWin32Error()}");
                DisposeModalClickShield();
                return;
            }
            RefreshModalClickShield();
            // Establish the owned shield above the separate native client once,
            // when the modal opens. The layout timer must not raise it later
            // while Chrome or another application is in the foreground.
            RaiseShieldWithinModalGroup();
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

    private void RaiseShieldWithinModalGroup()
    {
        if (_modalClickShieldHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_modalClickShieldHwnd))
            return;
        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE;
        var shieldRaised = NativeMethods.SetWindowPos(_modalClickShieldHwnd,
            NativeMethods.HWND_TOP, 0, 0, 0, 0, flags);
        var modal = GetTopmostHostedModal();
        var modalHwnd = modal is null ? IntPtr.Zero : new WindowInteropHelper(modal).Handle;
        var dialogRaised = modalHwnd != IntPtr.Zero && NativeMethods.IsWindow(modalHwnd)
            && NativeMethods.SetWindowPos(modalHwnd, NativeMethods.HWND_TOP,
                0, 0, 0, 0, flags);
        if (ReconnectDiagnostics.Active)
            ReconnectDiagnostics.Write($"modal-shield-order shieldRaised={shieldRaised} dialogRaised={dialogRaised} {ModalClickSnapshot()}");
    }

    private static string ZOrderBetween(IntPtr shield, IntPtr client)
    {
        if (shield == IntPtr.Zero || client == IntPtr.Zero) return "unknown";
        // GW_HWNDPREV follows the actual desktop Z order, including the
        // separately owned native client. Bound the walk against HWND reuse.
        var cursor = client;
        for (var i = 0; i < 512 && cursor != IntPtr.Zero; i++)
        {
            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
            if (cursor == shield) return "shield-above-client";
        }
        cursor = shield;
        for (var i = 0; i < 512 && cursor != IntPtr.Zero; i++)
        {
            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
            if (cursor == client) return "client-above-shield";
        }
        return "unknown";
    }

    private string ModalClickSnapshot()
    {
        var client = SelectedEntry?.Handle ?? IntPtr.Zero;
        var shield = _modalClickShieldHwnd;
        return $"shield=0x{shield.ToInt64():X} visible={NativeMethods.IsWindowVisible(shield)} enabled={IsWindowEnabled(shield)} "
            + $"bounds={WindowRectText(shield)} "
            + $"client=0x{client.ToInt64():X} visible={NativeMethods.IsWindowVisible(client)} enabled={IsWindowEnabled(client)} "
            + $"bounds={WindowRectText(client)} "
            + $"z={ZOrderBetween(shield, client)} foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X}";
    }

    private static string WindowRectText(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect)
            ? $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}"
            : "unknown";

    private IntPtr ModalShieldWindowProc(IntPtr hwnd, uint message, IntPtr wParam,
        IntPtr lParam)
    {
        var mouseMessage = (int)message;
        if (message == WmMouseActivate)
        {
            if (ReconnectDiagnostics.Active)
                ReconnectDiagnostics.Mouse($"shield-mouseactivate before {ModalClickSnapshot()}");
            // Windows first activates the clicked shield. The queued callback
            // then transfers foreground to the deepest visible modal. Eating
            // the click here prevents any operation in the native chat.
            RestoreHostedModalFromUserClick();
            return new IntPtr(MaActivateAndEat);
        }
        if (mouseMessage is WmLButtonDown or WmLButtonUp or WmLButtonDblClk
            or WmRButtonDown or WmRButtonUp or WmRButtonDblClk
            or WmMButtonDown or WmMButtonUp or WmMButtonDblClk
            or WmXButtonDown or WmXButtonUp or WmXButtonDblClk
            or WmMouseWheel or WmMouseHWheel)
        {
            if (IsModalActivationButtonDown(mouseMessage))
                RestoreHostedModalFromUserClick();
            return IntPtr.Zero; // never forward to native client
        }
        return CallWindowProc(_modalShieldPreviousProc, hwnd, message, wParam, lParam);
    }

    private void DisposeModalClickShield()
    {
        var hwnd = _modalClickShieldHwnd;
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
        {
            if (_modalShieldPreviousProc != IntPtr.Zero)
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC, _modalShieldPreviousProc);
            NativeMethods.DestroyWindow(hwnd);
        }
        _modalClickShieldHwnd = IntPtr.Zero;
        _modalShieldPreviousProc = IntPtr.Zero;
        _modalShieldWindowProc = null;
    }
}
