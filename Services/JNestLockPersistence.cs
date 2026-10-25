using System.IO;

namespace JuChuang.Services;

/// <summary>
/// Lock state is session-scoped. No on-disk lock marker is created or read.
/// The existing lock UI calls remain unchanged.
/// </summary>
internal static class JNestLockPersistence
{
    internal static string? DirectoryPathOverride { get; set; }
    private static string DirectoryPath => DirectoryPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JNest");
    private static bool _locked;
    internal static bool IsLocked => _locked;
    internal static void SaveLocked() => _locked = true;
    internal static void SaveUnlocked() => _locked = false;

    internal static void DeleteLegacyMarker()
    {
        var path = Path.Combine(DirectoryPath, "lock-active.marker");
        if (File.Exists(path)) File.Delete(path);
    }
}
