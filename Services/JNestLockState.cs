namespace JuChuang.Services;

/// <summary>
/// Lock state is deliberately independent of HWND and visual-layer state.
/// Only the main-window coordinator may transition into Unlocked after a
/// verified credential check and successful safe restore.
/// </summary>
internal sealed class JNestLockState
{
    internal enum Phase { Unlocked, Locking, Locked, Unlocking }
    internal Phase Current { get; private set; } = Phase.Unlocked;
    internal bool IsProtected => Current != Phase.Unlocked;

    internal bool BeginLock()
    {
        if (Current != Phase.Unlocked) return false;
        Current = Phase.Locking;
        return true;
    }

    internal void LockCompleted()
    {
        if (Current != Phase.Locking) throw new InvalidOperationException("Unexpected lock transition.");
        Current = Phase.Locked;
    }

    internal bool BeginUnlock(bool credentialVerified)
    {
        if (!credentialVerified || Current != Phase.Locked) return false;
        Current = Phase.Unlocking;
        return true;
    }

    internal void UnlockCompleted()
    {
        if (Current != Phase.Unlocking) throw new InvalidOperationException("Unexpected unlock transition.");
        Current = Phase.Unlocked;
    }

    internal void FailClosed() => Current = Phase.Locked;
}
