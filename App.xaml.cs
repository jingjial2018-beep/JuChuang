using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using JuChuang.Services;
using System.Windows;
using System.Windows.Threading;

namespace JuChuang;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\JNest-Desktop-SingleInstance";
    private const string ActivationPipeName = "JNest-Desktop-Activation";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private readonly CancellationTokenSource _activationCancellation = new();
    private readonly Queue<string> _pendingActivations = new();

    private static string? GetActivationArgument(IEnumerable<string> args)
    {
        var argument = args.FirstOrDefault(a => a.StartsWith("jnest-activate=", StringComparison.OrdinalIgnoreCase));
        return argument is null ? null : Uri.UnescapeDataString(argument["jnest-activate=".Length..]);
    }

    private static bool ForwardActivation(string? argument)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
                pipe.Connect(250);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine(argument ?? "activate");
                return true;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (TimeoutException) { Thread.Sleep(100); }
        }
        return false;
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("JNest"))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                    {
                        NativeMethods.EnumWindows((candidate, _) =>
                        {
                            NativeMethods.GetWindowThreadProcessId(candidate, out var owner);
                            if (owner != (uint)process.Id
                                || NativeMethods.GetWindow(candidate, NativeMethods.GW_OWNER) != IntPtr.Zero)
                                return true;
                            handle = candidate;
                            return false;
                        }, IntPtr.Zero);
                    }
                    if (handle == IntPtr.Zero) continue;
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(handle);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteActivityEvent("activation-fallback-" + exception.GetType().Name);
        }
    }

    private async Task ListenForActivationAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(ActivationPipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                var argument = await reader.ReadLineAsync(token);
                if (argument is not null)
                    await Dispatcher.InvokeAsync(() => DispatchActivation(argument));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception exception)
            {
                JuChuang.Services.DiagnosticLogService.WriteActivityEvent("activation-pipe-" + exception.GetType().Name);
                try { await Task.Delay(250, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void DispatchActivation(string argument)
    {
        if (MainWindow is MainWindow window && window.IsLoaded)
            window.ActivateFromToast(argument);
        else
            _pendingActivations.Enqueue(argument);
    }

    private void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        while (_pendingActivations.Count > 0)
        {
            var argument = _pendingActivations.Dequeue();
            DispatchActivation(argument);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!ForwardActivation(GetActivationArgument(e.Args)))
                BringExistingInstanceToFront();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // The old disk credential and marker have no meaning in a new session.
        // Remove only these two known files; a failed cleanup cannot relock startup.
        try { LockCredentialService.DeleteLegacyCredential(); }
        catch (Exception exception) { DiagnosticLogService.WriteActivityEvent("legacy-lock-credential-cleanup-" + exception.GetType().Name); }
        try { JNestLockPersistence.DeleteLegacyMarker(); }
        catch (Exception exception) { DiagnosticLogService.WriteActivityEvent("legacy-lock-marker-cleanup-" + exception.GetType().Name); }
        if (GetActivationArgument(e.Args) is { } initialArgument)
            _pendingActivations.Enqueue(initialArgument);
        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
                Dispatcher.BeginInvoke(new Action(() => DispatchActivation(args.Argument)));
        }
        catch (Exception exception)
        {
            JuChuang.Services.DiagnosticLogService.WriteActivityEvent("toast-activation-register-" + exception.GetType().Name);
        }
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindowLoaded));
        _ = ListenForActivationAsync(_activationCancellation.Token);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation.Cancel();
        if (_ownsMutex) { try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { } }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Record the original startup failure before attempting best-effort cleanup.
        LogException(e.Exception);
        e.Handled = true;
        try
        {
            ReleaseHostedWindows();
        }
        catch (Exception cleanupException)
        {
            LogException(cleanupException);
        }
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

