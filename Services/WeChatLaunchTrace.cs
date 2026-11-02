using System.Diagnostics;
using System.Threading;

namespace JuChuang.Services;

// One short, privacy-safe timeline per Add WeChat click. The process ID links
// CreateProcessW to the existing wechat-discovered event without logging paths.
internal sealed class WeChatLaunchTrace
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly string _attempt = Guid.NewGuid().ToString("N")[..8];
    private readonly object _sync = new();
    private readonly List<(string EventName, int ProcessId)> _pending = [];
    private int _processId;
    private int _windowEnumerated;

    internal void Record(string stage, int processId = 0)
    {
        var eventName = $"wechat-launch attempt={_attempt} stage={stage} elapsedMs={_watch.ElapsedMilliseconds}";
        lock (_sync)
        {
            _pending.Add((eventName, processId));
        }
    }

    // File I/O is deferred until CreateProcessW has returned. In particular,
    // the trace must never lengthen the Mutex-ready -> process-created interval.
    internal void Flush()
    {
        (string EventName, int ProcessId)[] batch;
        lock (_sync)
        {
            batch = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var (eventName, processId) in batch)
        {
            DiagnosticLogService.WriteActivityEvent(eventName, processId);
        }
    }

    internal void RecordProcessCreated(int processId)
    {
        Volatile.Write(ref _processId, processId);
        Record("process-created", processId);
    }

    internal void RecordWindowEnumerated(int processId)
    {
        if (processId <= 0 || processId != Volatile.Read(ref _processId)
            || Interlocked.Exchange(ref _windowEnumerated, 1) != 0)
        {
            return;
        }

        Record("window-enumerated", processId);
    }
}
