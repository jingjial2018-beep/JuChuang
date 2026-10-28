using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace JuChuang.Services;

public sealed record WeChatLocalProfile(
    string AccountId,
    string Nickname,
    string? AvatarPath);

public sealed record WeChatProfileDiagnostic(
    string AccountId,
    string AccountDirectory,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> ContactColumns,
    IReadOnlyDictionary<string, string?> SelfFields);

public enum WeChatProfileFailureStage
{
    None = 0,
    AccountDirectoryNotFound,
    ContactDatabaseUnreadable,
    KeyExtractionFailed,
    AmbiguousAccountKey,
    DatabaseOpenFailed,
    ContactTableMissing,
    SelfRowMissing,
    AvatarCacheMissing,
    WalMergeInconsistent,
}

public sealed record WeChatProfileReadResult(
    WeChatLocalProfile? Profile,
    WeChatProfileFailureStage Stage,
    string? Detail);

/// <summary>
/// Reads only the current account's row from WeChat 4.x contact.db. It never opens
/// message/session databases and never persists a database key. A short-lived
/// decrypted contact database copy is removed on a best-effort basis after the
/// read, on normal exit, and during later startup cleanup.
/// </summary>
public static partial class WeChatProfileService
{
    private const int PageSize = 4096;
    private const int ReserveSize = 80;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private const long MaximumRegionSize = 0x10000000;
    private const int ScanChunkSize = 4 * 1024 * 1024;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteOpenUri = 0x00000040;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;

    private static readonly byte[] ConfigCipherName = "com.Tencent.WCDB.Config.Cipher"u8.ToArray();
    private static readonly byte[] ConfigXorMask = Convert.FromHexString(
        "D2C7442458020000004889442450488B" +
        "450048844C2448488944254048584C24");

    [GeneratedRegex("[xX]'([0-9a-fA-F]{64,192})'", RegexOptions.CultureInvariant)]
    private static partial Regex HexLiteralRegex();

    public static WeChatLocalProfile? TryReadProfile(int processId, string accountId)
    {
        var result = TryReadProfileDetailed(processId, accountId);
        if (result.Profile is null)
        {
            DiagnosticLogService.WriteDebugEvent($"wechat-profile-read-{result.Stage}", processId);
        }
        else if (result.Profile.AvatarPath is null)
        {
            DiagnosticLogService.WriteDebugEvent("wechat-profile-avatar-cache-missing", processId);
        }

        return result.Profile;
    }

    public static WeChatLocalProfile? TryReadMatchingProfile(
        int processId,
        IEnumerable<string> accountIds)
        => TryReadMatchingProfileDetailed(processId, accountIds).Profile;

    public static WeChatProfileReadResult TryReadMatchingProfileDetailed(
        int processId,
        IEnumerable<string> accountIds)
    {
        var context = new ProfileReadContext();
        try
        {
            var candidates = new List<AccountDatabaseCandidate>();
            var locatedAccountCount = 0;
            foreach (var accountId in accountIds
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var location = FindAccountLocation(accountId);
                if (location is null)
                {
                    continue;
                }

                locatedAccountCount++;
                var firstPage = ReadFirstPage(location.Value.ContactDatabase);
                if (firstPage is null)
                {
                    continue;
                }

                candidates.Add(new AccountDatabaseCandidate(
                    accountId,
                    location.Value.AccountDirectory,
                    location.Value.ContactDatabase,
                    firstPage));
            }

            if (candidates.Count == 0)
            {
                context.Fail(
                    locatedAccountCount == 0
                        ? WeChatProfileFailureStage.AccountDirectoryNotFound
                        : WeChatProfileFailureStage.ContactDatabaseUnreadable,
                    locatedAccountCount == 0
                        ? "no matching local account directory was found"
                        : "matching account directories exist, but contact.db could not be read");
                return context.ToResult(null);
            }

            var keyMatch = ExtractContactKey(
                processId,
                candidates.Select(candidate => candidate.FirstPage).ToArray());
            if (keyMatch is null)
            {
                context.Fail(
                    WeChatProfileFailureStage.KeyExtractionFailed,
                    $"no verified contact.db key found in WeChat process {processId}");
                return context.ToResult(null);
            }

            if (keyMatch.Value.PageIndex < 0)
            {
                CryptographicOperations.ZeroMemory(keyMatch.Value.Key);
                context.Fail(WeChatProfileFailureStage.AmbiguousAccountKey,
                    "multiple account databases matched this process; refusing uncertain identity binding");
                return context.ToResult(null);
            }

            var selected = candidates[keyMatch.Value.PageIndex];
            var profile = WithKnownContactKey(
                processId,
                selected.ContactDatabase,
                keyMatch.Value.Key,
                context,
                databasePath => ReadCurrentAccount(
                    processId,
                    databasePath,
                    selected.AccountId,
                    selected.AccountDirectory,
                    context));
            return context.ToResult(profile);
        }
        catch (Exception exception)
        {
            context.Fail(WeChatProfileFailureStage.DatabaseOpenFailed, exception.Message);
            return context.ToResult(null);
        }
    }

    public static WeChatProfileReadResult TryReadProfileDetailed(int processId, string accountId)
    {
        var context = new ProfileReadContext();
        try
        {
            var location = FindAccountLocation(accountId);
            if (location is null)
            {
                context.Fail(
                    WeChatProfileFailureStage.AccountDirectoryNotFound,
                    $"no account directory matches '{accountId}' under Documents\\xwechat_files " +
                    "or Documents\\WeChat Files (custom storage location?)");
                return context.ToResult(null);
            }

            var profile = WithDecryptedContactDatabase(
                processId,
                location.Value.ContactDatabase,
                context,
                databasePath => ReadCurrentAccount(processId, databasePath, accountId, location.Value.AccountDirectory, context));
            return context.ToResult(profile);
        }
        catch (Exception exception)
        {
            context.Fail(WeChatProfileFailureStage.DatabaseOpenFailed, exception.Message);
            return context.ToResult(null);
        }
    }

