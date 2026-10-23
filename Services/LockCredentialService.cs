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

    internal static void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ArgumentException("Lock password must contain at least eight characters.", nameof(password));
        if (IsConfigured) throw new InvalidOperationException("Credential exists; verify the old password before replacing it.");
        WriteCredential(password);
    }

    internal static void ChangePassword(string oldPassword, string newPassword)
    {
        if (!Verify(oldPassword)) throw new UnauthorizedAccessException("Invalid current lock password.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("Lock password must contain at least eight characters.", nameof(newPassword));
        WriteCredential(newPassword);
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
