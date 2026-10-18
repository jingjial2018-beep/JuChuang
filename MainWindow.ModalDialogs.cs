using System;
using System.Windows;
using System.Windows.Interop;
using JuChuang.Services;

namespace JuChuang;

// The native WeChat / WhatsApp HWNDs remain top-level windows. Temporarily park
// them for WPF modal dialogs rather than merely suppressing their periodic Z-order.
// Both dialogs share identical positioning and input restrictions.
public partial class MainWindow
{
    private void ShowHostedModalDialog(Window dialog)
    {
        WindowHost.SuspendOverlayZOrder();
        WindowHost.SuspendForModalDialog();
        try
        {
            dialog.Owner = this;
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
            };
            dialog.ShowDialog();
        }
        finally
        {
            WindowHost.ResumeAfterModalDialog();
            WindowHost.ResumeOverlayZOrder();
        }
    }
}
