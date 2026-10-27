namespace JuChuang.Services;

/// <summary>
/// Conservative geometry fallback for WeChat 4.x Qt windows whose generic
/// "微信" title cannot distinguish a QR login dialog from the chat main view.
/// Explicit login titles and dedicated window classes take precedence in
/// ClientWindowDiscovery. This policy intentionally does NOT infer login solely
/// from window area: a 1027x739 chat window is less than 1M physical pixels.
/// </summary>
internal static class WeChatWindowRolePolicy
{
    public static bool IsCompactLoginShape(int windowWidthPx, int windowHeightPx, uint dpi)
    {
        if (windowWidthPx <= 0 || windowHeightPx <= 0) return false;

        // Per-monitor-v2 GetWindowRect sizes must be compared in the same
        // coordinate system across 100%, 150%, 200% and mixed-DPI monitors.
        var scale = (dpi >= 96 && dpi <= 768) ? dpi / 96.0 : 1.0;
        var widthDip = windowWidthPx / scale;
        var heightDip = windowHeightPx / scale;

        // Real PR #59 samples: login 296x388 at 96 DPI, main 1027x739 at
        // 96 DPI. The main chat is landscape; the QR-login UI is portrait or
        // nearly square. Avoid treating a resizable narrow chat as a login
        // merely because its area falls below an arbitrary pixel threshold.
        const double minWidthDip = 200;
        const double minHeightDip = 200;
        const double maxWidthDip = 700;
        const double maxHeightDip = 820;
        const double maxWidthHeightRatio = 1.10;

        return widthDip >= minWidthDip && heightDip >= minHeightDip
            && widthDip <= maxWidthDip && heightDip <= maxHeightDip
            && widthDip <= heightDip * maxWidthHeightRatio;
    }
}
