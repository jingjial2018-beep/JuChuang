using System.Text;
using System.Windows;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private readonly Dictionary<IntPtr, int> _managedLoginPromptPids = [];

    // WeChat's duplicate-login "提示" is a separate top-level HWND. It is not
    // the centered QR window and must not be cropped as if it were that window.
    // Manage only compact prompts owned by an observed WeChat login process.
    private void SyncWeChatLoginPrompts()
    {
        if (_isClosing || _isPreviewMode || WindowState == WindowState.Minimized
            || !IsLoaded || ClientArea.ActualWidth < 100 || ClientArea.ActualHeight < 100)
            return;

        var loginPids = Entries
            .Where(e => e.Kind == ClientKind.WeChat && e.SessionState == ClientSessionState.Login
                && e.IsAttached && e.ProcessId > 0)
            .Select(e => e.ProcessId)
            .ToHashSet();
        var selectedLogin = SelectedEntry is { Kind: ClientKind.WeChat,
            SessionState: ClientSessionState.Login, IsAttached: true } selected
            ? selected.ProcessId : -1;

        var observed = new HashSet<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) && !_managedLoginPromptPids.ContainsKey(hwnd))
                return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
            var pid = (int)rawPid;
            if (!loginPids.Contains(pid) && !_managedLoginPromptPids.ContainsKey(hwnd))
                return true;
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length is < 1 or > 16)
                return true;
            var title = new StringBuilder(length + 2);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            if (!string.Equals(title.ToString().Trim(), "提示", StringComparison.Ordinal))
                return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)
                || rect.Width is < 130 or > 640 || rect.Height is < 80 or > 440)
                return true;
            var windowClass = new StringBuilder(128);
            NativeMethods.GetClassName(hwnd, windowClass, windowClass.Capacity);
            if (!windowClass.ToString().StartsWith("Qt", StringComparison.OrdinalIgnoreCase))
                return true;
            observed.Add(hwnd);
            _managedLoginPromptPids[hwnd] = pid;
            if (pid == selectedLogin)
            {
                // PointToScreen returns device pixels, matching native window rects.
                var origin = ClientArea.PointToScreen(new Point(0, 0));
                var x = (int)Math.Round(origin.X + (ClientArea.ActualWidth - rect.Width) / 2);
                var y = (int)Math.Round(origin.Y + (ClientArea.ActualHeight - rect.Height) / 2);
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            else
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
            return true;
        }, IntPtr.Zero);
        foreach (var handle in _managedLoginPromptPids.Keys.ToArray())
        {
            if (!NativeMethods.IsWindow(handle))
                _managedLoginPromptPids.Remove(handle);
        }
    }

    private void RestoreWeChatLoginPrompts()
    {
        foreach (var handle in _managedLoginPromptPids.Keys.ToArray())
        {
            if (NativeMethods.IsWindow(handle))
                NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
        }
        _managedLoginPromptPids.Clear();
    }
}
