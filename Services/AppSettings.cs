using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JuChuang.Services;

public sealed class AppSettings
{
    public string? WeChatExecutablePath { get; set; }

    // 窗口位置、大小和最大化状态已不再记忆（窗口尺寸固定）。保留这五个属性
    // 只是为了兼容旧版 settings.json，不要删除。
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool EnableTaskbarFlash { get; set; } = true;

    public bool EnableToastNotifications { get; set; } = true;

    public bool EnableNotificationSound { get; set; } = true;

    // Preserve existing automatic identity/profile behavior on upgrade.
    // Disabling stops new private WeChat memory/database reads and can leave
    // unverified accounts unnamed; it does not delete user-selected labels.
    public bool EnableLocalProfileRead { get; set; } = true;

    public bool DndEnabled { get; set; }

    public int DndStartMinutes { get; set; } = 22 * 60;

    public int DndEndMinutes { get; set; } = 8 * 60;

    public bool IsDoNotDisturbActive(TimeOnly time)
    {
        if (!DndEnabled) return false;

        var now = time.Hour * 60 + time.Minute;
        var start = Math.Clamp(DndStartMinutes, 0, 1439);
        var end = Math.Clamp(DndEndMinutes, 0, 1439);
        if (start == end) return true;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    public double? SidebarWidth { get; set; }

    public Dictionary<string, string> AccountNamesByIdentity { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> AccountOrder { get; set; } = [];

    private static readonly object SaveLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private const int MaximumKeptCopies = 3;
    private static DateTime _lastProblemNoticeUtc = DateTime.MinValue;

    // 最近一次成功 Load/Save 之后内存设置的快照，作为与磁盘版本做三方合并时的“共同祖先”。
    // 实例字段而不是静态字段：每个 AppSettings 实例各自记录自己读到/写出的版本。
    private JsonObject? _baseSnapshot;

    /// <summary>
    /// 设置无法按预期保存时触发（文件暂时无法读取、或与磁盘上较新的版本存在冲突）。
    /// 参数是可直接展示给用户的一句话。
    /// </summary>
    public static event Action<string>? SaveProblem;

    // 仅供测试使用。
    internal static string? SettingsPathOverride { get; set; }
    internal static TimeSpan ProblemNoticeInterval { get; set; } = TimeSpan.FromSeconds(60);

    private static string SettingsPath => SettingsPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "settings.json");

    // 上一次成功保存前的版本，由 File.Replace 在每次保存时自动轮换。
    private static string BackupPath => SettingsPath + ".bak";

    private enum DiskState { Missing, Readable, Unreadable, Corrupt }

    public static AppSettings Load()
    {
        // 主文件正常就用主文件；主文件缺失或损坏时回退到上一份成功保存的备份，
        // 而不是直接用默认值——否则下一次 Save 会把用户的账号名称、顺序等覆盖掉。
        lock (SaveLock)
        {
            var settings = TryLoad(SettingsPath, quarantineIfCorrupt: true)
                ?? TryLoad(BackupPath, quarantineIfCorrupt: false)
                ?? new AppSettings();
            settings._baseSnapshot = settings.ToNode();
            return settings;
        }
    }

    private JsonObject ToNode() => JsonSerializer.SerializeToNode(this, SerializerOptions)!.AsObject();

    private static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new JsonException("settings file contains null");
        settings.AccountNamesByIdentity = new Dictionary<string, string>(
            settings.AccountNamesByIdentity ?? [],
            StringComparer.OrdinalIgnoreCase);
        settings.AccountOrder = (settings.AccountOrder ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return settings;
    }

    private static AppSettings? TryLoad(string path, bool quarantineIfCorrupt)
    {
        // 读取被杀毒软件等短暂占用时重试几次，避免把“暂时读不了”当成“文件损坏”。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (Exception exception)
            {
                DiagnosticLogService.WriteActivityEvent(
                    $"settings-load-failed:{Path.GetFileName(path)}:{exception.GetType().Name}");
                // 只有内容确实无法解析时才隔离；权限/IO 问题不动原文件。
                // 此时内存里是备份或默认值，不会丢数据：Save 之前会重新复核磁盘版本并合并。
                if (quarantineIfCorrupt && exception is JsonException)
                {
                    QuarantineCorruptFile(path);
                }

                return null;
            }
        }
    }

    /// <summary>读取磁盘上的主设置文件，区分“不存在 / 可读 / 暂时读不了 / 内容损坏”。</summary>
    private static DiskState ReadDisk(out AppSettings? disk)
    {
        disk = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return DiskState.Missing;
                }

