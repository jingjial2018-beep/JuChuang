using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JuChuang.Services;

namespace JuChuang;

// Keep hosted native HWNDs visible during modal dialogs. Prevent background
// client input while the dialog is open, without using global Topmost.
// Both dialogs share identical positioning and input restrictions.
public partial class MainWindow
{
    private static void LogModalZOrder(string text)
        => DiagnosticLogService.WriteDebugEvent("modal-zorder:" + text);

    private void ShowHostedModalDialog(Window dialog)
    {
        WindowHost.SuspendOverlayZOrder();
        try
        {
            dialog.Owner = this;
            LogModalZOrder($"open dialog={dialog.GetType().Name} owner=0x{new WindowInteropHelper(this).Handle.ToInt64():X}");
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.SourceInitialized += (_, _) =>
            {
                // WPF coordinates are DIPs for both the owner and dialog. The
                // actual owner bounds already reflect expanded/collapsed sidebar,
                // maximized state and the current display DPI.
                var ownerWidth = ActualWidth;
                var ownerHeight = ActualHeight;
                var dialogWidth = dialog.Width;
                var dialogHeight = dialog.Height;
                if (ownerWidth > 0 && ownerHeight > 0
                    && double.IsFinite(dialogWidth) && double.IsFinite(dialogHeight))
                {
                    dialog.Left = Left + (ownerWidth - dialogWidth) / 2;
                    dialog.Top = Top + (ownerHeight - dialogHeight) / 2;
                }

                if (PresentationSource.FromVisual(dialog) is HwndSource hwnd)
                    hwnd.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        const int wmWindowPosChanging = 0x0046;
                        if (message == wmWindowPosChanging && lParam != IntPtr.Zero)
                        {
                            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                            if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 &&
                                pos.hwndInsertAfter == NativeMethods.HWND_TOPMOST)
                            {
                                LogModalZOrder($"intercept hwnd=0x{window.ToInt64():X} insert=TOPMOST flags=0x{pos.flags:X}");
                                pos.hwndInsertAfter = NativeMethods.HWND_TOP;
                                Marshal.StructureToPtr(pos, lParam, false);
                            }
                        }
                        const int wmNcLButtonDown = 0x00A1;
                        const int wmNcLButtonDblClk = 0x00A3;
                        const int wmSysCommand = 0x0112;
                        const int htCaption = 2;
                        const int scMove = 0xF010;
                        if ((message == wmNcLButtonDown || message == wmNcLButtonDblClk)
                            && wParam.ToInt64() == htCaption)
                        {
                            handled = true;
                            return IntPtr.Zero;
                        }
                        if (message == wmSysCommand && ((wParam.ToInt64() & 0xFFF0) == scMove))
                        {
                            handled = true;
                            return IntPtr.Zero;
                        }
                        return IntPtr.Zero;
                    });
                if (PresentationSource.FromVisual(dialog) is HwndSource modalSource)
                {
                    var dialogHwnd = modalSource.Handle;
                    var exStyle = NativeMethods.GetWindowLongPtr(dialogHwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    LogModalZOrder($"created dialog={dialog.GetType().Name} hwnd=0x{dialogHwnd.ToInt64():X} exstyle=0x{exStyle:X} topmost={(exStyle & NativeMethods.WS_EX_TOPMOST) != 0}");
                    if ((exStyle & NativeMethods.WS_EX_TOPMOST) != 0)
                    {
                        var ok = NativeMethods.SetWindowPos(dialogHwnd, NativeMethods.HWND_NOTOPMOST,
                            0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                        LogModalZOrder($"initial-notopmost hwnd=0x{dialogHwnd.ToInt64():X} success={ok}");
                    }
                }
                BeginModalInputGuard(dialog);
            };
            // ShowDialog disables the other UI-thread windows. Create the
            // native hit-test shield after that transition, not before it.
            // The shield is never globally topmost and is disposed on close.
            dialog.Loaded += (_, _) => _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(EnsureModalClickShield));
            dialog.Closed += (_, _) =>
            {
                LogModalZOrder($"closed dialog={dialog.GetType().Name}");
                EndModalInputGuard();
            };
            dialog.ShowDialog();
        }
        finally
        {
            EndModalInputGuard();
            WindowHost.ResumeOverlayZOrder();
        }
    }
}
