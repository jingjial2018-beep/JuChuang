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
        foreach (var id in accountIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = WeChatProfileService.FindContactDatabasePath(id);
            if (path is null) { inconclusive = true; missing++; continue; }
            var status = QueryOwner(path, pid, startTicks.Value);
            if (status == 1) owners.Add(id);
            else if (status == -1) otherOwned++;
            else { inconclusive = true; probeErrors++; }
        }
        if (owners.Count == 1 && !inconclusive)
            return new(Verdict.Unique, owners[0], "UniqueOwner");
        if (owners.Count > 1)
            return new(Verdict.Conflict, null, "MultipleTargetOwners");
        return new(Verdict.Unknown, null,  $"Owners={owners.Count};OtherOwned={otherOwned};Missing={missing};Uncertain={probeErrors}");
    }

    // 1 target PID owns, -1 demonstrably another process owns, 0 inconclusive.
    private static int QueryOwner(string path, int pid, long startTicks)
    {
        var key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var handle, 0, key) != 0) return 0;
        try
        {
            if (RmRegisterResources(handle, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return 0;
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (rc != 234 && rc != 0) return 0;
            if (needed == 0) return 0;
            var entries = new RM_PROCESS_INFO[needed];
            count = needed;
            rc = RmGetList(handle, out needed, ref count, entries, ref reasons);
            if (rc != 0) return 0;
            var target = false;
            var other = false;
            for (int i = 0; i < count; i++)
            {
                var process = entries[i].Process;
                var ticks = DateTime.FromFileTimeUtc(process.StartTime).Ticks;
                if (process.ProcessId == pid && ticks == startTicks) target = true;
                else other = true;
            }
            return target && !other ? 1 : other && !target ? -1 : 0;
        }
        catch { return 0; }
        finally { RmEndSession(handle); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RM_UNIQUE_PROCESS
    {
        public int ProcessId;
        public long StartTime;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