                disk = Parse(File.ReadAllText(SettingsPath));
                return DiskState.Readable;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (JsonException)
            {
                return DiskState.Corrupt;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DiskState.Unreadable;
            }
        }
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            // 用移动而不是复制：主文件让位后，下一次 Save 不会把损坏内容轮换进 .bak。
            File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            TrimCopies(path, ".corrupt-*");
        }
        catch
        {
            // 隔离只是尽力而为。
        }
    }

    private static void PreserveDiskCopy()
    {
        try
        {
            File.Copy(SettingsPath, $"{SettingsPath}.conflict-{DateTime.UtcNow:yyyyMMddHHmmssfff}", overwrite: false);
            TrimCopies(SettingsPath, ".conflict-*");
        }
        catch
        {
            // 保留副本只是尽力而为。
        }
    }

    private static void TrimCopies(string basePath, string suffixPattern)
    {
        var directory = Path.GetDirectoryName(basePath)!;
        foreach (var stale in Directory
                     .EnumerateFiles(directory, Path.GetFileName(basePath) + suffixPattern)
                     .OrderByDescending(file => file, StringComparer.Ordinal)
                     .Skip(MaximumKeptCopies))
        {
            File.Delete(stale);
        }
    }

    public bool Save()
    {
        string? notice = null;
        var saved = false;
        try
        {
            lock (SaveLock)
            {
                notice = SaveCore(out saved);
            }
        }
        catch (Exception exception)
        {
            // A read-only profile should not stop the window manager from working.
            DiagnosticLogService.WriteActivityEvent($"settings-save-failed:{exception.GetType().Name}");
            notice = "设置保存失败，本次修改仅在当前运行期间生效，请检查磁盘空间和目录权限后重试";
        }

        if (notice is not null)
        {
            RaiseSaveProblem(notice);
        }
        return saved;
    }

    // 保存前先复核磁盘上的版本，而不是直接用内存里的旧配置覆盖：
    //  - 磁盘文件暂时读不了：不写入，提示用户（原文件原封不动）。
    //  - 磁盘版本自上次读写后被别的进程改过：按属性做三方合并，不覆盖对方的新设置。
    //  - 磁盘文件已损坏：隔离后写入当前内存设置。
    private string? SaveCore(out bool saved)
    {
        saved = false;
        string? notice = null;
        var state = ReadDisk(out var disk);
        switch (state)
        {
            case DiskState.Unreadable:
                DiagnosticLogService.WriteActivityEvent("settings-save-skipped:disk-unreadable");
                return "设置文件暂时无法读取，本次修改未保存";
            case DiskState.Corrupt:
                DiagnosticLogService.WriteActivityEvent("settings-disk-corrupt-at-save");
                QuarantineCorruptFile(SettingsPath);
                break;
            case DiskState.Readable when _baseSnapshot is not null:
                notice = MergeWithDisk(disk!);
                break;
        }

        WriteAtomically();
        _baseSnapshot = ToNode();
        saved = true;
        return notice;
    }

    private string? MergeWithDisk(AppSettings disk)
    {
        var baseNode = _baseSnapshot!;
        var theirs = disk.ToNode();
        if (JsonNode.DeepEquals(theirs, baseNode))
        {
            return null; // 磁盘版本自上次读写后没有变化。
        }

        var ours = ToNode();
        var merged = new JsonObject();
        var conflict = false;
        var keys = baseNode.Select(pair => pair.Key)
            .Concat(ours.Select(pair => pair.Key))
            .Concat(theirs.Select(pair => pair.Key))
            .Distinct();
        foreach (var key in keys)
        {
            var b = baseNode[key];
            var o = ours[key];
            var t = theirs[key];
            JsonNode? chosen;
            if (JsonNode.DeepEquals(o, b))
            {
                chosen = t; // 当前进程没改这一项：采用磁盘上的值。
            }
            else if (JsonNode.DeepEquals(t, b))
            {
                chosen = o; // 磁盘上没改这一项：保留当前修改。
            }
            else
            {
                chosen = o; // 双方都改了：以当前修改为准，并记录冲突。
                if (!JsonNode.DeepEquals(o, t))
                {
                    conflict = true;
                }
            }

            merged[key] = chosen?.DeepClone();
        }

        var mergedSettings = merged.Deserialize<AppSettings>()
            ?? throw new JsonException("merged settings are null");
        mergedSettings.AccountNamesByIdentity = new Dictionary<string, string>(
            mergedSettings.AccountNamesByIdentity ?? [],
            StringComparer.OrdinalIgnoreCase);
        mergedSettings.AccountOrder = (mergedSettings.AccountOrder ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CopyFrom(mergedSettings);

        if (conflict)
        {
            PreserveDiskCopy();
            DiagnosticLogService.WriteActivityEvent("settings-merge-conflict");
            return "设置已与磁盘上较新的版本合并，冲突项以当前修改为准";
        }

        DiagnosticLogService.WriteActivityEvent("settings-merged");
        return null;
    }

    private void CopyFrom(AppSettings source)
    {
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(this, property.GetValue(source));
            }
        }
    }

    private void WriteAtomically()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        // 先完整写入并刷盘到临时文件，再原子替换主文件；写到一半断电或崩溃时，
        // 主文件仍是完整的旧版本，旧版本同时保留为 .bak。
        var temporaryPath = SettingsPath + ".tmp";
        using (var stream = new FileStream(
                   temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(this, SerializerOptions));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(SettingsPath))
        {
            File.Replace(temporaryPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, SettingsPath);
        }
    }

    private static void RaiseSaveProblem(string message)
    {
        var now = DateTime.UtcNow;
        if (now - _lastProblemNoticeUtc < ProblemNoticeInterval)
        {
            return; // 同样的问题在一分钟内只提示一次。
        }

        _lastProblemNoticeUtc = now;
        try
        {
            SaveProblem?.Invoke(message);
        }
        catch
        {
            // 提示失败不能影响保存流程。
        }
    }
}
