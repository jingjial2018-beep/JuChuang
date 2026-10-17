using System.Text;
using System.Windows;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private readonly Dictionary<IntPtr, int> _managedLoginPromptPids = [];
    private readonly HashSet<IntPtr> _loggedUnmatchedWeChatPrompts = [];

    // WeChat's duplicate-login "提示" is a separate top-level HWND. It is not
    // the centered QR window and must not be cropped as if it were that window.
    // Manage only compact prompts owned by an observed WeChat login process.
    private void SyncWeChatLoginPrompts()
    {
        if (_isClosing || _isPreviewMode || WindowState == WindowState.Minimized
            || !IsLoaded || ClientArea.ActualWidth < 100 || ClientArea.ActualHeight < 100)
            return;

        var attachedWeChat = Entries
            .Where(e => e.Kind == ClientKind.WeChat
                && e.IsAttached && e.ProcessId > 0 && NativeMethods.IsWindow(e.Handle))
            .ToArray();
        var loginPids = attachedWeChat.Select(e => e.ProcessId).ToHashSet();
        var selectedLogin = SelectedEntry is { Kind: ClientKind.WeChat,
            IsAttached: true } selected
            ? selected.ProcessId : -1;

        var observed = new HashSet<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) && !_managedLoginPromptPids.ContainsKey(hwnd))
                return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
            var pid = (int)rawPid;
            var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
            var ownerPid = 0;
            if (owner != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(owner, out var rawOwnerPid);
                ownerPid = (int)rawOwnerPid;
            }
            // A popup must be owned by the exact hosted WeChat HWND, not
            // merely share a PID with a candidate process.
            var hostEntry = attachedWeChat.FirstOrDefault(e =>
                e.Handle == owner && e.ProcessId == pid && ownerPid == pid);
            var associatedPid = hostEntry?.ProcessId ?? 0;
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length is < 1 or > 16)
                return true;
            var title = new StringBuilder(length + 2);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var nativeTitle = title.ToString().Trim();
            if (nativeTitle != "提示" && nativeTitle != "Weixin")
                return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)
                || rect.Width is < 130 or > 640 || rect.Height is < 80 or > 440)
                return true;
            var windowClass = new StringBuilder(128);
            NativeMethods.GetClassName(hwnd, windowClass, windowClass.Capacity);
            var recognized = (nativeTitle == "提示"
                || (nativeTitle == "Weixin"
                    && windowClass.ToString() == "Qt51514QWindowIcon"));
            if (!recognized || associatedPid <= 0 || !loginPids.Contains(associatedPid))
            {
                if (_loggedUnmatchedWeChatPrompts.Add(hwnd))
                    DiagnosticLogService.WriteActivityEvent(
                        $"wechat-login-prompt-unmatched pid={pid} ownerPid={ownerPid} class={windowClass}",
                        pid);
                return true;
            }
            observed.Add(hwnd);
            _managedLoginPromptPids[hwnd] = associatedPid;
            if (associatedPid == selectedLogin)
            {
                // PointToScreen returns device pixels, matching native window rects.
                var origin = ClientArea.PointToScreen(new Point(0, 0));
                var matrix = System.Windows.PresentationSource.FromVisual(ClientArea)
                    ?.CompositionTarget?.TransformToDevice;
                var scaleX = matrix?.M11 ?? 1.0;
                var scaleY = matrix?.M22 ?? 1.0;
                var x = (int)Math.Round(origin.X + (ClientArea.ActualWidth * scaleX - rect.Width) / 2);
                var y = (int)Math.Round(origin.Y + (ClientArea.ActualHeight * scaleY - rect.Height) / 2);
                // Popup is a native top-level window; keep it fully onscreen.
                var monitor = NativeMethods.MonitorFromWindow(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle,
                    NativeMethods.MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new NativeMethods.MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
                };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    x = Math.Clamp(x, monitorInfo.rcWork.Left,
                        Math.Max(monitorInfo.rcWork.Left, monitorInfo.rcWork.Right - rect.Width));
                    y = Math.Clamp(y, monitorInfo.rcWork.Top,
                        Math.Max(monitorInfo.rcWork.Top, monitorInfo.rcWork.Bottom - rect.Height));
                }
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
            {
                _managedLoginPromptPids.Remove(handle);
                continue;
            }
            NativeMethods.GetWindowThreadProcessId(handle, out var livePid);
            var currentOwner = NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER);
            if (!attachedWeChat.Any(e => e.Handle == currentOwner
                    && e.ProcessId == (int)livePid))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
                _managedLoginPromptPids.Remove(handle);
            }
        }
        _loggedUnmatchedWeChatPrompts.RemoveWhere(handle => !NativeMethods.IsWindow(handle));
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
