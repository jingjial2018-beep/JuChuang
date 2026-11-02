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
}
