using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private sealed record ClosedClientRecoveryRecord(
        ClientKind Kind, int Pid, long ProcessStartUtcTicks, IntPtr MainHwnd, DateTime ClosedUtc);

    private readonly List<ClosedClientRecoveryRecord> _closedClientRecovery = [];
    private static readonly TimeSpan ClosedClientRecoveryLifetime = TimeSpan.FromHours(1);
    private const int MaxClosedClientRecoveryRecords = 32;

    private void RememberClosedClient(ClientWindowEntry entry)
    {
        var start = TryGetProcessStartUtcTicks(entry.ProcessId);
        if (!start.HasValue || !WindowBelongsToProcess(entry.Handle, entry.ProcessId)) return;

        _closedClientRecovery.RemoveAll(record => record.Kind == entry.Kind && record.Pid == entry.ProcessId);
        _closedClientRecovery.Add(new ClosedClientRecoveryRecord(
            entry.Kind, entry.ProcessId, start.Value, entry.Handle, DateTime.UtcNow));
        PruneClosedClientRecovery();
    }

    private void PruneClosedClientRecovery()
    {
        var now = DateTime.UtcNow;
        _closedClientRecovery.RemoveAll(record =>
            now - record.ClosedUtc > ClosedClientRecoveryLifetime
            || TryGetProcessStartUtcTicks(record.Pid) != record.ProcessStartUtcTicks);
        while (_closedClientRecovery.Count > MaxClosedClientRecoveryRecords)
            _closedClientRecovery.RemoveAt(0);
    }

    // Called only while the scan semaphore is held by the explicit Attach All action.
    private void RecoverClosedClientWindows()
    {
        PruneClosedClientRecovery();
        foreach (var record in _closedClientRecovery.ToArray())
        {
            if (Entries.Any(entry => entry.Kind == record.Kind && entry.ProcessId == record.Pid))
            {
                _closedClientRecovery.Remove(record);
                continue;
            }

            // The discovery filter checks title, class, owner, dimensions and main-window
            // role before an invisible HWND is ever shown. This also rejects a reused HWND.
            var candidate = ClientWindowDiscovery.FindRecoverableMainWindows(record.Pid)
                .Where(window => window.Kind == record.Kind)
                .OrderByDescending(window => window.Handle == record.MainHwnd)
                .FirstOrDefault();
            if (candidate is null) continue;
            if (TryGetProcessStartUtcTicks(record.Pid) != record.ProcessStartUtcTicks
                || !WindowBelongsToProcess(candidate.Handle, record.Pid)) continue;

            if (!NativeMethods.IsWindowVisible(candidate.Handle))
                NativeMethods.ShowWindow(candidate.Handle, NativeMethods.SW_RESTORE);
        }
    }

    private void ConsumeRecoveredClientRecords()
    {
        PruneClosedClientRecovery();
        _closedClientRecovery.RemoveAll(record => Entries.Any(entry =>
            entry.Kind == record.Kind && entry.ProcessId == record.Pid && entry.IsAttached
            && TryGetProcessStartUtcTicks(entry.ProcessId) == record.ProcessStartUtcTicks));
    }
}
