using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JuChuang.Services;

public sealed record CachedWeChatProfile(
    string IdentityKey,
    string AccountId,
    string Nickname,
    string? AvatarPath,
    DateTime UpdatedUtc);

/// <summary>
/// 聚窗自己的只读资料缓存层。
/// 仅保存已经从微信本地资料成功解析出的昵称，并把已存在的本地头像复制到
/// %LOCALAPPDATA%\JuChuang\profile-cache。不会保存数据库密钥、解密数据库或聊天数据。
/// </summary>
public sealed class WeChatProfileCacheService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, CachedWeChatProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);

    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "profile-cache");

    private static string IndexPath => Path.Combine(CacheRoot, "profiles.json");

    public WeChatProfileCacheService()
    {
        LoadIndex();
    }

    public bool TryGet(
        string identityKey,
        string accountId,
        out WeChatLocalProfile profile)
    {
        profile = null!;
        if (string.IsNullOrWhiteSpace(identityKey)
            || string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (!_profiles.TryGetValue(identityKey, out var cached)
                || !cached.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var avatarPath = IsUsableImage(cached.AvatarPath)
                ? cached.AvatarPath
                : null;
            var nickname = string.IsNullOrWhiteSpace(cached.Nickname)
                ? accountId
                : cached.Nickname.Trim();

            profile = new WeChatLocalProfile(accountId, nickname, avatarPath);
            return true;
        }
    }

    /// <summary>
    /// 将新读取到的资料与已有缓存合并。新头像有效时复制到聚窗目录；
    /// 新头像缺失时保留同一账号以前的缓存头像。
    /// </summary>
    public WeChatLocalProfile Store(
        string identityKey,
        WeChatLocalProfile liveProfile)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return liveProfile;
        }

        lock (_syncRoot)
        {
            _profiles.TryGetValue(identityKey, out var existing);
            var sameAccount = existing is not null
                && existing.AccountId.Equals(
                    liveProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase);

            var nickname = !string.IsNullOrWhiteSpace(liveProfile.Nickname)
                ? liveProfile.Nickname.Trim()
                : sameAccount && !string.IsNullOrWhiteSpace(existing!.Nickname)
                    ? existing.Nickname
                    : liveProfile.AccountId;

            var avatarPath = CopyAvatarToCache(identityKey, liveProfile.AvatarPath);
            if (avatarPath is null
                && sameAccount
                && IsUsableImage(existing!.AvatarPath))
            {
                avatarPath = existing.AvatarPath;
            }

            // 同一个 identityKey 如果对应的 AccountId 已经变化，不复用旧账号头像。
            if (!sameAccount && avatarPath is null)
            {
                DeleteCachedAvatar(identityKey);
            }

            var cached = new CachedWeChatProfile(
                identityKey,
                liveProfile.AccountId,
                nickname,
                avatarPath,
                DateTime.UtcNow);
            _profiles[identityKey] = cached;
            SaveIndex();

            return new WeChatLocalProfile(
                liveProfile.AccountId,
                cached.Nickname,
                cached.AvatarPath);
        }
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return;
            }

            var profiles = JsonSerializer.Deserialize<List<CachedWeChatProfile>>(
                File.ReadAllText(IndexPath));
            if (profiles is null)
            {
                return;
            }

            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.IdentityKey)
                    || string.IsNullOrWhiteSpace(profile.AccountId))
                {
                    continue;
                }

                _profiles[profile.IdentityKey] = profile;
            }
        }
        catch
        {
            // 缓存损坏不能影响窗口管理主流程。
        }
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            var tempPath = IndexPath + ".tmp";
            var json = JsonSerializer.Serialize(
                _profiles.Values
                    .OrderBy(profile => profile.IdentityKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, IndexPath, overwrite: true);
        }
        catch
        {
            // 缓存写入失败时继续使用当前内存资料。
        }
    }

    private static string? CopyAvatarToCache(string identityKey, string? sourcePath)
    {
        if (!IsUsableImage(sourcePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(CacheRoot);
            var extension = DetectImageExtension(sourcePath!);
            if (extension is null)
            {
                return null;
            }

            var baseName = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identityKey)))
                .ToLowerInvariant();
            var destination = Path.Combine(CacheRoot, $"avatar-{baseName}{extension}");

            if (!Path.GetFullPath(sourcePath!).Equals(
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath!, destination, overwrite: true);
            }

            return destination;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteCachedAvatar(string identityKey)
    {
        try
        {
            if (!Directory.Exists(CacheRoot))
            {
                return;
            }

            var baseName = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identityKey)))
                .ToLowerInvariant();
            foreach (var file in Directory.EnumerateFiles(
                         CacheRoot,
                         $"avatar-{baseName}.*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 图片可能正在被 WPF 解码；下一次覆盖即可。
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsUsableImage(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && File.Exists(path)
           && DetectImageExtension(path) is not null;

    private static string? DetectImageExtension(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(header);
            if (read >= 3
                && header[0] == 0xFF
                && header[1] == 0xD8
                && header[2] == 0xFF)
            {
                return ".jpg";
            }

            if (read >= 8
                && header[..8].SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                return ".png";
            }

            if (read >= 6
                && (header[..6].SequenceEqual("GIF87a"u8)
                    || header[..6].SequenceEqual("GIF89a"u8)))
            {
                return ".gif";
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
