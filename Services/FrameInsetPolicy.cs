namespace JuChuang.Services;

/// <summary>
/// Four physical-pixel distances from the Win32 HWND bounds to the DWM
/// extended visible frame. This is not a fixed DIP offset.
/// </summary>
public readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom);

public readonly record struct FrameInsetDecision(
    FrameInsets Effective,
    FrameInsets? Pending,
    int PendingCount,
    bool Changed);

/// <summary>
/// Pure, deterministic protection against frame inset drift. DWM frame bounds
/// can transiently change while native apps redraw; a candidate must be
/// observed twice on consecutive one-second watchdog passes before applying.
/// </summary>
public static class FrameInsetPolicy
{
    public static FrameInsets? Measure(
        int windowLeft, int windowTop, int windowRight, int windowBottom,
        int frameLeft, int frameTop, int frameRight, int frameBottom)
    {
        if (windowRight - windowLeft < 80 || windowBottom - windowTop < 80 ||
            frameRight <= frameLeft || frameBottom <= frameTop)
            return null;

        var left = frameLeft - windowLeft;
        var top = frameTop - windowTop;
        var right = windowRight - frameRight;
        var bottom = windowBottom - frameBottom;

        // Reject transient, virtualized, off-screen or unrelated geometries.
        // Never clamp impossible measurements to a seemingly plausible offset.
        if (left < 0 || top < 0 || right < 0 || bottom < 0 ||
            left > 20 || top > 20 || right > 20 || bottom > 20)
            return null;

        return new FrameInsets(left, top, right, bottom);
    }

    public static FrameInsetDecision Observe(
        FrameInsets effective, FrameInsets? pending, int pendingCount,
        FrameInsets? measurement)
    {
        if (measurement is null || measurement.Value == effective)
            return new FrameInsetDecision(effective, null, 0, false);

        if (pending == measurement && pendingCount >= 1)
            return new FrameInsetDecision(measurement.Value, null, 0, true);

        return new FrameInsetDecision(effective, measurement, 1, false);
    }
}
