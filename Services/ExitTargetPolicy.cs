namespace JuChuang.Services;

public static class ExitTargetPolicy
{
    // A manually popped-out client still has a JNest account card and remains managed.
    // A failed or transient attachment is not an explicit user-managed target.
    public static bool IsManaged(bool isAttached, bool isManuallyDetached)
        => isAttached || isManuallyDetached;
}

public static class ExitResultPresentation
{
    public static string Status(ExitProcessResult result)
        => result.Confirmed ? "已退出"
            : !result.Requested ? "请求失败"
            : result.Error == "Timeout" ? "未确认退出"
            : "确认失败";
}
