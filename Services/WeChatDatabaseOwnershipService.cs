using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JuChuang.Services;

// Read-only Windows Restart Manager file-usage evidence. Never reads chat content.
internal static class WeChatDatabaseOwnershipService
{
    internal enum Verdict { Unknown, Unique, Conflict }
    internal sealed record Result(Verdict Status, string? AccountId, string Detail);

    internal static Result Resolve(int pid, long? startTicks, IReadOnlyList<string> accountIds)
    {
        if (pid <= 0 || !startTicks.HasValue || accountIds.Count == 0)
            return new(Verdict.Unknown, null, "missing process identity or account candidates");
        var owners = new List<string>();
        var inconclusive = false;
        var otherOwned = 0;
        var missing = 0;
        var probeErrors = 0;
        var samePid = 0;
        var startMismatch = 0;
        var otherPid = 0;
        long maxDeltaMs = 0;
        foreach (var id in accountIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = WeChatProfileService.FindContactDatabasePath(id);
            if (path is null) { inconclusive = true; missing++; continue; }
            var result = QueryOwner(path, pid, startTicks.Value);
            var status = result.Status;
            samePid += result.SamePid;
            startMismatch += result.StartMismatch;
            otherPid += result.OtherPid;
            maxDeltaMs = Math.Max(maxDeltaMs, result.DeltaMs);
            if (status == 1) owners.Add(id);
            else if (status == -1) otherOwned++;
            else { inconclusive = true; probeErrors++; }
        }
        if (owners.Count == 1 && !inconclusive)
            return new(Verdict.Unique, owners[0], $"UniqueOwner;SamePid={samePid};TimeMismatch={startMismatch};OtherPid={otherPid};MaxDeltaMs={maxDeltaMs}");
        if (owners.Count > 1)
            return new(Verdict.Conflict, null, $"MultipleTargetOwners;SamePid={samePid};TimeMismatch={startMismatch};OtherPid={otherPid}");
        return new(Verdict.Unknown, null, $"Owners={owners.Count};OtherOwned={otherOwned};Missing={missing};Uncertain={probeErrors};SamePid={samePid};TimeMismatch={startMismatch};OtherPid={otherPid};MaxDeltaMs={maxDeltaMs}");
    }

    private sealed record OwnerProbe(int Status, int SamePid, int StartMismatch, int OtherPid, long DeltaMs);

    // 1: same PID and same start time exclusively owns the file.
    // -1: demonstrably other process owns it; 0: any ambiguity or API error.
    private static OwnerProbe QueryOwner(string path, int pid, long startTicks)
    {
        var unknown = new OwnerProbe(0, 0, 0, 0, 0);
        var key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var handle, 0, key) != 0) return unknown;
        try
        {
            if (RmRegisterResources(handle, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return unknown;
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (rc != 234 && rc != 0) return unknown;
            if (needed == 0) return unknown;
            var entries = new RM_PROCESS_INFO[needed];
            count = needed;
            rc = RmGetList(handle, out needed, ref count, entries, ref reasons);
            if (rc != 0 || count > entries.Length) return unknown;
            var target = false;
            var samePid = 0;
            var mismatch = 0;
            var otherPid = 0;
            long maxDeltaMs = 0;
            for (int i = 0; i < count; i++)
            {
                var process = entries[i].Process;
                if (process.ProcessId != pid) { otherPid++; continue; }
                samePid++;
                var ticks = DateTime.FromFileTimeUtc(process.StartTime.ToInt64()).Ticks;
                var delta = Math.Abs((ticks - startTicks) / TimeSpan.TicksPerMillisecond);
                maxDeltaMs = Math.Max(maxDeltaMs, delta);
                if (ticks == startTicks) target = true;
                else mismatch++;
            }
            var status = target && otherPid == 0 && mismatch == 0 ? 1
                : samePid == 0 && otherPid > 0 ? -1 : 0;
            return new(status, samePid, mismatch, otherPid, maxDeltaMs);
        }
        catch { return unknown; }
        finally { RmEndSession(handle); }
    }

    // Native rstrtmgr.h uses DWORD + FILETIME, which is a 12-byte
    // RM_UNIQUE_PROCESS (not DWORD + 8-byte-aligned Int64).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RM_FILETIME
    {
        public uint Low;
        public uint High;
        public long ToInt64() => (long)(((ulong)High << 32) | Low);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RM_UNIQUE_PROCESS
    {
        public int ProcessId;
        public RM_FILETIME StartTime;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, int flags, string sessionKey);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, IntPtr services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? processes, ref uint rebootReasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
}
