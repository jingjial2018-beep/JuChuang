using System.Diagnostics;

namespace JuChuang.Services;

public interface IExitProcessTarget
{
    int ProcessId { get; }
    void RequestExit();
    Task<bool> WaitForExitAsync(TimeSpan timeout);
}

public sealed record ExitProcessResult(int ProcessId, bool Requested, bool Confirmed, string? Error);

public static class ExitBatchService
{
    public static async Task<IReadOnlyList<ExitProcessResult>> RunAsync(
        IReadOnlyList<IExitProcessTarget> targets, TimeSpan timeout)
    {
        // Issue every request before waiting so one slow process cannot delay the others.
        // Kill can block in the OS. Start all requests on worker threads so a
        // WPF Closing handler can yield before any termination request finishes.
        var uniqueTargets = targets.DistinctBy(target => target.ProcessId).ToArray();
        var requests = uniqueTargets.Select(target => Task.Run(() => RequestExit(target))).ToArray();
        // A blocking native termination request must not hold the whole batch indefinitely.
        // Preserve a separate Unknown/RequestTimeout result: its worker might finish later.
        var requestResults = await Task.WhenAll(requests.Select(async (request, index) =>
        {
            var completed = await Task.WhenAny(request, Task.Delay(timeout));
            return completed == request
                ? await request
                : new ExitProcessResult(uniqueTargets[index].ProcessId, false, false, "RequestTimeout");
        }));
        var confirmations = uniqueTargets.Select((target, index) =>
            requestResults[index] is { } failure
                ? Task.FromResult(failure)
                : ConfirmAsync(target, timeout));
        return await Task.WhenAll(confirmations);
    }

    private static ExitProcessResult? RequestExit(IExitProcessTarget target)
    {
        try
        {
            target.RequestExit();
            return null;
        }
        catch (Exception exception)
        {
            return new ExitProcessResult(target.ProcessId, false, false,
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static async Task<ExitProcessResult> ConfirmAsync(IExitProcessTarget target, TimeSpan timeout)
    {
        try
        {
            var confirmed = await target.WaitForExitAsync(timeout);
            return new ExitProcessResult(target.ProcessId, true, confirmed,
                confirmed ? null : "Timeout");
        }
        catch (Exception exception)
        {
            return new ExitProcessResult(target.ProcessId, true, false,
                exception.GetType().Name + ": " + exception.Message);
        }
    }
}

internal sealed class SystemExitProcessTarget : IExitProcessTarget
{
    private readonly Process process;
    private readonly int processId;
    private readonly long processStartUtcTicks;

    public SystemExitProcessTarget(Process process)
    {
        this.process = process;
        processId = process.Id;
        processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
    }

    public int ProcessId => processId;

    public void RequestExit()
    {
        // The caller may dispose its Process wrapper after a bounded request timeout.
        // A late native Kill worker owns a separate wrapper and must verify process
        // identity again, rather than accessing a disposed wrapper or a reused PID.
        using var current = Process.GetProcessById(processId);
        if (current.HasExited || current.StartTime.ToUniversalTime().Ticks != processStartUtcTicks)
            throw new InvalidOperationException("Client process identity changed during exit.");
        current.Kill(entireProcessTree: false);
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
