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
    // Phase 1: start every graceful request before observing any process.
    // Phase 2: wait for all graceful exits concurrently.
    // Phase 3: start every eligible Kill fallback before confirming any of them.
    public static async Task<IReadOnlyList<ExitProcessResult>> RunAsync(
        IReadOnlyList<IExitProcessTarget> targets,
        TimeSpan gracefulTimeout,
        TimeSpan operationTimeout)
    {
        var uniqueTargets = targets.DistinctBy(target => target.ProcessId).ToArray();
        if (uniqueTargets.Length == 0) return Array.Empty<ExitProcessResult>();

        var requestTasks = uniqueTargets
            .Select(target => Task.Run(target.RequestGracefulExit))
            .ToArray();
        var requestOutcomes = await Task.WhenAll(requestTasks.Select(
            (task, index) => ObserveGracefulRequestAsync(task, uniqueTargets[index].ProcessId, operationTimeout)));

        var results = new ExitProcessResult?[uniqueTargets.Length];
        var gracefulRequested = new bool[uniqueTargets.Length];
        var waitIndices = new List<int>();

        for (var index = 0; index < uniqueTargets.Length; index++)
        {
            var outcome = requestOutcomes[index];
            if (!outcome.Completed)
            {
                results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId, false, false,
                    "GracefulRequestTimeout");
                continue;
            }

            if (outcome.Error is { } requestError)
            {
                var error = requestError is ExitProcessIdentityException
                    ? "IdentityMismatch: " + requestError.Message
                    : "GracefulRequestFailed: " + requestError.GetType().Name;
                results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId, false, false, error);
                if (requestError is not ExitProcessIdentityException)
                    DiagnosticLogService.WriteErrorEvent("client-exit-graceful-request", requestError);
                continue;
            }

            gracefulRequested[index] = outcome.Requested;
            waitIndices.Add(index);
        }

        var gracefulWaits = await Task.WhenAll(waitIndices.Select(index =>
            ObserveExitAsync(uniqueTargets[index], gracefulTimeout, "client-exit-graceful-confirmation")));
        var killIndices = new List<int>();

        for (var offset = 0; offset < waitIndices.Count; offset++)
        {
            var index = waitIndices[offset];
            var wait = gracefulWaits[offset];
            if (wait.Exited)
            {
                results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId,
                    gracefulRequested[index], true, null)
                {
                    GracefulRequested = gracefulRequested[index]
                };
            }
            else
            {
                killIndices.Add(index);
            }
        }

        var killTasks = killIndices
            .Select(index => Task.Run(uniqueTargets[index].ForceKill))
            .ToArray();
        var killOutcomes = await Task.WhenAll(killTasks.Select(
            (task, offset) => ObserveKillRequestAsync(task, uniqueTargets[killIndices[offset]].ProcessId, operationTimeout)));

        var killWaitIndices = new List<int>();
        for (var offset = 0; offset < killIndices.Count; offset++)
        {
            var index = killIndices[offset];
            var outcome = killOutcomes[offset];
            if (!outcome.Completed)
            {
                results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId, true, false,
                    "KillRequestTimeout")
                {
                    GracefulRequested = gracefulRequested[index]
                };
                continue;
            }

            if (outcome.Error is { } killError)
            {
                var exited = await TryObserveExited(uniqueTargets[index], TimeSpan.FromMilliseconds(100));
                var error = exited ? null
                    : killError is ExitProcessIdentityException
                        ? "IdentityMismatch: " + killError.Message
                        : "KillFailed: " + killError.GetType().Name;
                results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId, true, exited, error)
                {
                    GracefulRequested = gracefulRequested[index]
                };
                if (!exited) DiagnosticLogService.WriteErrorEvent("client-exit-kill-request", killError);
                continue;
            }

            killWaitIndices.Add(index);
        }

        var killWaits = await Task.WhenAll(killWaitIndices.Select(index =>
            ObserveExitAsync(uniqueTargets[index], operationTimeout, "client-exit-kill-confirmation")));
        for (var offset = 0; offset < killWaitIndices.Count; offset++)
        {
            var index = killWaitIndices[offset];
            var wait = killWaits[offset];
            results[index] = new ExitProcessResult(uniqueTargets[index].ProcessId, true, wait.Exited,
                wait.Exited ? null : wait.Error is null
                    ? "KillTimeout"
                    : "KillConfirmationFailed: " + wait.Error.GetType().Name)
            {
                GracefulRequested = gracefulRequested[index],
                KillRequested = true
            };
        }

        return results.Select(result => result!).ToArray();
    }

    private static async Task<(bool Completed, bool Requested, Exception? Error)> ObserveGracefulRequestAsync(
        Task<bool> task, int processId, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            ObserveLateFailure(task, "client-exit-graceful-late-worker");
            DiagnosticLogService.WriteActivityEvent($"client-exit-graceful-request-timeout pid={processId}");
            return (false, false, null);
        }

        try { return (true, await task, null); }
        catch (Exception exception) { return (true, false, exception); }
    }

    private static async Task<(bool Completed, Exception? Error)> ObserveKillRequestAsync(
        Task task, int processId, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            ObserveLateFailure(task, "client-exit-kill-late-worker");
            DiagnosticLogService.WriteActivityEvent($"client-exit-kill-request-timeout pid={processId}");
            return (false, null);
        }

        try { await task; return (true, null); }
        catch (Exception exception) { return (true, exception); }
    }

    private static async Task<(bool Exited, Exception? Error)> ObserveExitAsync(
        IExitProcessTarget target, TimeSpan timeout, string eventName)
    {
        try { return (await target.WaitForExitAsync(timeout), null); }
        catch (Exception exception)
        {
            DiagnosticLogService.WriteErrorEvent(eventName, exception);
            return (false, exception);
        }
    }

    private static async Task<bool> TryObserveExited(IExitProcessTarget target, TimeSpan timeout)
    {
        try { return await target.WaitForExitAsync(timeout); }
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
        if (!NativeMethods.IsWindow(windowHandle)) return false;
        VerifyWindowOwner();
        return NativeMethods.PostMessage(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void ForceKill()
    {
        using var current = Process.GetProcessById(processId);
        VerifyProcessIdentity(current);
        // WM_CLOSE may destroy the original HWND before process exit. If it remains
        // valid, it must still belong to the validated PID before Kill is allowed.
        if (NativeMethods.IsWindow(windowHandle))
            VerifyWindowOwner();
        current.Kill(entireProcessTree: false);
    }

    private void VerifyProcessIdentity(Process current)
    {
        if (current.HasExited)
            throw new ExitProcessIdentityException("The validated client process has already exited.");

        long currentStart;
        try { currentStart = current.StartTime.ToUniversalTime().Ticks; }
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
    public static bool ShouldPromptForChoice(bool allCompleted, bool choiceApproved, bool initialized)
        => !allCompleted && !choiceApproved && initialized;

    public static bool ShouldBeginExitAll(bool allCompleted, bool exitAllSelected)
        => !allCompleted && exitAllSelected;
}
