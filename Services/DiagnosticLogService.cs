using System.IO;
using System.Security.Cryptography;
using System.Text;
using JuChuang.Models;

namespace JuChuang.Services;

/// <summary>
/// 聚窗本地诊断日志。仅记录窗口/账号状态与资料解析阶段；
/// 不记录聊天内容、数据库密钥、解密数据库内容或头像文件路径。
/// </summary>
public static class DiagnosticLogService
{
    private static readonly object SyncRoot = new();
    private const int RetentionDays = 14;
    private const int MaximumDetailLength = 600;

    private static string LogRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "logs");

    public static string CurrentLogPath => Path.Combine(
        LogRoot,
        $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log");

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

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogRoot);
                CleanupOldLogs();
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断日志永远不能影响窗口管理主流程。
        }
    }

    public static string BuildClipboardReport(ClientWindowEntry entry)
    {
        var builder = new StringBuilder();
        builder.AppendLine("JuChuang account diagnostics");
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
        }
        catch
        {
        }

        return redacted;
    }

    private static string Sanitize(string? value)
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
        return normalized.Length <= MaximumDetailLength
            ? normalized
            : normalized[..MaximumDetailLength] + "…";
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogRoot, "diagnostics-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
