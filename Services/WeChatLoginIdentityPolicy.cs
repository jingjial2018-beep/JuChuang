namespace JuChuang.Services;

/// <summary>
/// Prevents a reused WeChat process from resolving its previous account from
/// a database key that may remain in memory after logout.
/// </summary>
public static class WeChatLoginIdentityPolicy
{
    private static readonly TimeSpan SameAccountRetryDelay = TimeSpan.FromSeconds(45);

    public static IReadOnlyList<WeChatAccountIdentity> NewAccountCandidates(
        IReadOnlyList<WeChatAccountIdentity> candidates,
        string? previousIdentityKey,
        DateTime? loginTransitionUtc)
        => string.IsNullOrWhiteSpace(previousIdentityKey)
            ? candidates
            : candidates.Where(account => !account.Key.Equals(
                previousIdentityKey, StringComparison.OrdinalIgnoreCase)
                && (!loginTransitionUtc.HasValue
                    || account.LastLoginUtc >= loginTransitionUtc.Value)).ToArray();

    public static WeChatAccountIdentity? ConfirmedSameAccountCandidate(
        IReadOnlyList<WeChatAccountIdentity> candidates,
        string? previousIdentityKey,
        DateTime? loginTransitionUtc,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(previousIdentityKey)
            || !loginTransitionUtc.HasValue
            || nowUtc - loginTransitionUtc.Value < SameAccountRetryDelay)
        {
            return null;
        }

        var previous = candidates.FirstOrDefault(account => account.Key.Equals(
            previousIdentityKey, StringComparison.OrdinalIgnoreCase));
        if (previous is null || previous.LastLoginUtc < loginTransitionUtc.Value)
            return null;

        // A newly logged-in different account takes precedence even if its
        // contact database is still being written. Never show the old profile
        // merely because the old key remains readable in this process.
        return candidates.Any(account => !account.Key.Equals(
            previousIdentityKey, StringComparison.OrdinalIgnoreCase)
            && account.LastLoginUtc >= loginTransitionUtc.Value)
            ? null
            : previous;
    }
}
