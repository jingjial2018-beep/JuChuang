using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JuChuang.Services;

/// <summary>
/// Per-user, non-plaintext lock credential. UI and client hiding must be
/// integrated before this service is exposed as a product feature.
/// </summary>
internal static class LockCredentialService
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JNest");
    private static readonly string CredentialPath = Path.Combine(DirectoryPath, "lock-credential.json");
    private sealed record Record(int Version, int Iterations, string Salt, string Hash);

    internal static bool IsConfigured => File.Exists(CredentialPath);

    // Pure policy used by both the UI and regression tests: only a pair of
    // empty inputs in change mode can request credential removal.
    internal static bool IsRemovalRequest(bool changingExisting, string password, string confirmation)
        => changingExisting && password.Length == 0 && confirmation.Length == 0;

    internal static void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new ArgumentException("Lock password must contain at least six characters.", nameof(password));
        if (IsConfigured) throw new InvalidOperationException("Credential exists; verify the old password before replacing it.");
        WriteCredential(password);
    }

    internal static void ChangePassword(string oldPassword, string newPassword)
    {
        if (!Verify(oldPassword)) throw new UnauthorizedAccessException("Invalid current lock password.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            throw new ArgumentException("Lock password must contain at least six characters.", nameof(newPassword));
        WriteCredential(newPassword);
    }

    // Cancellation is deliberately not represented by an empty stored password.
    // Preserve a verified credential until the user confirms removal.
    internal static void RemovePassword(string currentPassword)
    {
        // Refuse to remove the only key while a persisted lock remains active.
        if (JNestLockPersistence.IsLocked)
            throw new InvalidOperationException("Unlock JNest before removing its password.");
        if (!Verify(currentPassword))
            throw new UnauthorizedAccessException("Invalid current lock password.");
        File.Delete(CredentialPath);
    }

    internal static bool Verify(string password)
    {
        try
        {
            if (!IsConfigured) return false;
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(CredentialPath));
            if (record is null || record.Version != 1 || record.Iterations != Iterations) return false;
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            if (salt.Length != SaltBytes || expected.Length != HashBytes) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
        {
            return false; // Fail closed.
        }
    }

    private static void WriteCredential(string password)
    {
        Directory.CreateDirectory(DirectoryPath);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        var record = new Record(1, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        var temp = CredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record), Encoding.UTF8);
            File.Move(temp, CredentialPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
