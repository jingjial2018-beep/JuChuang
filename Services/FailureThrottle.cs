namespace JuChuang.Services;

/// <summary>
/// 同一来源的反复失败在时间窗口内只上报一次，避免每 5 秒一次的扫描失败刷屏日志和状态栏。
/// 非线程安全：仅在 UI 线程使用。
/// </summary>
internal sealed class FailureThrottle
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, DateTime> _lastReportedUtc = new();

    public FailureThrottle(TimeSpan interval)
    {
        _interval = interval;
    }

    public bool ShouldReport(string source, DateTime nowUtc)
    {
        if (_lastReportedUtc.TryGetValue(source, out var last) && nowUtc - last < _interval)
        {
            return false;
        }

        _lastReportedUtc[source] = nowUtc;
        return true;
    }
}
