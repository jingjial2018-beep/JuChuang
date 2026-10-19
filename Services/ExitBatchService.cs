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
        var results = new Task<ExitProcessResult>[targets.Count];
        var requested = new List<(int Index, IExitProcessTarget Target)>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            try
            {
                target.RequestExit();
                requested.Add((index, target));
            }
            catch (Exception exception)
            {
                results[index] = Task.FromResult(new ExitProcessResult(
                    target.ProcessId, false, false, exception.GetType().Name + ": " + exception.Message));
            }
        }
        foreach (var (index, target) in requested)
            results[index] = ConfirmAsync(target, timeout);
        return await Task.WhenAll(results);
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

internal sealed class SystemExitProcessTarget(Process process) : IExitProcessTarget
{
    public int ProcessId => process.Id;

    public void RequestExit() => process.Kill(entireProcessTree: false);

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
