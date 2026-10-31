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
    {
        if (result.Confirmed)
            return result.KillRequested ? "已强制退出"
                : result.GracefulRequested ? "已正常退出"
                : "已退出";

        if (result.Error?.StartsWith("IdentityMismatch:", StringComparison.Ordinal) == true)
            return "进程身份校验失败，未执行强制退出";
        if (result.Error == "GracefulRequestTimeout")
            return "正常退出请求超时";
        if (result.Error?.StartsWith("GracefulRequestFailed:", StringComparison.Ordinal) == true)
            return "正常退出请求失败";
        if (result.Error == "KillRequestTimeout")
            return "强制退出请求超时";
        if (result.Error == "KillTimeout")
            return "强制退出未确认";
        if (result.Error?.StartsWith("KillConfirmationFailed:", StringComparison.Ordinal) == true)
            return "强制退出确认失败";
        if (result.Error?.StartsWith("KillFailed:", StringComparison.Ordinal) == true)
            return "强制退出失败";

        return "确认失败";
    }
}
