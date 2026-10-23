using System.Windows;
using System.Windows.Interop;

namespace JuChuang;

/// <summary>
/// Consistent stationary placement for JNest-owned password dialogs.
/// This only blocks window moves; close, keyboard focus and button input still work.
/// </summary>
internal static class FixedModalWindow
{
    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(BlockMove);
        };
    }

    private static IntPtr BlockMove(IntPtr hwnd, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        const int wmNcLButtonDown = 0x00A1, wmNcLButtonDblClk = 0x00A3;
        const int wmSysCommand = 0x0112, htCaption = 2, scMove = 0xF010;
        if (((message == wmNcLButtonDown || message == wmNcLButtonDblClk)
                && wParam.ToInt64() == htCaption)
            || (message == wmSysCommand && (wParam.ToInt64() & 0xFFF0) == scMove))
            handled = true;
        return IntPtr.Zero;
    }
}
