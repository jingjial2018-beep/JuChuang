using System.Runtime.InteropServices;
using System.Text;

namespace JuChuang.Services;

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int GWLP_HWNDPARENT = -8;
    internal const long WS_CHILD = 0x40000000L;
    internal const long WS_POPUP = 0x80000000L;
    internal const long WS_VISIBLE = 0x10000000L;
    internal const long WS_CAPTION = 0x00C00000L;
    internal const long WS_THICKFRAME = 0x00040000L;
    internal const long WS_SYSMENU = 0x00080000L;
    internal const long WS_MINIMIZEBOX = 0x00020000L;
    internal const long WS_MAXIMIZEBOX = 0x00010000L;
    internal const long WS_CLIPCHILDREN = 0x02000000L;
    internal const long WS_CLIPSIBLINGS = 0x04000000L;
    internal const long WS_EX_APPWINDOW = 0x00040000L;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const uint GW_OWNER = 4;
    internal const uint GA_ROOT = 2;
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal const int DWMWA_NCRENDERING_POLICY = 2;
    internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_CAPTION_COLOR = 35;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMNCRP_USEWINDOWSTYLE = 0;
    internal const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    // v0.2.1 forced #E1E1E6. Treat that value as a legacy override and return
    // it to DWM's native theme so an already-running client also recovers.
    internal const int LegacyHostedClientChromeColorRef = 0x00E6E1E1;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_ACTIVATEAPP = 0x001C;
    internal const uint WM_NCACTIVATE = 0x0086;
    internal const uint WM_CANCELMODE = 0x001F;
    internal const uint WM_EXITSIZEMOVE = 0x0232;
    internal const int WA_ACTIVE = 1;
    internal const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    internal const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    internal const int HSHELL_FLASH = 0x8006;
    internal const uint FLASHW_STOP = 0x00000000;
    internal const uint FLASHW_CAPTION = 0x00000001;
    internal const uint FLASHW_TRAY = 0x00000002;
    internal const uint FLASHW_ALL = FLASHW_CAPTION | FLASHW_TRAY;
    internal const uint FLASHW_TIMER = 0x00000004;
    internal const uint FLASHW_TIMERNOFG = 0x0000000C;
    internal const uint RDW_INVALIDATE = 0x0001;
    internal const uint RDW_ERASE = 0x0004;
    internal const uint RDW_UPDATENOW = 0x0100;
    internal const uint RDW_FRAME = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FLASHWINFO
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Flags;
        internal uint Count;
        internal uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal int Width => Math.Max(0, Right - Left);
        internal int Height => Math.Max(0, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    // PrintWindow 标志：强制从硬件加速（DirectUI/WebView）窗口回读完整内容。
    // 微信 4.x（Qt）与 WhatsApp（WebView2）的自绘窗口若不带此标志会截到黑屏。
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    internal readonly record struct DwmChromeState(
        bool HasBorderColor,
        int BorderColor,
        bool HasCaptionColor,
        int CaptionColor,
        bool HasCornerPreference,
        int CornerPreference,
        bool HasNonClientPolicy,
        int NonClientPolicy,
        bool HasTransitionsDisabled,
        int TransitionsDisabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRectRgn(
        int left,
        int top,
        int right,
        int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlashWindowEx(ref FLASHWINFO flashInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RedrawWindow(
        IntPtr hWnd,
        IntPtr updateRect,
        IntPtr updateRegion,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(
        IntPtr hwnd,
        int dwAttribute,
        out int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(
        IntPtr hwnd,
        int dwAttribute,
        out RECT pvAttribute,
        int cbAttribute);

    internal static bool TryGetExtendedFrameBounds(IntPtr hWnd, out RECT rect)
    {
        return DwmGetWindowAttributeRect(
            hWnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            out rect,
            Marshal.SizeOf<RECT>()) == 0;
    }

    internal static DwmChromeState CaptureNativeChromeState(IntPtr hWnd)
    {
        var hasBorder = DwmGetWindowAttributeInt(
            hWnd,
            DWMWA_BORDER_COLOR,
            out var border,
            sizeof(int)) == 0;
        var hasCaption = DwmGetWindowAttributeInt(
            hWnd,
            DWMWA_CAPTION_COLOR,
            out var caption,
            sizeof(int)) == 0;
        var hasCornerPreference = DwmGetWindowAttributeInt(
            hWnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            out var cornerPreference,
            sizeof(int)) == 0;
        var hasNonClientPolicy = DwmGetWindowAttributeInt(
            hWnd,
            DWMWA_NCRENDERING_POLICY,
            out var nonClientPolicy,
            sizeof(int)) == 0;
        var hasTransitionsDisabled = DwmGetWindowAttributeInt(
            hWnd,
            DWMWA_TRANSITIONS_FORCEDISABLED,
            out var transitionsDisabled,
            sizeof(int)) == 0;

        // Releases of v0.2.1 could leave this forced color on a detached client.
        // DWM default is the closest representation of the original native state.
        if (hasBorder && border == LegacyHostedClientChromeColorRef)
        {
            border = DwmColorDefault;
        }
        if (hasCaption && caption == LegacyHostedClientChromeColorRef)
        {
            caption = DwmColorDefault;
        }

        return new DwmChromeState(
            hasBorder,
            border,
            hasCaption,
            caption,
            hasCornerPreference,
            cornerPreference,
            hasNonClientPolicy,
            nonClientPolicy,
            hasTransitionsDisabled,
            transitionsDisabled);
    }

    internal static void ApplyNativeChromeState(IntPtr hWnd, DwmChromeState state)
    {
        var borderColor = state.HasBorderColor ? state.BorderColor : DwmColorDefault;
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

        var captionColor = state.HasCaptionColor ? state.CaptionColor : DwmColorDefault;
        DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

        var preference = state.HasCornerPreference ? state.CornerPreference : 0;
        DwmSetWindowAttribute(
            hWnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));

        var policy = state.HasNonClientPolicy ? state.NonClientPolicy : DWMNCRP_USEWINDOWSTYLE;
        DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

        var disabled = state.HasTransitionsDisabled ? state.TransitionsDisabled : 0;
        DwmSetWindowAttribute(
            hWnd,
            DWMWA_TRANSITIONS_FORCEDISABLED,
            ref disabled,
            sizeof(int));
    }
}
