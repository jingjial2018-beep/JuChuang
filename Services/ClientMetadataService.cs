using System.Diagnostics;
using System.IO;
using JuChuang.Models;

namespace JuChuang.Services;

public sealed record WeChatAccountIdentity(
    string Key,
    string AccountId,
    string DisplayName,
    DateTime LastLoginUtc);

public static class ClientMetadataService
{
    public static IReadOnlyDictionary<int, WeChatAccountIdentity> ResolveWeChatAccounts(
        IEnumerable<WindowCandidate> candidates)
    {
        var processStarts = candidates
            .Where(candidate => candidate.Kind == ClientKind.WeChat)
            .Select(candidate => candidate.ProcessId)
            .Distinct()
            .Select(TryGetProcessStart)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .OrderBy(item => item.StartUtc)
            .ToArray();

        if (processStarts.Length == 0)
        {
            return new Dictionary<int, WeChatAccountIdentity>();
        }

        var accounts = FindLoginAccounts().ToList();
        var resolved = new Dictionary<int, WeChatAccountIdentity>();

        // Match in process-start order and consume each login record once. WeChat
        // launches the instance shell before it writes key_info.dat, so a global
        // minimum-distance match can swap two accounts that start within seconds of
        // each other. The launch order preserves the instance-index ordering used by
        // the desktop client and keeps each profile attached to its own window.
        foreach (var process in processStarts)
        {
            var match = accounts
                .Select(account => new
                {
                    Account = account,
                    Difference = (account.LastLoginUtc - process.StartUtc).Duration()
                })
                .OrderBy(item => item.Difference)
                .ThenBy(item => item.Account.LastLoginUtc)
                .ThenBy(item => item.Account.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (match is null)
            {
                continue;
            }

            resolved[process.ProcessId] = match.Account;
            accounts.Remove(match.Account);
        }

        return resolved;
    }

    public static IReadOnlyList<WeChatAccountIdentity> RankWeChatAccountsForProcess(int processId)
    {
        var processStart = TryGetProcessStart(processId);
        var accounts = FindLoginAccounts();
        if (processStart is null)
        {
            return accounts;
        }

        return accounts
            .OrderBy(account => (account.LastLoginUtc - processStart.Value.StartUtc).Duration())
            .ThenBy(account => account.LastLoginUtc)
            .ThenBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly object AccountCacheLock = new();
    private static IReadOnlyList<WeChatAccountIdentity> _cachedAccounts = [];
    private static DateTime _cachedAccountsUtc;

    public static IReadOnlyList<WeChatAccountIdentity> FindLoginAccounts()
    {
        lock (AccountCacheLock)
        {
            if (DateTime.UtcNow - _cachedAccountsUtc < TimeSpan.FromSeconds(15))
            {
                return _cachedAccounts;
            }

            _cachedAccounts = EnumerateLoginAccounts();
            _cachedAccountsUtc = DateTime.UtcNow;
            return _cachedAccounts;
        }
    }

    private static IReadOnlyList<WeChatAccountIdentity> EnumerateLoginAccounts()
    {
        var accounts = new List<WeChatAccountIdentity>();
        var loginRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tencent",
            "xwechat",
            "login");

        if (!Directory.Exists(loginRoot))
        {
            return accounts;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(loginRoot).ToArray();
        }
        catch
        {
            return accounts;
        }

        foreach (var directory in directories)
        {
            WeChatAccountIdentity? identity = null;
            try
            {
                var directoryName = Path.GetFileName(directory).Trim();
                var keyInfoPath = Path.Combine(directory, "key_info.dat");
                if (directoryName.Length is < 2 or > 64 || !File.Exists(keyInfoPath))
                {
                    continue;
                }

                identity = new WeChatAccountIdentity(
                    $"wechat-login:{directoryName.ToLowerInvariant()}",
                    directoryName,
                    directoryName,
                    File.GetLastWriteTimeUtc(keyInfoPath));
            }
            catch
            {
                // An account directory can disappear while WeChat logs in or out.
            }

            if (identity is not null)
            {
                accounts.Add(identity);
            }
        }

        return accounts
            .OrderBy(account => account.LastLoginUtc)
            .ThenBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (int ProcessId, DateTime StartUtc)? TryGetProcessStart(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (processId, process.StartTime.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }
}
