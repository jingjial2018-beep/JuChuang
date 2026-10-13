using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Interop;
using JuChuang.Services;
using JuChuang.Models;

namespace JuChuang;

public partial class MainWindow
{
    private static readonly ImageSource UnreadTaskbarOverlay = CreateUnreadTaskbarOverlay();

    // Keep the JNest main HWND visible to the Windows taskbar independently of hosted clients.
    private void EnsureMainWindowTaskbarButton()
    {
        if (_isClosing) return;
        ShowInTaskbar = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        long desired = (exStyle | NativeMethods.WS_EX_APPWINDOW) & ~NativeMethods.WS_EX_TOOLWINDOW;
        if (desired == exStyle) return;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(desired));
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private void UpdateTaskbarUnreadOverlay()
    {
        if (_isClosing) return;

        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = Entries.Any(entry =>
            entry.AlertDisplayMode != AlertDisplayMode.None)
            ? UnreadTaskbarOverlay : null;
    }

    private static ImageSource CreateUnreadTaskbarOverlay()
    {
        var circle = new EllipseGeometry(new Point(8, 8), 6, 6);
        var drawing = new GeometryDrawing(
            Brushes.OrangeRed,
            new Pen(Brushes.White, 1.5),
            circle);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}

