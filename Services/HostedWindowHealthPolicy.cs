namespace JuChuang.Services;

/// <summary>
/// Native HWND redraw and strict geometry verification can transiently disagree
/// during layout. A session remains visibly hosted when any verification in the
/// existing ShowOnly -> refresh -> refresh sequence succeeds.
/// </summary>
internal static class HostedWindowHealthPolicy
{
    internal static bool IsVerified(
        bool showVerified,
        bool firstRefreshVerified,
        bool secondRefreshVerified)
        => showVerified || firstRefreshVerified || secondRefreshVerified;

    internal static bool IsClipOnlyFailure(bool hostIdentityAndBoundsValid, int regionKind)
        => hostIdentityAndBoundsValid && regionKind == 0;

    internal static bool HasExpectedClip(
        int regionKind, NativeMethods.RECT actual, NativeMethods.RECT expected)
        => regionKind != 0
            && actual.Left == expected.Left && actual.Top == expected.Top
            && actual.Right == expected.Right && actual.Bottom == expected.Bottom;
}
