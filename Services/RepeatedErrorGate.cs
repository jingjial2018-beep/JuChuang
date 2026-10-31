namespace JuChuang.Services;

/// <summary>
/// A bounded, thread-safe per-fingerprint throttle for repeated diagnostics.
/// Different sources, HRESULTs, or messages still log independently.
/// The first error is always recorded; this is not an exception suppressor.
/// </summary>
internal sealed class RepeatedErrorGate(TimeSpan window, int maximumEntries)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _lastWrittenUtc = new(StringComparer.Ordinal);

    internal bool ShouldLog(string fingerprint, DateTime utcNow)
    {
        lock (_sync)
        {
            if (_lastWrittenUtc.TryGetValue(fingerprint, out var previous)
                && utcNow >= previous && utcNow - previous < window)
                return false;

            _lastWrittenUtc[fingerprint] = utcNow;
            if (_lastWrittenUtc.Count > maximumEntries)
            {
                // Keep the most recent fingerprints. This cap is important
                // when exceptions include distinct process IDs or paths.
                foreach (var key in _lastWrittenUtc.OrderBy(item => item.Value)
                             .Take(_lastWrittenUtc.Count - maximumEntries)
                             .Select(item => item.Key).ToArray())
                    _lastWrittenUtc.Remove(key);
            }
            return true;
        }
    }
}
