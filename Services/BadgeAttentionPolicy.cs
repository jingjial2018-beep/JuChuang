namespace JuChuang.Services;

/// <summary>
/// Activity is only a request to scan. A notification requires a known
/// baseline and a newly observed unread badge.
/// </summary>
internal static class BadgeAttentionPolicy
{
    internal static bool IsNewUnread(BadgeResult? previousStable, BadgeResult current)
    {
        if (previousStable is null || current.Confidence == BadgeConfidenceLevel.NoBadge)
        {
            return false;
        }

        var previous = previousStable.Value;
        if (previous.Confidence == BadgeConfidenceLevel.NoBadge)
        {
            return true;
        }

        // A dot becoming a count can be improved recognition, not a new message.
        return previous.Confidence == BadgeConfidenceLevel.High
            && current.Confidence == BadgeConfidenceLevel.High
            && current.Number > previous.Number;
    }
}

