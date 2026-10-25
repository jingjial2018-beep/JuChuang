using System.Security.Cryptography;

namespace JuChuang.Services;

/// <summary>
/// A lock credential belongs only to the current JNest process. Restarting
/// JNest intentionally starts a new, unlocked session.
/// </summary>
internal static class LockCredentialService
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    // Kept for compatibility with existing probes that configure test paths.
    internal static string? DirectoryPathOverride { get; set; }
    private static readonly object Gate = new();
    private static byte[]? _salt;
    private static byte[]? _hash;

    internal static bool IsConfigured
    {
        get { lock (Gate) return _hash is not null; }
    }

    internal static bool IsRemovalRequest(bool changingExisting, string password, string confirmation)
        => changingExisting && password.Length == 0 && confirmation.Length == 0;

    internal static void SetPassword(string password)
    {
        Validate(password);
        lock (Gate)
        {
            if (_hash is not null)
                throw new InvalidOperationException("Credential exists; verify the old password before replacing it.");
            WriteCredential(password);
        }
    }

    internal static void ChangePassword(string oldPassword, string newPassword)
    {
        Validate(newPassword);
        lock (Gate)
        {
            if (!VerifyCore(oldPassword))
                throw new UnauthorizedAccessException("Invalid current lock password.");
            WriteCredential(newPassword);
        }
    }

    internal static void RemovePassword(string currentPassword)
    {
        lock (Gate)
        {
            if (JNestLockPersistence.IsLocked)
                throw new InvalidOperationException("Unlock JNest before removing its password.");
            if (!VerifyCore(currentPassword))
                throw new UnauthorizedAccessException("Invalid current lock password.");
            ClearCredential();
        }
    }

    internal static bool Verify(string password)
    {
        lock (Gate) return VerifyCore(password);
    }

    private static bool VerifyCore(string password)
    {
        if (_salt is null || _hash is null) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, _salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        try { return CryptographicOperations.FixedTimeEquals(actual, _hash); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private static void Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new ArgumentException("Lock password must contain at least six characters.", nameof(password));
    }

    private static void WriteCredential(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        ClearCredential();
        _salt = salt;
        _hash = hash;
    }

    private static void ClearCredential()
    {
        if (_salt is not null) CryptographicOperations.ZeroMemory(_salt);
        if (_hash is not null) CryptographicOperations.ZeroMemory(_hash);
        _salt = null;
        _hash = null;
    }
}
