namespace JuChuang.Services;

/// <summary>
/// Pure foreground-window rules shared by native overlay hosting and badge
/// calibration. Keeping the decision separate makes it regression-testable.
/// </summary>
internal static class WindowActivityPolicy
{
    internal static bool IsManagerContextForeground(
        IntPtr foregroundWindow,
        IntPtr managerRoot,
        IntPtr currentHostedWindow)
    {
        if (foregroundWindow == IntPtr.Zero || managerRoot == IntPtr.Zero)
        {
            return false;
        }

        return foregroundWindow == managerRoot
               || (currentHostedWindow != IntPtr.Zero && foregroundWindow == currentHostedWindow);
    }

    // Only an intentional button-down on JNest's native client can restore a
    // pending modal dialog. Wheel, up, injected background timers and unrelated
    // application clicks must never steal focus.
    internal static bool ShouldRestoreModalFromHostedClick(
        bool modalAvailable, bool isHostedTarget, bool isButtonDown, bool isAlreadyForeground,
        bool isManagerTarget = false)
        => modalAvailable && (isHostedTarget || isManagerTarget)
           && isButtonDown && !isAlreadyForeground;

    internal static bool ShouldPromoteHostedWindow(
        bool zOrderSuppressed,
        IntPtr foregroundWindow,
        IntPtr managerRoot,
        IntPtr currentHostedWindow)
        => !zOrderSuppressed
           && IsManagerContextForeground(foregroundWindow, managerRoot, currentHostedWindow);
}
