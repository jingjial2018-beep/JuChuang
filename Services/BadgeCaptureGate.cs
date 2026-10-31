using JuChuang.Models;

namespace JuChuang.Services;

internal enum BadgeCaptureStartResult
{
    Started,
    AlreadyRunning,
    CoolingDown,
}

internal enum BadgeCaptureStatus
{
    Completed,
    Unavailable,
    WindowHung,
    TimedOut,
    Skipped,
}

internal readonly record struct BadgeCaptureAttempt(
    BadgeCaptureStatus Status,
    BadgeResult? Result);

/// <summary>
/// Prevents repeated capture attempts for a client window while a native capture
/// is still running or during the cooldown after a timeout/unresponsive window.
/// The gate is intentionally independent of WPF so its state transitions are testable.
/// </summary>
internal sealed class BadgeCaptureGate
{
    private readonly object _sync = new();
    private readonly HashSet<(IntPtr Handle, int ProcessId)> _inFlight = [];
    private readonly Dictionary<(IntPtr Handle, int ProcessId), DateTime> _retryAfterUtc = [];

    internal BadgeCaptureStartResult TryBegin(IntPtr handle, int processId, DateTime nowUtc)
    {
        var key = (handle, processId);
        lock (_sync)
        {
            foreach (var expired in _retryAfterUtc
                         .Where(item => item.Value <= nowUtc)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _retryAfterUtc.Remove(expired);
            }

            if (_inFlight.Contains(key))
                return BadgeCaptureStartResult.AlreadyRunning;

            if (_retryAfterUtc.TryGetValue(key, out var retryAfter) && retryAfter > nowUtc)
                return BadgeCaptureStartResult.CoolingDown;

            _retryAfterUtc.Remove(key);
            _inFlight.Add(key);
            return BadgeCaptureStartResult.Started;
        }
    }

    internal void Defer(IntPtr handle, int processId, DateTime retryAfterUtc)
    {
        var key = (handle, processId);
        lock (_sync)
        {
            if (!_retryAfterUtc.TryGetValue(key, out var current)
                || retryAfterUtc > current)
            {
                _retryAfterUtc[key] = retryAfterUtc;
            }
        }
    }

    internal void Complete(IntPtr handle, int processId)
    {
        lock (_sync)
        {
            _inFlight.Remove((handle, processId));
        }
    }
}
