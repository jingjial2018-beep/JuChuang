namespace JuChuang.Services;

/// <summary>
/// 运行一个异步操作，把异常交给回调而不是向外抛出。后台扫描由定时器和事件回调触发，
/// 这些入口一旦有异常逃逸就会走到全局异常处理并让整个程序退出。
/// </summary>
internal static class GuardedRunner
{
    /// <returns>操作是否成功完成。</returns>
    public static async Task<bool> RunAsync(Func<Task> action, Action<Exception> onFailure)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                onFailure(exception);
            }
            catch
            {
                // 失败回调本身也不能让异常逃逸。
            }

            return false;
        }
    }
}
