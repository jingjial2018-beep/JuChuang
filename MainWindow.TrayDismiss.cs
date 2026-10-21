using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace JuChuang;

public partial class MainWindow
{
    // WPF ContextMenu is detached from the hidden owner HWND. Windows shell tray clicks
    // do not reliably release its mouse capture, so observe mouse-button transitions
    // while this one popup is open; never intercept or swallow a global mouse event.
    private readonly DispatcherTimer _trayDismissTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(20)
    };
    private bool _trayLeftWasDown;
    private bool _trayRightWasDown;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsMouseButtonDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void BeginTrayMenuDismissTracking()
    {
        _trayLeftWasDown = IsMouseButtonDown(0x01);
        _trayRightWasDown = IsMouseButtonDown(0x02);
        _trayDismissTimer.Tick -= TrayDismissTimer_Tick;
        _trayDismissTimer.Tick += TrayDismissTimer_Tick;
        _trayDismissTimer.Start();
    }

    private void EndTrayMenuDismissTracking()
    {
        _trayDismissTimer.Stop();
        _trayDismissTimer.Tick -= TrayDismissTimer_Tick;
    }

    private void TrayDismissTimer_Tick(object? sender, EventArgs e)
    {
        if (_trayMenu is not { IsOpen: true })
        {
            EndTrayMenuDismissTracking();
            return;
        }

        bool left = IsMouseButtonDown(0x01);
        bool right = IsMouseButtonDown(0x02);
        bool newlyPressed = (left && !_trayLeftWasDown) || (right && !_trayRightWasDown);
        _trayLeftWasDown = left;
        _trayRightWasDown = right;
        if (!newlyPressed) return;

        // Do not interfere with real menu-item clicks.
        var pointer = WinForms.Cursor.Position;
        try
        {
            var origin = _trayMenu.PointToScreen(new Point(0, 0));
            var bottomRight = _trayMenu.PointToScreen(new Point(
                _trayMenu.ActualWidth, _trayMenu.ActualHeight));
            bool inside = pointer.X >= origin.X && pointer.X < bottomRight.X
                && pointer.Y >= origin.Y && pointer.Y < bottomRight.Y;
            if (inside) return;
        }
        catch (InvalidOperationException)
        {
            // Popup was disposed/recreated between WPF input events.
        }

        // Clicking a different tray icon must not keep JNest's menu on top.
        _trayMenu.IsOpen = false;
    }
}
