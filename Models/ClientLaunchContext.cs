namespace JuChuang.Models;

/// <summary>
/// 一次“添加客户端”操作的短生命周期上下文。
/// 通过启动前的 PID 快照区分真正的新实例，避免扫描时误选中已经存在的同类窗口。
/// </summary>
public sealed class ClientLaunchContext
{
    private readonly HashSet<int> _baselineProcessIds;

    public ClientLaunchContext(ClientKind kind, IEnumerable<int> baselineProcessIds)
    {
        Kind = kind;
        StartedUtc = DateTime.UtcNow;
        _baselineProcessIds = baselineProcessIds
            .Where(processId => processId > 0)
            .ToHashSet();
    }

    public ClientKind Kind { get; }

    public DateTime StartedUtc { get; }

    public int? ExpectedProcessId { get; private set; }

    public int? ClaimedProcessId { get; private set; }

    public IntPtr ClaimedHandle { get; private set; }

    public void ExpectProcess(int processId)
    {
        if (processId > 0)
        {
            ExpectedProcessId = processId;
        }
    }

    public bool CanClaim(ClientKind kind, int processId)
        => ClaimedProcessId is null
           && kind == Kind
           && processId > 0
           && (ExpectedProcessId is { } expected
               ? processId == expected
               : !_baselineProcessIds.Contains(processId));

    public void Claim(int processId, IntPtr handle)
    {
        ClaimedProcessId = processId;
        ClaimedHandle = handle;
    }
}
