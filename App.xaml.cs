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
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReleaseHostedWindows();
        LogException(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"聚窗遇到异常，客户端窗口已尝试恢复到桌面。\n\n{e.Exception.Message}",
            "聚窗",
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
            Dispatcher.Invoke(ReleaseHostedWindows);
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
