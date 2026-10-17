using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace JuChuang;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // 清理上次崩溃或旧版本遗留的解密临时文件。只删超过 2 分钟的，避免误删
        // 另一个 JNest 实例正在使用的文件。
        _ = Task.Run(() =>
        {
            try
            {
                JuChuang.Services.WeChatProfileService.CleanupTemporaryArtifacts(TimeSpan.FromMinutes(2));
            }
            catch
            {
                // 清理只是尽力而为。
            }
        });
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReleaseHostedWindows();
        LogException(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"JNest遇到异常，客户端窗口已尝试恢复到桌面。\n\n{e.Exception.Message}",
            "JNest",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogException(exception);
        }

        try
        {
            // 崩溃发生在后台线程时 UI 线程可能已被阻塞；带超时，避免进程卡死而无法退出。
            Dispatcher.Invoke(
                ReleaseHostedWindows,
                DispatcherPriority.Send,
                CancellationToken.None,
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The dispatcher may already be shutting down.
        }
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JuChuang");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch
        {
            // Logging failures must never interrupt the error-handling path.
        }
    }

    private void ReleaseHostedWindows()
    {
        if (MainWindow is MainWindow manager)
        {
            manager.ReleaseHostedWindowsForShutdown();
        }
    }
}