    internal static WeChatProfileDiagnostic? InspectCurrentAccount(int processId, string accountId)
    {
        var context = new ProfileReadContext();
        try
        {
            var location = FindAccountLocation(accountId);
            if (location is null)
            {
                return null;
            }

            return WithDecryptedContactDatabase(
                processId,
                location.Value.ContactDatabase,
                context,
                databasePath => InspectCurrentAccountDatabase(databasePath, accountId, location.Value.AccountDirectory));
        }
        catch
        {
            return null;
        }
    }

    private static T? WithDecryptedContactDatabase<T>(
        int processId,
        string contactDatabase,
        ProfileReadContext context,
        Func<string, T?> action)
        where T : class
    {
        var firstPage = ReadFirstPage(contactDatabase);
        if (firstPage is null)
        {
            context.Fail(
                WeChatProfileFailureStage.ContactDatabaseUnreadable,
                $"cannot read the first page of '{contactDatabase}' (locked or truncated)");
            return null;
        }

        var keyMatch = ExtractContactKey(processId, [firstPage]);
        if (keyMatch is null)
        {
            context.Fail(
                WeChatProfileFailureStage.KeyExtractionFailed,
                $"no verified key found in WeChat process {processId}; the memory layout offsets " +
                "are bound to a specific WeChat 4.x build and may need an update after a WeChat upgrade");
            return null;
        }

        if (keyMatch.Value.PageIndex < 0)
        {
            CryptographicOperations.ZeroMemory(keyMatch.Value.Key);
            context.Fail(WeChatProfileFailureStage.AmbiguousAccountKey,
                "multiple verified keys for profile database; identity cannot be trusted");
            return null;
        }

        return WithKnownContactKey(processId, contactDatabase, keyMatch.Value.Key, context, action);
    }

