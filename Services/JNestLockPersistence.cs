using System.IO;

namespace JuChuang.Services;

/// <summary>
/// Lock marker persists the locked state across ordinary application restarts.
/// The marker contains no secret or account data. Failure to persist the marker
/// must fail the lock operation rather than silently producing a nonpersistent lock.
/// </summary>
internal static class JNestLockPersistence
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JNest");
    private static readonly string MarkerPath = Path.Combine(DirectoryPath, "lock-active.marker");

    internal static bool IsLocked => File.Exists(MarkerPath);

    internal static void SaveLocked()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = MarkerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, "locked-v1");
            File.Move(temporary, MarkerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) {}
        }
    }

    internal static void SaveUnlocked()
    {
        if (File.Exists(MarkerPath))
            File.Delete(MarkerPath);
    }
}
