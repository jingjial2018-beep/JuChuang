using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Interop;
using JuChuang.Services;
using JuChuang.Models;

namespace JuChuang;

public partial class MainWindow
{
    private static readonly ImageSource UnreadTaskbarOverlay = CreateUnreadTaskbarOverlay();
    private System.Drawing.Icon? _taskbarIconSmall;
    private System.Drawing.Icon? _taskbarIconLarge;

    // Explicitly apply the same embedded app.ico to both native taskbar icon sizes.
    // Keep icon objects alive: WM_SETICON does not take ownership of their handles.
    private void ApplyJNestTaskbarIcon()
    {
        if (_taskbarIconSmall is null || _taskbarIconLarge is null)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/app.ico");
                using var source = Application.GetResourceStream(uri)?.Stream;
                if (source is null) return;
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                buffer.Position = 0;
                using var original = new System.Drawing.Icon(buffer);
                _taskbarIconSmall = new System.Drawing.Icon(original, new System.Drawing.Size(16, 16));
                _taskbarIconLarge = new System.Drawing.Icon(original, new System.Drawing.Size(32, 32));
            }
            catch
            {
                _taskbarIconSmall?.Dispose();
                _taskbarIconLarge?.Dispose();
                _taskbarIconSmall = null;
                _taskbarIconLarge = null;
                return; // The normal WPF Icon property remains the fallback.
            }
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON,
            new IntPtr(NativeMethods.ICON_SMALL), _taskbarIconSmall.Handle);
        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON,
            new IntPtr(NativeMethods.ICON_BIG), _taskbarIconLarge.Handle);
    }

    private void ReleaseJNestTaskbarIcon()
    {
        _taskbarIconSmall?.Dispose();
        _taskbarIconLarge?.Dispose();
        _taskbarIconSmall = null;
        _taskbarIconLarge = null;
    }

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

