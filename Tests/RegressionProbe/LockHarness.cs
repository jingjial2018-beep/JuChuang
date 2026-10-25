using System.Windows;
using System.Windows.Controls;
using JuChuang.Models;

namespace JuChuang;

// Only collaborators are replaced. MainWindow.Lock.cs is linked directly from
// production so tests exercise its actual ordering, awaits and exception paths.
public partial class MainWindow
{
    public readonly ReviewHost WindowHost = new();
    public readonly Border LockOverlay = new();
    public readonly Button MainMenuButton = new();
    public readonly PasswordBox LockPasswordInput = new();
    public readonly TextBlock LockErrorText = new();
    public readonly Button UnlockJNestButton = new();
    public readonly ListBox AccountsList = new();
    public readonly List<ClientWindowEntry> Entries = [];
    public ClientWindowEntry? SelectedEntry;
    public readonly TaskCompletionSource ScanEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ScanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ProtectedForReview => IsJNestProtected;
    public void LockForReview() => LockJNest_Click(this, new RoutedEventArgs());
    public Task UnlockForReview() => TryUnlockAsync();
    private void ShowHostedModalDialog(Window dialog) => throw new InvalidOperationException("Unexpected password setup dialog");
    private void DismissAccountToast(ClientWindowEntry entry) { }
    private void ShowSelectedEntry() { }
    private Task TryScanAndAttachAsync() { ScanEntered.TrySetResult(); return ScanCompletion.Task; }
    private static long? TryGetProcessStartUtcTicks(int pid) => null;
}

public sealed class ReviewHost
{
    public Visibility Visibility { get; set; }
    public int Suspensions { get; private set; }
    public void SuspendForModalDialog() => Suspensions++;
    public void ResumeAfterModalDialog() => Suspensions--;
}

