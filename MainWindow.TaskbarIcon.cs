using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JuChuang;

public partial class MainWindow
{
    private Icon? _taskbarLargeIcon;
    private Icon? _titlebarSmallIcon;

    private void ApplyTaskbarLargeIcon()
    {
        // WM_SETICON / ICON_BIG changes the large HWND icon used for taskbar and Alt+Tab.
        // ICON_SMALL, Window.Icon, the titlebar logo and notification assets remain unchanged.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "taskbar.ico");
        if (!System.IO.File.Exists(path)) return;
        try
        {
            var icon = new Icon(path);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) { icon.Dispose(); return; }
            SendMessage(hwnd, 0x0080, new IntPtr(1), icon.Handle);
            _taskbarLargeIcon?.Dispose();
            _taskbarLargeIcon = icon;
            // Preserve the original small titlebar logo.
            var original = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(original))
            {
                var small = new Icon(original, new System.Drawing.Size(16, 16));
                SendMessage(hwnd, 0x0080, IntPtr.Zero, small.Handle);
                _titlebarSmallIcon?.Dispose();
                _titlebarSmallIcon = small;
            }
        }
        catch (Exception)
        {
            // Missing/invalid optional icon must not interfere with window startup.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