    private static T? WithKnownContactKey<T>(
        int processId,
        string contactDatabase,
        byte[] key,
        ProfileReadContext context,
        Func<string, T?> action)
        where T : class
    {
        string? decryptedPath = null;
        try
        {
            var scratchDirectory = EnsureScratchDirectory();
            CleanupTemporaryArtifacts(ReadTimeCleanupMinimumAge);
            decryptedPath = Path.Combine(scratchDirectory, BuildScratchFileName(processId));
            DecryptDatabase(contactDatabase, decryptedPath, key);

            // WCDB keeps contact.db in WAL mode: rows written after the last checkpoint
            // (typically freshly logged-in accounts) only exist inside contact.db-wal.
            // Decrypt those frames and merge them into the decrypted copy so SQLite sees them.
            var walPath = contactDatabase + "-wal";
            var appliedFrames = File.Exists(walPath)
                ? MergeDecryptedWal(walPath, decryptedPath, key)
                : 0;
            context.WalFramesApplied = appliedFrames;

            try
            {
                return action(decryptedPath);
            }
            catch (Exception) when (appliedFrames > 0)
            {
                // The merged WAL image may be inconsistent (partial frame / torn write).
                // Fall back to the main database alone instead of failing outright.
                DiagnosticLogService.WriteDebugEvent("wechat-profile-wal-fallback", processId);
                context.WalMergeFallback = true;
                DeleteTemporaryDatabase(decryptedPath);
                DecryptDatabase(contactDatabase, decryptedPath, key);
                return action(decryptedPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (decryptedPath is not null)
            {
                DeleteTemporaryDatabase(decryptedPath);
            }
        }
    }

    // 解密副本只存在于读取资料的几秒钟内。目录放在 JNest 自己的数据目录下并限制为仅当前用户
    // 和 SYSTEM 可访问，不再使用共享的 %TEMP%。旧版本遗留在 %TEMP%\duokaiqi-profile 的
    // 残留文件也由下面的清理一并处理。
    private static readonly TimeSpan ReadTimeCleanupMinimumAge = TimeSpan.FromMinutes(10);

    // 仅供测试使用：把临时目录指到测试专用位置，避免触碰真实用户数据。
    internal static string? ScratchDirectoryOverride { get; set; }
    internal static string? LegacyScratchDirectoryOverride { get; set; }

    private static string ScratchDirectory => ScratchDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "tmp",
        "profile");

    private static string LegacyScratchDirectory => LegacyScratchDirectoryOverride
        ?? Path.Combine(Path.GetTempPath(), "duokaiqi-profile");

    private static string EnsureScratchDirectory()
    {
        var directory = ScratchDirectory;
        // Never write a plaintext database into a directory whose ACL could not be
        // verified. Existing directories may have been created by an older version.
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Current user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, inherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit,
            PropagationFlags.None, AccessControlType.Allow));

        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            info.Create(security);
        }
        else
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Profile scratch directory is a reparse point.");
            info.SetAccessControl(security);
        }

        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Profile scratch directory is a reparse point.");

        return directory;
    }

    /// <summary>
    /// 清理不再由有效 JNest 进程持有的解密副本。新文件名记录创建者 JNest PID；
    /// 仍运行的创建者保留副本，防止多个 JNest 实例互相删除正在读取的数据库。
    /// 旧版本没有创建者标记的文件继续按年龄保守清理。
    /// </summary>
    internal static int CleanupTemporaryArtifacts(TimeSpan minimumAge)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - minimumAge;
        foreach (var directory in new[] { ScratchDirectory, LegacyScratchDirectory })
        {
            if (!Directory.Exists(directory)) continue;
            string[] files;
            try { files = Directory.EnumerateFiles(directory, "contact-*").ToArray(); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var lastWritten = File.GetLastWriteTimeUtc(file);
                    var fileName = Path.GetFileName(file);
                    var match = OwnerPattern.Match(fileName);
                    if (match.Success
                        && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid)
                        && long.TryParse(match.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ownerStartTicks))
                    {
                        // 创建者仍是同一个进程实例（PID 与启动时间都吻合）时不清理。
                        // 不依赖进程显示名称，也不会把被系统复用的 PID 当成仍存活的创建者。
                        if (IsLiveScratchOwner(ownerPid, ownerStartTicks)) continue;
                        // 容许刚写完的文件完成打开/关闭过程；避免与并发退出争用。
                        if (lastWritten > DateTime.UtcNow - OrphanGracePeriod) continue;
                    }
                    else if (lastWritten > cutoff)
                    {
                        continue;
                    }

                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 文件仍被其他进程占用或权限不足，留给下一次清理。
                }
            }
        }
        return removed;
    }

    internal static void CleanupCurrentProcessArtifacts()
    {
        if (!Directory.Exists(ScratchDirectory)) return;
        string[] files;
        try { files = Directory.EnumerateFiles(ScratchDirectory, "contact-*").ToArray(); }
        catch { return; }

        foreach (var file in files)
        {
            var match = OwnerPattern.Match(Path.GetFileName(file));
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var ownerPid)
                || !long.TryParse(match.Groups[2].Value, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var ownerStartTicks)
                || ownerPid != Environment.ProcessId
                || ownerStartTicks != OwnerStartTicks.Value) continue;
            try { File.Delete(file); }
            catch { /* Startup cleanup will retry after an abnormal shutdown. */ }
        }
    }

    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromSeconds(5);

    // 文件名：contact-{微信PID}-owner{创建者PID}-{创建者启动时间ticks(十六进制)}-{GUID}.db
    private static readonly Regex OwnerPattern = new(
        @"^contact-\d+-owner(\d+)-([0-9a-f]+)-[0-9a-fA-F]{32}\.db(?:-wal|-shm)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<long> OwnerStartTicks = new(() =>
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0L; // 取不到启动时间时记为 0：清理端只能确认 PID 存活，无法区分 PID 复用。
        }
    });

    internal static long CurrentProcessStartTicks => OwnerStartTicks.Value;

    internal static string BuildScratchFileName(int weChatProcessId)
        => $"contact-{weChatProcessId}-owner{Environment.ProcessId}-{OwnerStartTicks.Value:x}-{Guid.NewGuid():N}.db";

    /// <summary>
    /// 创建者是否仍是“同一个进程实例”。用 PID + 启动时间核对，而不是进程名：
    /// 用户改名 exe、从不同目录运行都不影响判断，PID 被复用时启动时间也对不上。
    /// </summary>
    private static bool IsLiveScratchOwner(int ownerPid, long recordedStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(ownerPid);
            if (process.HasExited) return false;
            if (recordedStartTicks == 0) return true;
            return process.StartTime.ToUniversalTime().Ticks == recordedStartTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch
        {
            // 无法确认进程身份时（例如权限不足）宁可保留副本，避免误删活跃读取。
            return true;
        }
    }

    private static void DeleteTemporaryDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            for (var attempt = 0; attempt < 5 && File.Exists(path); attempt++)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    if (attempt < 4)
                    {
                        Thread.Sleep(50 * (attempt + 1));
                    }
                }
            }
        }
    }

    private static WeChatLocalProfile? ReadCurrentAccount(
        int processId,
        string databasePath,
        string accountId,
        string accountDirectory,
        ProfileReadContext context)
    {
        NativeSqliteDatabase database;
        try
        {
            database = NativeSqliteDatabase.OpenReadOnly(databasePath);
        }
        catch (Exception exception)
        {
            context.Fail(
                WeChatProfileFailureStage.DatabaseOpenFailed,
                $"cannot open the decrypted copy with SQLite: {exception.Message}");
            return null;
        }

        using var _ = database;
        var columns = database.QueryColumnNames("PRAGMA table_info(contact)", nameColumnIndex: 1);
        if (!columns.Contains("username", StringComparer.OrdinalIgnoreCase))
        {
            context.Fail(
                WeChatProfileFailureStage.ContactTableMissing,
                "the contact table (or its username column) is missing after decryption; " +
                "the key or the page layout does not match this WeChat build");
            return null;
        }

        var wantedColumns = new[]
            {
                "username", "nick_name", "remark", "alias",
                "big_head_url", "small_head_url", "head_img_md5"
            }
            .Where(name => columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var row = database.QuerySingle(
            $"SELECT {string.Join(",", wantedColumns.Select(QuoteIdentifier))} " +
            "FROM contact WHERE username=? LIMIT 1",
            accountId);
        if (row is null)
        {
            context.Fail(
                WeChatProfileFailureStage.SelfRowMissing,
                $"no contact row for '{accountId}'; the account id may not match the directory " +
                "or the self row has not been written yet");
            return null;
        }

        var nickname = GetField(row, "nick_name");
        if (string.IsNullOrWhiteSpace(nickname))
        {
            nickname = GetField(row, "remark");
        }
        if (string.IsNullOrWhiteSpace(nickname))
        {
            nickname = accountId;
        }

        var smallHeadUrl = GetField(row, "small_head_url");
        var bigHeadUrl = GetField(row, "big_head_url");
        var contentHash = GetField(row, "head_img_md5");

        var avatarPath = FindExactAvatarCache(
            accountDirectory,
            accountId,
            smallHeadUrl,
            bigHeadUrl,
            contentHash);

        // WeChat 4.1.x can keep the self avatar only in the dedicated encrypted
        // db_storage/head_image/head_image.db even when temp/head_image has not
        // materialized a plain cache file yet. Read only the current account row
        // as a local fallback; no network request is introduced.
        avatarPath ??= TryReadSelfAvatarFromHeadImageDatabase(
            processId,
            accountDirectory,
            accountId);

        if (avatarPath is null)
        {
            context.AvatarCacheMiss = true;
        }

        return new WeChatLocalProfile(accountId, nickname.Trim(), avatarPath);
    }

    private static string? FindExactAvatarCache(
        string accountDirectory,
        string accountId,
        string? smallHeadUrl,
        string? bigHeadUrl,
        string? contentHash)
    {
        var accountRoot = Directory.GetParent(accountDirectory)?.FullName;
        var cacheDirectories = new[]
        {
            Path.Combine(accountDirectory, "temp", "head_image"),
            accountRoot is null ? null : Path.Combine(accountRoot, "all_users", "head_imgs")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        if (cacheDirectories.Length == 0)
        {
            return null;
        }

        var cacheNames = new List<string>();
        foreach (var url in new[] { smallHeadUrl, bigHeadUrl })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            cacheNames.Add(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))
                .ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(contentHash))
        {
            cacheNames.Add(contentHash.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            cacheNames.Add(accountId.Trim());
            cacheNames.Add(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(accountId.Trim())))
                .ToLowerInvariant());
        }

        var extensions = new[] { string.Empty, ".jpg", ".jpeg", ".png", ".gif" };
        foreach (var directory in cacheDirectories)
        {
            foreach (var cacheName in cacheNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var extension in extensions)
                {
                    var directPath = Path.Combine(directory, cacheName + extension);
                    if (IsPlainImageFile(directPath))
                    {
                        return directPath;
                    }

                    if (cacheName.Length >= 2)
                    {
                        var shardedPath = Path.Combine(
                            directory,
                            cacheName[..2],
                            cacheName + extension);
                        if (IsPlainImageFile(shardedPath))
                        {
                            return shardedPath;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string? TryReadSelfAvatarFromHeadImageDatabase(
        int processId,
        string accountDirectory,
        string accountId)
    {
        var databasePath = Path.Combine(
            accountDirectory,
            "db_storage",
            "head_image",
            "head_image.db");
        if (!File.Exists(databasePath))
        {
            return null;
        }

        var avatarContext = new ProfileReadContext();
        try
        {
            return WithDecryptedContactDatabase(
                processId,
                databasePath,
                avatarContext,
                decryptedPath => ExtractSelfAvatarBlob(
                    decryptedPath,
                    accountDirectory,
                    accountId));
        }
        catch (Exception)
        {
            DiagnosticLogService.WriteDebugEvent("wechat-avatar-db-fallback-failed", processId);
            return null;
        }
    }

    private static string? ExtractSelfAvatarBlob(
        string databasePath,
        string accountDirectory,
        string accountId)
    {
        using var database = NativeSqliteDatabase.OpenReadOnly(databasePath);
        var columns = database.QueryColumnNames(
            "PRAGMA table_info(head_image)",
            nameColumnIndex: 1);
        if (!columns.Contains("username", StringComparer.OrdinalIgnoreCase)
            || !columns.Contains("image_buffer", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var image = database.QuerySingleBlob(
            "SELECT image_buffer FROM head_image WHERE username=? LIMIT 1",
            accountId);
        if (image is null || image.Length < 8)
        {
            return null;
        }

        var extension = DetectPlainImageExtension(image);
        if (extension is null)
        {
            return null;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuChuang",
            "avatar-db-cache");
        Directory.CreateDirectory(cacheRoot);

        var identity = accountDirectory + "\n" + accountId;
        var fileName = "avatar-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24] + extension;
        var destination = Path.Combine(cacheRoot, fileName);

        File.WriteAllBytes(destination, image);
        return destination;
    }

    private static string? DetectPlainImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3
            && data[0] == 0xFF
            && data[1] == 0xD8
            && data[2] == 0xFF)
        {
            return ".jpg";
        }

        if (data.Length >= 8
            && data[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (data.Length >= 6
            && (data[..6].SequenceEqual("GIF87a"u8)
                || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return ".gif";
        }

        return null;
    }

    private static bool IsPlainImageFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < 8)
            {
                return false;
            }

            return DetectPlainImageExtension(header) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static WeChatProfileDiagnostic InspectCurrentAccountDatabase(
        string databasePath,
        string accountId,
        string accountDirectory)
    {
        using var database = NativeSqliteDatabase.OpenReadOnly(databasePath);
        var tables = database.QueryColumnNames(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",
            nameColumnIndex: 0);
        var columns = database.QueryColumnNames("PRAGMA table_info(contact)", nameColumnIndex: 1);
        var selfRow = columns.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : database.QuerySingle(
                  $"SELECT {string.Join(",", columns.Select(QuoteIdentifier))} " +
                  "FROM contact WHERE username=? LIMIT 1",
                  accountId)
              ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        return new WeChatProfileDiagnostic(accountId, accountDirectory, tables, columns, selfRow);
    }

    private static string QuoteIdentifier(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string? GetField(IReadOnlyDictionary<string, string?> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : null;

    internal static string? FindContactDatabasePath(string accountId)
        => FindAccountLocation(accountId)?.ContactDatabase;

    private static (string AccountDirectory, string ContactDatabase)? FindAccountLocation(string accountId)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var roots = new[]
        {
            Path.Combine(documents, "xwechat_files"),
            Path.Combine(documents, "WeChat Files")
        };

        var candidates = new List<(DateTime ModifiedUtc, string Directory, string Database)>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> accountDirectories;
            try
            {
                accountDirectories = Directory.EnumerateDirectories(root).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var directory in accountDirectories)
            {
                var name = Path.GetFileName(directory);
                if (!name.Equals(accountId, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(accountId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var contactDatabase = Path.Combine(directory, "db_storage", "contact", "contact.db");
                if (!File.Exists(contactDatabase))
                {
                    continue;
                }

                candidates.Add((File.GetLastWriteTimeUtc(contactDatabase), directory, contactDatabase));
            }
        }

        var selected = candidates.OrderByDescending(candidate => candidate.ModifiedUtc).FirstOrDefault();
        return selected.Database is null ? null : (selected.Directory, selected.Database);
    }

    private static byte[]? ReadFirstPage(string path)
    {
        var page = new byte[PageSize];
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ReadExact(stream, page) == PageSize ? page : null;
        }
        catch
        {
            return null;
        }
    }

    private static (int PageIndex, byte[] Key)? ExtractContactKey(
        int processId,
        IReadOnlyList<byte[]> firstPages)
    {
        if (firstPages.Count == 0)
        {
            return null;
        }

        var verifiedMatches = new Dictionary<int, byte[]>();
        var processHandle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var nameAddresses = FindBytes(processHandle, ConfigCipherName);
            foreach (var nameAddress in nameAddresses)
            {
                var pair = new byte[16];
                BitConverter.GetBytes(nameAddress).CopyTo(pair, 0);
                BitConverter.GetBytes((long)ConfigCipherName.Length).CopyTo(pair, 8);

                foreach (var pairAddress in FindBytes(processHandle, pair))
                {
                    var node = ReadMemory(processHandle, pairAddress - 0x10, 0x50);
                    if (node is null || node.Length < 0x40)
                    {
                        continue;
                    }

                    if (BitConverter.ToInt64(node, 0x10) != nameAddress
                        || BitConverter.ToInt64(node, 0x18) != ConfigCipherName.Length)
                    {
                        continue;
                    }

                    var configPointer = BitConverter.ToInt64(node, 0x28);
                    if (!IsProbablePointer(configPointer))
                    {
                        continue;
                    }

                    var cipherObject = ReadMemory(processHandle, configPointer + 0x88, 0x28);
                    if (cipherObject is null || cipherObject.Length < 0x18)
                    {
                        continue;
                    }

                    var dataPointer = BitConverter.ToInt64(cipherObject, 0x08);
                    var dataLength = BitConverter.ToInt64(cipherObject, 0x10);
                    if (!IsProbablePointer(dataPointer) || dataLength is <= 0 or > 1024)
                    {
                        continue;
                    }

                    var blob = ReadMemory(processHandle, dataPointer, checked((int)dataLength));
                    if (blob is null || blob.Length != dataLength)
                    {
                        continue;
                    }

                    for (var index = 0; index < blob.Length; index++)
                    {
                        blob[index] ^= ConfigXorMask[index % ConfigXorMask.Length];
                    }

                    var decoded = Encoding.ASCII.GetString(blob);
                    foreach (Match match in HexLiteralRegex().Matches(decoded))
                    {
                        var run = match.Groups[1].Value;
                        foreach (var start in CandidateStarts(run.Length))
                        {
                            if (start + 64 > run.Length)
                            {
                                continue;
                            }

                            var candidate = Convert.FromHexString(run.AsSpan(start, 64));
                            if (!IsProbableKey(candidate))
                            {
                                CryptographicOperations.ZeroMemory(candidate);
                                continue;
                            }

                            for (var pageIndex = 0; pageIndex < firstPages.Count; pageIndex++)
                            {
                                if (VerifyKey(candidate, firstPages[pageIndex], explicitSalt: null))
                                {
                                    if (!verifiedMatches.ContainsKey(pageIndex))
                                        verifiedMatches[pageIndex] = candidate.ToArray();
                                }
                            }

                            if (start + 96 <= run.Length)
                            {
                                var explicitSalt = Convert.FromHexString(run.AsSpan(start + 64, 32));
                                for (var pageIndex = 0; pageIndex < firstPages.Count; pageIndex++)
                                {
                                    if (VerifyKey(candidate, firstPages[pageIndex], explicitSalt))
                                    {
                                        var keyWithSalt = new byte[48];
                                        candidate.CopyTo(keyWithSalt, 0);
                                        explicitSalt.CopyTo(keyWithSalt, 32);
                                        CryptographicOperations.ZeroMemory(candidate);
                                        CryptographicOperations.ZeroMemory(explicitSalt);
                                        if (!verifiedMatches.ContainsKey(pageIndex))
                                            verifiedMatches[pageIndex] = keyWithSalt.ToArray();
                                        CryptographicOperations.ZeroMemory(keyWithSalt);
                                    }
                                }

                                CryptographicOperations.ZeroMemory(explicitSalt);
                            }

                            CryptographicOperations.ZeroMemory(candidate);
                        }
                    }
                }
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }

        if (verifiedMatches.Count == 1)
        {
            var match = verifiedMatches.Single();
            return (match.Key, match.Value);
        }

        foreach (var key in verifiedMatches.Values)
            CryptographicOperations.ZeroMemory(key);
        // -1 is an explicit ambiguous verdict, not a successful account match.
        return verifiedMatches.Count > 1 ? (-1, Array.Empty<byte>()) : null;
    }

    private sealed record AccountDatabaseCandidate(
        string AccountId,
        string AccountDirectory,
        string ContactDatabase,
        byte[] FirstPage);

    private static IEnumerable<int> CandidateStarts(int length)
    {
        var seen = new HashSet<int> { 0 };
        if (length > 96)
        {
            for (var start = 0; start < length - 63; start += 32)
            {
                seen.Add(start);
            }
            seen.Add(length - 64);
        }
        return seen;
    }

    private static bool VerifyKey(byte[] candidate, byte[] page, byte[]? explicitSalt)
    {
        if (page.Length < PageSize || candidate.Length != 32)
        {
            return false;
        }

        var salt = explicitSalt ?? page.AsSpan(0, 16).ToArray();
        if (salt.Length != 16)
        {
            return false;
        }

        var macSalt = salt.Select(value => (byte)(value ^ 0x3A)).ToArray();
        var macKey = Rfc2898DeriveBytes.Pbkdf2(
            candidate,
            macSalt,
            2,
            HashAlgorithmName.SHA512,
            32);
        var authenticated = new byte[PageSize - ReserveSize + 4];
        Buffer.BlockCopy(page, 16, authenticated, 0, PageSize - ReserveSize);
        BitConverter.GetBytes(1).CopyTo(authenticated, PageSize - ReserveSize);

        using var hmac = new HMACSHA512(macKey);
        var actual = hmac.ComputeHash(authenticated);
        var expected = page.AsSpan(PageSize - 64, 64);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);

        CryptographicOperations.ZeroMemory(macKey);
        CryptographicOperations.ZeroMemory(macSalt);
        CryptographicOperations.ZeroMemory(authenticated);
        CryptographicOperations.ZeroMemory(actual);
        if (explicitSalt is null)
        {
            CryptographicOperations.ZeroMemory(salt);
        }
        return matches;
    }

    private static void DecryptDatabase(string sourcePath, string destinationPath, byte[] key)
    {
        using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var page = new byte[PageSize];
        var pageNumber = 1;
        while (true)
        {
            Array.Clear(page);
            var bytesRead = ReadExact(input, page);
            if (bytesRead == 0)
            {
                break;
            }

            output.Write(DecryptPage(key, page, pageNumber));
            pageNumber++;
            if (bytesRead < PageSize)
            {
                break;
            }
        }
    }

    private static byte[] DecryptPage(byte[] keyMaterial, byte[] page, int pageNumber)
    {
        var key = keyMaterial.AsSpan(0, 32);
        var iv = page.AsSpan(PageSize - ReserveSize, 16);
        var output = new byte[PageSize];
        int encryptedOffset;
        int encryptedLength;

        if (pageNumber == 1)
        {
            encryptedOffset = 16;
            encryptedLength = PageSize - ReserveSize - 16;
            if (keyMaterial.Length == 48)
            {
                page.AsSpan(0, 16).CopyTo(output);
            }
            else
            {
                "SQLite format 3\0"u8.CopyTo(output);
            }
        }
        else
        {
            encryptedOffset = 0;
            encryptedLength = PageSize - ReserveSize;
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(page, encryptedOffset, encryptedLength);
        decrypted.CopyTo(output, encryptedOffset);
        CryptographicOperations.ZeroMemory(decrypted);
        return output;
    }

    private static int ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return offset;
    }

    /// <summary>
    /// Decrypts the frames of a WeChat WAL sidecar file and merges every committed frame
    /// into the already-decrypted main database copy. Frames whose salt does not match the
    /// WAL header belong to an older checkpoint cycle and stop the scan, mirroring SQLite's
    /// own recovery rules. Returns the number of frames applied.
    /// </summary>
    private static int MergeDecryptedWal(string walPath, string decryptedPath, byte[] key)
    {
        const int walHeaderSize = 32;
        const int walFrameHeaderSize = 24;
        const int maxWalPageNumber = 10_000_000;

        try
        {
            using var input = new FileStream(
                walPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(
                decryptedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var header = new byte[walHeaderSize];
            if (ReadExact(input, header) < walHeaderSize)
            {
                return 0;
            }

            var magic = ReadInt32BigEndian(header, 0);
            if (magic != 0x377F0682 && magic != 0x377F0683)
            {
                return 0;
            }

            var walPageSize = ReadInt32BigEndian(header, 8);
            if (walPageSize != PageSize)
            {
                // Unexpected page size: leave the main database copy untouched.
                return 0;
            }

            var salt = new byte[8];
            Buffer.BlockCopy(header, 16, salt, 0, 8);

            var frameHeader = new byte[walFrameHeaderSize];
            var encryptedPage = new byte[PageSize];
            var pending = new List<(int PageNumber, byte[] Data)>();
            var appliedFrames = 0;

            while (true)
            {
                Array.Clear(frameHeader);
                if (ReadExact(input, frameHeader) < walFrameHeaderSize)
                {
                    break;
                }

                Array.Clear(encryptedPage);
                if (ReadExact(input, encryptedPage) < PageSize)
                {
                    break;
                }

                if (!frameHeader.AsSpan(8, 8).SequenceEqual(salt))
                {
                    // Frames from a previous WAL cycle: stop, just like SQLite recovery.
                    break;
                }

                var pageNumber = ReadInt32BigEndian(frameHeader, 0);
                var commitSize = ReadInt32BigEndian(frameHeader, 4);
                if (pageNumber <= 0 || pageNumber > maxWalPageNumber)
                {
                    break;
                }

                pending.Add((pageNumber, DecryptPage(key, encryptedPage, pageNumber)));
                if (commitSize <= 0)
                {
                    continue;
                }

                // Commit frame: apply every pending page, then truncate to the committed size.
                foreach (var (pageNo, data) in pending)
                {
                    var offset = (long)(pageNo - 1) * PageSize;
                    if (offset + PageSize > output.Length)
                    {
                        output.SetLength(offset + PageSize);
                    }

                    output.Position = offset;
                    output.Write(data, 0, PageSize);
                }

                var committedLength = (long)commitSize * PageSize;
                if (committedLength > 0 && committedLength <= (long)maxWalPageNumber * PageSize)
                {
                    // SetLength grows with zeroes when a commit extends the database.
                    output.SetLength(committedLength);
                }

                appliedFrames += pending.Count;
                pending.Clear();
            }

            output.Flush();
            return appliedFrames;
        }
        catch
        {
            // A partially merged copy still opens fine for the pages already applied.
            return 0;
        }
    }

    private static int ReadInt32BigEndian(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    private sealed class ProfileReadContext
    {
        internal WeChatProfileFailureStage Stage { get; private set; } = WeChatProfileFailureStage.None;
        internal string? Detail { get; private set; }
        internal int WalFramesApplied;
        internal bool WalMergeFallback;
        internal bool AvatarCacheMiss;

        internal void Fail(WeChatProfileFailureStage stage, string detail)
        {
            if (Stage == WeChatProfileFailureStage.None)
            {
                Stage = stage;
                Detail = detail;
            }
        }

        internal WeChatProfileReadResult ToResult(WeChatLocalProfile? profile)
        {
            var parts = new List<string>();
            if (Detail is not null)
            {
                parts.Add(Detail);
            }

            if (WalFramesApplied > 0)
            {
                parts.Add($"wal-frames-applied={WalFramesApplied}");
            }

            if (WalMergeFallback)
            {
                parts.Add("wal-merge-fallback=true");
            }

            if (AvatarCacheMiss && profile is not null)
            {
                parts.Add("avatar-cache-miss");
            }

            var resultStage = profile is null
                ? Stage
                : AvatarCacheMiss
                    ? WeChatProfileFailureStage.AvatarCacheMissing
                    : WeChatProfileFailureStage.None;
            return new WeChatProfileReadResult(
                profile,
                resultStage,
                parts.Count == 0 ? null : string.Join("; ", parts));
        }
    }

    private static List<long> FindBytes(IntPtr processHandle, byte[] needle)
    {
        var hits = new List<long>();
        var scanBuffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize);
        long address = 0;
        try
        {
            while (true)
            {
                if (VirtualQueryEx(
                        processHandle,
                        new IntPtr(address),
                        out var info,
                        (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                {
                    break;
                }

                var baseAddress = info.BaseAddress.ToInt64();
                var regionSize = checked((long)info.RegionSize);
                if (info.State == MemCommit
                    && IsReadableProtection(info.Protect)
                    && regionSize is > 0 and < MaximumRegionSize)
                {
                    var carry = Array.Empty<byte>();
                    for (long offset = 0; offset < regionSize; offset += ScanChunkSize)
                    {
                        var bytesToRead = checked((int)Math.Min(ScanChunkSize, regionSize - offset));
                        if (!ReadProcessMemory(
                                processHandle,
                                new IntPtr(baseAddress + offset),
                                scanBuffer,
                                bytesToRead,
                                out var bytesReadNative)
                            || bytesReadNative == 0)
                        {
                            carry = Array.Empty<byte>();
                            continue;
                        }

                        var bytesRead = checked((int)bytesReadNative);
                        var chunk = scanBuffer.AsSpan(0, bytesRead);
                        if (carry.Length > 0)
                        {
                            var prefixLength = Math.Min(needle.Length - 1, bytesRead);
                            var boundary = new byte[carry.Length + prefixLength];
                            carry.CopyTo(boundary, 0);
                            chunk[..prefixLength].CopyTo(boundary.AsSpan(carry.Length));
                            AddBoundaryMatches(
                                boundary,
                                carry.Length,
                                needle,
                                baseAddress + offset - carry.Length,
                                hits);
                        }

                        AddMatches(chunk, needle, baseAddress + offset, hits);
                        var carryLength = Math.Min(Math.Max(0, needle.Length - 1), bytesRead);
                        carry = carryLength == 0
                            ? Array.Empty<byte>()
                            : chunk[^carryLength..].ToArray();
                    }
                }

                if (regionSize <= 0 || baseAddress > long.MaxValue - regionSize)
                {
                    break;
                }
                var next = baseAddress + regionSize;
                if (next <= address)
                {
                    break;
                }
                address = next;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanBuffer);
        }
        return hits;
    }

    private static void AddMatches(
        ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle,
        long baseAddress,
        ICollection<long> hits)
    {
        var consumed = 0;
        while (consumed <= haystack.Length - needle.Length)
        {
            var relative = haystack[consumed..].IndexOf(needle);
            if (relative < 0)
            {
                break;
            }

            var index = consumed + relative;
            hits.Add(baseAddress + index);
            consumed = index + 1;
        }
    }

    private static void AddBoundaryMatches(
        ReadOnlySpan<byte> boundary,
        int boundaryOffset,
        ReadOnlySpan<byte> needle,
        long baseAddress,
        ICollection<long> hits)
    {
        var consumed = 0;
        while (consumed <= boundary.Length - needle.Length)
        {
            var relative = boundary[consumed..].IndexOf(needle);
            if (relative < 0)
            {
                break;
            }

            var index = consumed + relative;
            if (index < boundaryOffset && index + needle.Length > boundaryOffset)
            {
                hits.Add(baseAddress + index);
            }
            consumed = index + 1;
        }
    }

    private static byte[]? ReadMemory(IntPtr processHandle, long address, int length)
    {
        if (address <= 0 || length <= 0)
        {
            return null;
        }

        var buffer = new byte[length];
        if (!ReadProcessMemory(processHandle, new IntPtr(address), buffer, length, out var read)
            || read == 0)
        {
            return null;
        }

        if ((long)read == length)
        {
            return buffer;
        }

        Array.Resize(ref buffer, checked((int)read));
        return buffer;
    }

    private static bool IsReadableProtection(uint protection)
        => (protection & PageGuard) == 0 && (((protection & 0xFF) & 0xE6) != 0);

    private static bool IsProbablePointer(long value)
        => value is >= 0x10000 and < 0x0000800000000000;

    private static bool IsProbableKey(byte[] value)
        => value.Length == 32
           && value.Distinct().Count() >= 15
           && value.Any(item => item != 0)
           && value.Any(item => item != 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        internal IntPtr BaseAddress;
        internal IntPtr AllocationBase;
        internal uint AllocationProtect;
        internal uint Alignment1;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
        internal uint Alignment2;
    }

    private sealed class NativeSqliteDatabase : IDisposable
    {
        private IntPtr _handle;

        private NativeSqliteDatabase(IntPtr handle) => _handle = handle;

        internal static NativeSqliteDatabase OpenReadOnly(string path)
        {
            var immutableUri = new Uri(path, UriKind.Absolute).AbsoluteUri + "?immutable=1";
            var pathBytes = Utf8(immutableUri);
            var result = sqlite3_open_v2(
                pathBytes,
                out var database,
                SqliteOpenReadOnly | SqliteOpenUri,
                IntPtr.Zero);
            if (result != 0 || database == IntPtr.Zero)
            {
                var message = database == IntPtr.Zero ? $"SQLite error {result}" : GetError(database);
                if (database != IntPtr.Zero)
                {
                    sqlite3_close_v2(database);
                }
                throw new InvalidDataException(message);
            }
            return new NativeSqliteDatabase(database);
        }

        internal IReadOnlyList<string> QueryColumnNames(string sql, int nameColumnIndex)
        {
            using var statement = Prepare(sql);
            var values = new List<string>();
            while (true)
            {
                var result = sqlite3_step(statement.Handle);
                if (result == SqliteDone)
                {
                    break;
                }
                if (result != SqliteRow)
                {
                    throw new InvalidDataException(GetError(_handle));
                }
                values.Add(ReadText(statement.Handle, nameColumnIndex) ?? string.Empty);
            }
            return values;
        }

        internal byte[]? QuerySingleBlob(string sql, string parameter)
        {
            using var statement = Prepare(sql);
            var parameterBytes = Utf8(parameter, terminate: false);
            if (sqlite3_bind_text(
                    statement.Handle,
                    1,
                    parameterBytes,
                    parameterBytes.Length,
                    new IntPtr(-1)) != 0)
            {
                throw new InvalidDataException(GetError(_handle));
            }

            var result = sqlite3_step(statement.Handle);
            if (result == SqliteDone)
            {
                return null;
            }
            if (result != SqliteRow)
            {
                throw new InvalidDataException(GetError(_handle));
            }

            var length = sqlite3_column_bytes(statement.Handle, 0);
            var pointer = sqlite3_column_blob(statement.Handle, 0);
            if (pointer == IntPtr.Zero || length <= 0)
            {
                return null;
            }

            var value = new byte[length];
            Marshal.Copy(pointer, value, 0, length);
            return value;
        }

        internal Dictionary<string, string?>? QuerySingle(string sql, string parameter)
        {
            using var statement = Prepare(sql);
            var parameterBytes = Utf8(parameter, terminate: false);
            if (sqlite3_bind_text(
                    statement.Handle,
                    1,
                    parameterBytes,
                    parameterBytes.Length,
                    new IntPtr(-1)) != 0)
            {
                throw new InvalidDataException(GetError(_handle));
            }

            var result = sqlite3_step(statement.Handle);
            if (result == SqliteDone)
            {
                return null;
            }
            if (result != SqliteRow)
            {
                throw new InvalidDataException(GetError(_handle));
            }

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var columnCount = sqlite3_column_count(statement.Handle);
            for (var index = 0; index < columnCount; index++)
            {
                var name = Marshal.PtrToStringUTF8(sqlite3_column_name(statement.Handle, index)) ?? $"column_{index}";
                fields[name] = ReadText(statement.Handle, index);
            }
            return fields;
        }

        private NativeSqliteStatement Prepare(string sql)
        {
            var sqlBytes = Utf8(sql);
            var result = sqlite3_prepare_v2(_handle, sqlBytes, sqlBytes.Length, out var statement, IntPtr.Zero);
            if (result != 0 || statement == IntPtr.Zero)
            {
                throw new InvalidDataException(GetError(_handle));
            }
            return new NativeSqliteStatement(statement);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }
            sqlite3_close_v2(_handle);
            _handle = IntPtr.Zero;
        }

        private static string? ReadText(IntPtr statement, int column)
        {
            var pointer = sqlite3_column_text(statement, column);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }
            var length = sqlite3_column_bytes(statement, column);
            return length <= 0 ? string.Empty : Marshal.PtrToStringUTF8(pointer, length);
        }

        private static string GetError(IntPtr database)
            => Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "SQLite error";

        private static byte[] Utf8(string value, bool terminate = true)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (!terminate)
            {
                return bytes;
            }
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }
    }

    private sealed class NativeSqliteStatement : IDisposable
    {
        internal NativeSqliteStatement(IntPtr handle) => Handle = handle;
        internal IntPtr Handle { get; private set; }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero)
            {
                return;
            }
            sqlite3_finalize(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out nuint bytesRead);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        byte[] sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(
        IntPtr statement,
        int index,
        byte[] value,
        int byteCount,
        IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
