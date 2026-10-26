using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JuChuang.Models;

namespace JuChuang.Services;

/// <summary>
/// JNest 本地诊断日志。仅记录窗口/账号状态与资料解析阶段；
/// 不记录聊天内容、数据库密钥、解密数据库内容或头像文件路径。
/// </summary>
public static class DiagnosticLogService
{
    private static readonly object SyncRoot = new();
    private const int RetentionDays = 7;
    private const long MaximumFileBytes = 10L * 1024 * 1024;
    private const long MaximumTotalBytes = 50L * 1024 * 1024;
    private const int MaximumStackLength = 1800;
    private static readonly bool DetailedTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("JNEST_DIAGNOSTIC_DETAIL"), "1", StringComparison.Ordinal);
    private const int MaximumDetailLength = 600;
    private sealed record RmSnapshot(int Generation, string Status, string Code, string Token, string Detail);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, RmSnapshot> RmSnapshots = new();
    internal static void RecordRmSnapshot(int pid, int generation, string status, string code, string? identityKey, string detail)
        => RmSnapshots[pid] = new RmSnapshot(generation, status, code, CreateAccountToken(identityKey), detail);
    internal static void ClearRmSnapshot(int pid) => RmSnapshots.TryRemove(pid, out _);


    // Test-only path override; normal production logging keeps the existing location.
    internal static string? DirectoryPathOverride { get; set; }

    private static string LogRoot => DirectoryPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "logs");

    public static string CurrentLogPath => Path.Combine(
        LogRoot,
        $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log");

    // High-frequency native window traces are opt-in for targeted diagnostics only.
    // Set JNEST_DIAGNOSTIC_DETAIL=1 before starting JNest to enable for one process.
    public static void WriteDebugEvent(string eventName, int processId = 0)
    {
        if (DetailedTraceEnabled) WriteActivityEvent(eventName, processId);
    }

    public static void WriteActivityEvent(string eventName, int processId = 0)
    {
        try
        {
            var line = $"{DateTime.UtcNow:O}\t{Sanitize(eventName)}\tpid={processId}{Environment.NewLine}";
            AppendLine(line);
        }
        catch
        {
            // Diagnostics must never interrupt notification delivery.
        }
    }

    /// <summary>
    /// 记录后台任务/异步入口中被兜底捕获的异常。只写异常类型、HRESULT、出错方法和
    /// 已遮蔽用户目录的消息，不含堆栈全文，也不含任何聊天内容。
    /// </summary>
    public static void WriteErrorEvent(string source, Exception exception)
    {
        try
        {
            var site = exception.TargetSite is { } method
                ? $"{method.DeclaringType?.Name}.{method.Name}"
                : null;
            var line =
                $"{DateTime.UtcNow:O}\tbackground-error" +
                $"\tsource={Sanitize(source)}" +
                $"\ttype={Sanitize(exception.GetType().Name)}" +
                $"\thresult=0x{exception.HResult:X8}" +
                $"\tsite={Sanitize(site)}" +
                $"\tdetail={Sanitize(RedactDetail(exception.Message, null))}" +
                $"\tstack={Sanitize(RedactDetail(exception.StackTrace, null), MaximumStackLength)}" +
                Environment.NewLine;
            AppendLine(line);
        }
        catch
        {
            // 诊断日志永远不能影响窗口管理主流程。
        }
    }

    public static void WriteAccountEvent(ClientWindowEntry entry, string eventName)
    {
        try
        {
            var accountToken = CreateAccountToken(entry.IdentityKey ?? entry.AccountId);
            var diagnosticCode = Sanitize(entry.ProfileDiagnosticCode);
            var diagnosticDetail = Sanitize(RedactDetail(entry.ProfileDiagnosticDetail, entry.AccountId));
            var version = Sanitize(entry.ClientVersion);

            var line =
                $"{DateTime.UtcNow:O}\t{Sanitize(eventName)}" +
                $"\tkind={entry.Kind}" +
                $"\tpid={entry.ProcessId}" +
                $"\thwnd=0x{entry.Handle.ToInt64():X}" +
                $"\tsession={entry.SessionState}" +
                $"\tgeneration={entry.AccountGeneration}" +
                $"\tattached={entry.IsAttached}" +
                $"\tonline={entry.IsOnline}" +
                $"\tprofile={entry.ProfileState}" +
                $"\taccount={accountToken}" +
                $"\tversion={version}" +
                $"\tdiagnostic={diagnosticCode}" +
                $"\tdetail={diagnosticDetail}" +
                $"\tavatar={(entry.HasAvatar ? "yes" : "no")}" +
                Environment.NewLine;

            AppendLine(line);
        }
        catch
        {
            // 诊断日志永远不能影响窗口管理主流程。
        }
    }

    public static string BuildClipboardReport(ClientWindowEntry entry)
    {
        var builder = new StringBuilder();
        builder.AppendLine("JNest account diagnostics");
        builder.AppendLine($"TimeUtc: {DateTime.UtcNow:O}");
        builder.AppendLine($"Kind: {entry.Kind}");
        builder.AppendLine($"ClientVersion: {entry.ClientVersion ?? "-"}");
        builder.AppendLine($"ProcessId: {entry.ProcessId}");
        builder.AppendLine($"WindowHandle: 0x{entry.Handle.ToInt64():X}");
        builder.AppendLine($"SessionState: {entry.SessionState}");
        builder.AppendLine($"AccountGeneration: {entry.AccountGeneration}");
        builder.AppendLine($"AccountToken: {CreateAccountToken(entry.IdentityKey ?? entry.AccountId)}");
        builder.AppendLine($"Attached: {entry.IsAttached}");
        builder.AppendLine($"Online: {entry.IsOnline}");
        builder.AppendLine($"ManuallyDetached: {entry.IsManuallyDetached}");
        builder.AppendLine($"ProfileState: {entry.ProfileState}");
        builder.AppendLine($"ProfileDiagnosticCode: {entry.ProfileDiagnosticCode ?? "-"}");
        builder.AppendLine($"ProfileDiagnosticDetail: {Sanitize(RedactDetail(entry.ProfileDiagnosticDetail, entry.AccountId))}");
        if (RmSnapshots.TryGetValue(entry.ProcessId, out var rm)
            && rm.Generation == entry.AccountGeneration)
        {
            builder.AppendLine($"RmOwnershipStatus: {rm.Status}");
            builder.AppendLine($"RmMatchedAccountToken: {rm.Token}");
            builder.AppendLine($"RmDiagnosticCode: {rm.Code}");
            builder.AppendLine($"RmDiagnosticDetail: {Sanitize(rm.Detail)}");
        }
        else
        {
            builder.AppendLine("RmOwnershipStatus: NotChecked");
            builder.AppendLine("RmMatchedAccountToken: -");
            builder.AppendLine("RmDiagnosticCode: -");
        }
        builder.AppendLine($"AvatarAvailable: {entry.HasAvatar}");
        builder.AppendLine($"UnreadMode: {entry.AlertDisplayMode}");
        builder.AppendLine($"UnreadCount: {entry.MessageAlertCount}");
        builder.AppendLine($"LastSeenUtc: {entry.LastSeenUtc:O}");
        builder.AppendLine($"WindowMissingSinceUtc: {entry.WindowMissingSinceUtc?.ToString("O") ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Privacy: no chat content, database key, decrypted database, nickname, full account id, or avatar path is included.");
        return builder.ToString();
    }

    private static string CreateAccountToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
            return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
        }
        catch
        {
            return "-";
        }
    }

    private static string? RedactDetail(string? value, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var redacted = value;
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                redacted = redacted.Replace(
                    userProfile,
                    "%USERPROFILE%",
                    StringComparison.OrdinalIgnoreCase);
            }

            // 诊断信息有时来自数据库/客户端保存的旧路径，不一定属于当前 Windows
            // 登录用户。统一遮掉任意 C:\\Users\\<name> 前缀，避免复制或日志中泄露用户名。
            redacted = Regex.Replace(
                redacted,
                @"(?i)[A-Z]:\\Users\\[^\\\r\n]+",
                "%USERPROFILE%");

            if (!string.IsNullOrWhiteSpace(Environment.UserName))
            {
                redacted = redacted.Replace(
                    Environment.UserName,
                    "<user>",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(accountId))
            {
                redacted = redacted.Replace(
                    accountId,
                    "<account>",
                    StringComparison.OrdinalIgnoreCase);
            }

            // 当 AccountId 还没成功绑定时，detail 里仍可能出现 wxid_*。
            redacted = Regex.Replace(
                redacted,
                @"(?i)\bwxid_[A-Za-z0-9_-]+\b",
                "<account>");
        }
        catch
        {
        }

        return redacted;
    }

    private static string Sanitize(string? value, int maxLength = MaximumDetailLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "…";
    }

    private static DateTime _lastCleanupUtc = DateTime.MinValue;
    private static string? _lastActiveLogPath;

    private static void AppendLine(string line)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(LogRoot);
            var path = ResolveActiveLogPath(Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(path, line, Encoding.UTF8);
            var rotated = !string.Equals(path, _lastActiveLogPath, StringComparison.OrdinalIgnoreCase);
            _lastActiveLogPath = path;
            if (rotated || DateTime.UtcNow - _lastCleanupUtc >= TimeSpan.FromMinutes(10))
            {
                _lastCleanupUtc = DateTime.UtcNow;
                CleanupOldLogs(path);
            }
        }
    }

    // Keep the original daily name for compatibility; rotate into numbered files
    // once the UTF-8 byte cap would be exceeded (no truncate of live entries).
    private static string ResolveActiveLogPath(int bytesToAppend)
    {
        var basePath = CurrentLogPath;
        for (var index = 0; index < 10000; index++)
        {
            var path = index == 0 ? basePath
                : Path.Combine(LogRoot, $"diagnostics-{DateTime.UtcNow:yyyyMMdd}-{index:D3}.log");
            if (!File.Exists(path) || new FileInfo(path).Length + bytesToAppend <= MaximumFileBytes)
                return path;
        }
        throw new IOException("Diagnostic log rotation index exhausted.");
    }

    private static void CleanupOldLogs(string activePath)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var files = Directory.EnumerateFiles(LogRoot, "diagnostics-*.log")
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (string.Equals(file.FullName, activePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (file.LastWriteTimeUtc >= cutoff && total <= MaximumTotalBytes)
                    continue;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch
                {
                    // A locked diagnostics file can be removed on the next rotation.
                }
            }
        }
        catch
        {
            // Diagnostics must not disrupt the host process.
        }
    }
}
