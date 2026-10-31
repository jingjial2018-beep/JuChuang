using System.ComponentModel;
using System.Diagnostics;

namespace JuChuang.Services;

public interface IExitProcessTarget
{
    int ProcessId { get; }
    bool RequestGracefulExit();
    Task<bool> WaitForExitAsync(TimeSpan timeout);
    void ForceKill();
}

public sealed record ExitProcessResult(int ProcessId, bool Requested, bool Confirmed, string? Error)
{
    public bool GracefulRequested { get; init; }
    public bool KillRequested { get; init; }
}

public sealed class ExitProcessIdentityException : InvalidOperationException
{
    public ExitProcessIdentityException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

// A batch may time out immediately before a terminated client is observed.
// The original Process wrapper, rather than a new PID lookup, is used as
// evidence: it still identifies the process generation validated before exit.
public static class ExitCompletionPolicy
{
    public static ExitProcessResult Reconcile(ExitProcessResult result, bool originalProcessExited)
        => originalProcessExited && !result.Confirmed
            ? result with { Confirmed = true, Error = null }
            : result;
}

public static class ExitBatchService
{
    // Every target begins its graceful request concurrently. Each client gets a
    // bounded grace period; only clients still alive after that are force-killed.
    public static async Task<IReadOnlyList<ExitProcessResult>> RunAsync(
        IReadOnlyList<IExitProcessTarget> targets,
        TimeSpan gracefulTimeout,
        TimeSpan operationTimeout)
    {
        var uniqueTargets = targets.DistinctBy(target => target.ProcessId).ToArray();
        var exits = uniqueTargets
            .Select(target => ExitOneAsync(target, gracefulTimeout, operationTimeout))
            .ToArray();
        return await Task.WhenAll(exits);
    }

    private static async Task<ExitProcessResult> ExitOneAsync(
        IExitProcessTarget target,
        TimeSpan gracefulTimeout,
        TimeSpan operationTimeout)
    {
        var gracefulTask = Task.Run(target.RequestGracefulExit);
        if (await Task.WhenAny(gracefulTask, Task.Delay(operationTimeout)) != gracefulTask)
        {
            ObserveLateFailure(gracefulTask, "client-exit-graceful-late-worker");
            DiagnosticLogService.WriteActivityEvent($"client-exit-graceful-request-timeout pid={target.ProcessId}");
            return new ExitProcessResult(target.ProcessId, false, false, "GracefulRequestTimeout");
        }

        bool gracefulRequested;
        try
        {
            gracefulRequested = await gracefulTask;
        }
        catch (ExitProcessIdentityException exception)
        {
            return new ExitProcessResult(target.ProcessId, false, false,
                "IdentityMismatch: " + exception.Message);
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent("client-exit-graceful-request", exception);
            return new ExitProcessResult(target.ProcessId, false, false,
                "GracefulRequestFailed: " + exception.GetType().Name);
        }

        try
        {
            if (await target.WaitForExitAsync(gracefulTimeout))
            {
                return new ExitProcessResult(target.ProcessId, gracefulRequested, true, null)
                {
                    GracefulRequested = gracefulRequested
                };
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent("client-exit-graceful-confirmation", exception);
            // A failed observation is not evidence of exit. The kill target performs
            // a fresh PID/start-time/HWND check before any forced termination.
        }

        var killTask = Task.Run(target.ForceKill);
        if (await Task.WhenAny(killTask, Task.Delay(operationTimeout)) != killTask)
        {
            ObserveLateFailure(killTask, "client-exit-kill-late-worker");
            DiagnosticLogService.WriteActivityEvent($"client-exit-kill-request-timeout pid={target.ProcessId}");
            return new ExitProcessResult(target.ProcessId, true, false, "KillRequestTimeout")
            {
                GracefulRequested = gracefulRequested
            };
        }

        try
        {
            await killTask;
        }
        catch (ExitProcessIdentityException exception)
        {
            var exited = await TryObserveExited(target, TimeSpan.FromMilliseconds(100));
            return new ExitProcessResult(target.ProcessId, true, exited,
                exited ? null : "IdentityMismatch: " + exception.Message)
            {
                GracefulRequested = gracefulRequested
            };
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent("client-exit-kill-request", exception);
            var exited = await TryObserveExited(target, TimeSpan.FromMilliseconds(100));
            return new ExitProcessResult(target.ProcessId, true, exited,
                exited ? null : "KillFailed: " + exception.GetType().Name)
            {
                GracefulRequested = gracefulRequested
            };
        }

        var confirmed = await TryObserveExited(target, operationTimeout);
        return new ExitProcessResult(target.ProcessId, true, confirmed,
            confirmed ? null : "KillTimeout")
        {
            GracefulRequested = gracefulRequested,
            KillRequested = true
        };
    }

    private static async Task<bool> TryObserveExited(IExitProcessTarget target, TimeSpan timeout)
    {
        try
        {
            return await target.WaitForExitAsync(timeout);
        }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent("client-exit-process-observation", exception);
            return false;
        }
    }

    private static void ObserveLateFailure(Task task, string eventName)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Exception is { } exception)
                    DiagnosticLogService.WriteErrorEvent(eventName, exception.GetBaseException());
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class SystemExitProcessTarget : IExitProcessTarget
{
    private readonly Process process;
    private readonly int processId;
    private readonly long processStartUtcTicks;
    private readonly IntPtr windowHandle;

    public SystemExitProcessTarget(Process process, IntPtr windowHandle)
    {
        this.process = process;
        this.windowHandle = windowHandle;
        processId = process.Id;
        processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
    }

    public int ProcessId => processId;

    public bool RequestGracefulExit()
    {
        using var current = Process.GetProcessById(processId);
        VerifyProcessIdentity(current);

        // The HWND can disappear if the client is already closing. That is not
        // permission to target a replacement window; ForceKill rechecks ownership
        // if this original handle is still valid.
        if (!NativeMethods.IsWindow(windowHandle)) return false;
        VerifyWindowOwner();
        return NativeMethods.PostMessage(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void ForceKill()
    {
        using var current = Process.GetProcessById(processId);
        VerifyProcessIdentity(current);

        // WM_CLOSE may destroy the original HWND before the client process exits.
        // If the handle still exists, it must still belong to the validated PID.
        if (NativeMethods.IsWindow(windowHandle))
            VerifyWindowOwner();

        current.Kill(entireProcessTree: false);
    }

    private void VerifyProcessIdentity(Process current)
    {
        if (current.HasExited)
            throw new ExitProcessIdentityException("The validated client process has already exited.");

        long currentStart;
        try
        {
            currentStart = current.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new ExitProcessIdentityException("The client process start time cannot be verified.", exception);
        }

        if (currentStart != processStartUtcTicks)
            throw new ExitProcessIdentityException("The client PID now belongs to a different process generation.");
    }

    private void VerifyWindowOwner()
    {
        NativeMethods.GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
        if (ownerProcessId != (uint)processId)
            throw new ExitProcessIdentityException("The original client HWND no longer belongs to the validated PID.");
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }
}

public static class ExitClosePolicy
{
    // A successful all-client exit re-enters Window_Closing through Close().
    // It must proceed to cleanup without showing another choice or starting exit again.
    public static bool ShouldPromptForChoice(bool allCompleted, bool choiceApproved, bool initialized)
        => !allCompleted && !choiceApproved && initialized;

    public static bool ShouldBeginExitAll(bool allCompleted, bool exitAllSelected)
        => !allCompleted && exitAllSelected;
}
