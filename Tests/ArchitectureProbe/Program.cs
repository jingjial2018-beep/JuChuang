using JuChuang.Models;
using JuChuang.Services;

var failures = new List<string>();

Run("Activity requires a confirmed new badge before notification", () =>
{
    var noBadge = BadgeResult.NoBadge;
    var dot = BadgeResult.Dot;
    var one = new BadgeResult(true, 1, BadgeConfidenceLevel.High);
    var two = new BadgeResult(true, 2, BadgeConfidenceLevel.High);

    Assert(!BadgeAttentionPolicy.IsNewUnread(null, one),
        "initial historical unread must only establish a baseline");
    Assert(BadgeAttentionPolicy.IsNewUnread(noBadge, dot),
        "new dot after a known empty baseline should notify");
    Assert(BadgeAttentionPolicy.IsNewUnread(noBadge, one),
        "new count after a known empty baseline should notify");
    Assert(!BadgeAttentionPolicy.IsNewUnread(dot, one),
        "improved dot recognition must not notify twice");
    Assert(!BadgeAttentionPolicy.IsNewUnread(one, one),
        "unchanged unread count must not notify");
    Assert(BadgeAttentionPolicy.IsNewUnread(one, two),
        "increased reliable count should notify");
    Assert(!BadgeAttentionPolicy.IsNewUnread(two, noBadge),
        "badge disappearance must not notify");
});

Run("ClientLaunchContext rejects baseline PID", () =>
{
    var context = new ClientLaunchContext(ClientKind.WeChat, [100, 200]);
    Assert(!context.CanClaim(ClientKind.WeChat, 100), "baseline PID must not be claimable");
    Assert(!context.CanClaim(ClientKind.WhatsApp, 300), "wrong client kind must not be claimable");
    Assert(context.CanClaim(ClientKind.WeChat, 300), "new WeChat PID should be claimable");
    context.Claim(300, new IntPtr(0x1234));
    Assert(context.ClaimedProcessId == 300, "claimed PID was not stored");
    Assert(context.ClaimedHandle == new IntPtr(0x1234), "claimed HWND was not stored");
    Assert(!context.CanClaim(ClientKind.WeChat, 301), "context must only claim one process");
});

Run("ClientLaunchContext honors exact CreateProcess PID", () =>
{
    var context = new ClientLaunchContext(ClientKind.WeChat, [100, 200]);
    context.ExpectProcess(43210);

    Assert(!context.CanClaim(ClientKind.WeChat, 300),
        "when CreateProcess returns an authoritative PID, unrelated new PIDs must be rejected");
    Assert(context.CanClaim(ClientKind.WeChat, 43210),
        "the exact CreateProcess PID must be claimable");

    context.Claim(43210, new IntPtr(0x5678));
    Assert(context.ClaimedProcessId == 43210, "exact PID was not stored after claim");
});

Run("WeChat profile status respects window role and keeps account identity on rebind", () =>
{
    var entry = NewWeChatEntry();
    entry.Status = "已接入";
    entry.AccountId = "wxid_arch_probe";
    entry.ProfileState = AccountProfileState.Pending;
    entry.Handle = new IntPtr(0x2222);

    entry.SessionState = ClientSessionState.Login;
    Assert(entry.StatusSummary == "已接入 · 等待登录",
        "QR login window must display waiting for login instead of local profile status");

    entry.ProfileState = AccountProfileState.WaitingForLocalData;
    Assert(entry.StatusSummary == "已接入 · 等待登录",
        "profile reading failures must not mask the login window state");

    entry.SessionState = ClientSessionState.Main;
    entry.ProfileState = AccountProfileState.Pending;
    Assert(entry.StatusSummary == "已接入 · 资料读取中",
        "logged-in main window should show profile reading progress");

    entry.ProfileState = AccountProfileState.ReadyWithoutAvatar;
    Assert(entry.StatusSummary == "已接入 · 资料待更新",
        "incomplete avatar data should use the unified profile status");

    entry.ProfileState = AccountProfileState.CompatibilityIssue;
    Assert(entry.StatusSummary == "已接入 · 资料待更新",
        "profile compatibility errors must use the same concise status");

    entry.ProfileState = AccountProfileState.ReadyFromCache;
    Assert(entry.StatusSummary == "已接入 · 使用缓存资料",
        "cached identity must be labeled as cached");

    entry.ProfileState = AccountProfileState.Ready;
    Assert(entry.StatusSummary == "已接入",
        "fully ready profile state should not add a suffix");

    var previousAccountId = entry.AccountId;
    entry.Handle = new IntPtr(0x3333);
    entry.SessionState = ClientSessionState.Rebinding;
    Assert(entry.StatusSummary == "已接入",
        "unknown/rebinding role should not guess login or profile progress");
    Assert(entry.AccountId == previousAccountId,
        "changing/rebinding HWND must not clear account identity");
});

Run("Manual detach state survives HWND replacement", () =>
{
    var entry = NewWeChatEntry();
    entry.IsManuallyDetached = true;
    entry.Handle = new IntPtr(0x1111);
    entry.Handle = new IntPtr(0x4444);

    Assert(entry.IsManuallyDetached,
        "manual detach is account-session state and must survive an HWND change");
});

Run("Diagnostic clipboard report redacts account identifiers", () =>
{
    var entry = NewWeChatEntry();
    const string rawAccountId = "wxid_sensitive_arch_probe";
    const string rawIdentity = "C:\\Users\\SensitiveUser\\Documents\\xwechat_files\\wxid_sensitive_arch_probe";
    entry.AccountId = rawAccountId;
    entry.IdentityKey = rawIdentity;
    entry.ClientVersion = "4.0.test";
    entry.ProfileState = AccountProfileState.WaitingForLocalData;
    entry.ProfileDiagnosticCode = "SelfRowMissing";
    entry.ProfileDiagnosticDetail =
        $"no contact row for '{rawAccountId}' under C:\\Users\\SensitiveUser\\Documents\\xwechat_files";
    entry.MessageAlertCount = 7;
    entry.AlertDisplayMode = AlertDisplayMode.Count;

    var report = DiagnosticLogService.BuildClipboardReport(entry);

    Assert(report.Contains("AccountToken:", StringComparison.Ordinal),
        "diagnostic report should contain a stable hashed account token");
    Assert(!report.Contains(rawAccountId, StringComparison.OrdinalIgnoreCase),
        "diagnostic report leaked the raw account id");
    Assert(!report.Contains(rawIdentity, StringComparison.OrdinalIgnoreCase),
        "diagnostic report leaked the raw identity key");
    Assert(!report.Contains("C:\\Users\\SensitiveUser", StringComparison.OrdinalIgnoreCase),
        "diagnostic report leaked a local user path");
    Assert(report.Contains("SelfRowMissing", StringComparison.Ordinal),
        "diagnostic stage should remain visible after redaction");
});

Run("WhatsApp does not expose WeChat profile status", () =>
{
    var entry = new ClientWindowEntry(
        IntPtr.Zero,
        0,
        string.Empty,
        string.Empty,
        ClientKind.WhatsApp,
        "WhatsApp",
        null,
        ClientSessionState.Main)
    {
        Status = "已嵌入"
    };

    Assert(entry.ProfileState == AccountProfileState.NotApplicable,
        "WhatsApp must not enter the WeChat profile pipeline");
    Assert(entry.StatusSummary == "已嵌入",
        "WhatsApp status should not contain WeChat profile text");
});

Run("AppSettings saves atomically, keeps a backup and recovers from a corrupt file", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "jnest-settings-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");
    var previousOverride = AppSettings.SettingsPathOverride;
    AppSettings.SettingsPathOverride = path;
    try
    {
        var first = new AppSettings { SidebarWidth = 111 };
        first.AccountNamesByIdentity["acct"] = "first-version";
        first.Save();
        Assert(File.Exists(path), "first save should create the settings file");

        var second = AppSettings.Load();
        Assert(second.SidebarWidth == 111, "settings should round-trip through Save/Load");
        second.SidebarWidth = 222;
        second.Save();

        Assert(File.Exists(path + ".bak"), "second save should keep the previous version as .bak");
        Assert(!File.Exists(path + ".tmp"), "no temporary file may remain after a successful save");
        Assert(AppSettings.Load().SidebarWidth == 222, "primary file should hold the latest version");

        File.WriteAllText(path, "{ this is not valid json");
        var recovered = AppSettings.Load();
        Assert(recovered.SidebarWidth == 111,
            "a corrupt primary file must fall back to the last good backup instead of defaults");
        Assert(recovered.AccountNamesByIdentity.TryGetValue("acct", out var name) && name == "first-version",
            "account names must survive recovery from the backup");
        Assert(Directory.EnumerateFiles(directory, "settings.json.corrupt-*").Any(),
            "the corrupt file should be kept aside for inspection");
        Assert(!File.Exists(path), "the corrupt primary file should be moved out of the way");

        recovered.Save();
        Assert(AppSettings.Load().SidebarWidth == 111,
            "saving after recovery must not rotate the corrupt content into the backup");
    }
    finally
    {
        AppSettings.SettingsPathOverride = previousOverride;
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
});

Run("AppSettings merges settings written by another process instead of overwriting them", () =>
{
    WithSettingsDirectory(path =>
    {
        new AppSettings { SidebarWidth = 100 }.Save();

        var processA = AppSettings.Load();
        var processB = AppSettings.Load();
        processB.WindowWidth = 900;
        processB.Save();

        processA.SidebarWidth = 150;
        processA.Save();

        var onDisk = AppSettings.Load();
        Assert(onDisk.SidebarWidth == 150, "the edit made by this process must be kept");
        Assert(onDisk.WindowWidth == 900,
            "a value written by another process in the meantime must not be overwritten by stale memory");
        Assert(processA.WindowWidth == 900, "in-memory settings should adopt the merged result");
    });
});

Run("AppSettings keeps the current edit on conflict and preserves the other version", () =>
{
    WithSettingsDirectory(path =>
    {
        new AppSettings { SidebarWidth = 100 }.Save();

        var processC = AppSettings.Load();
        var processD = AppSettings.Load();
        processD.SidebarWidth = 200;
        processD.Save();

        var notices = new List<string>();
        Action<string> handler = notices.Add;
        AppSettings.SaveProblem += handler;
        try
        {
            processC.SidebarWidth = 300;
            processC.Save();
        }
        finally
        {
            AppSettings.SaveProblem -= handler;
        }

        Assert(AppSettings.Load().SidebarWidth == 300, "on a true conflict the current edit wins");
        Assert(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "settings.json.conflict-*").Any(),
            "the overwritten disk version must be preserved for inspection");
        Assert(notices.Count == 1, "the user must be told about the conflict exactly once");
    });
});

Run("AppSettings never overwrites a settings file it cannot read, and recovers afterwards", () =>
{
    WithSettingsDirectory(path =>
    {
        new AppSettings { SidebarWidth = 100 }.Save();
        var settings = AppSettings.Load();
        settings.SidebarWidth = 400;

        var notices = new List<string>();
        Action<string> handler = notices.Add;
        AppSettings.SaveProblem += handler;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                settings.Save();
            }
        }
        finally
        {
            AppSettings.SaveProblem -= handler;
        }

        Assert(notices.Count == 1, "the user must be told that the save was skipped");
        Assert(AppSettings.Load().SidebarWidth == 100,
            "a file that could not be read must be left untouched");

        settings.Save();
        Assert(AppSettings.Load().SidebarWidth == 400,
            "once the file is readable again the pending change must be saved");
    });
});

Run("Scratch cleanup keeps live owners and removes orphaned or legacy leftovers", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "jnest-scratch-probe-" + Guid.NewGuid().ToString("N"));
    var scratch = Path.Combine(root, "scratch");
    var legacy = Path.Combine(root, "legacy");
    Directory.CreateDirectory(scratch);
    Directory.CreateDirectory(legacy);
    WeChatProfileService.ScratchDirectoryOverride = scratch;
    WeChatProfileService.LegacyScratchDirectoryOverride = legacy;
    try
    {
        var longAgo = DateTime.UtcNow.AddHours(-1);
        var ticks = WeChatProfileService.CurrentProcessStartTicks;

        string Create(string directory, string name, DateTime writtenUtc)
        {
            var filePath = Path.Combine(directory, name);
            File.WriteAllText(filePath, "x");
            File.SetLastWriteTimeUtc(filePath, writtenUtc);
            return filePath;
        }

        string OwnedName(int ownerPid, long startTicks)
            => $"contact-1-owner{ownerPid}-{startTicks:x}-{Guid.NewGuid():N}.db";

        var liveOwner = Create(scratch, WeChatProfileService.BuildScratchFileName(1234), longAgo);
        var reusedPid = Create(scratch, OwnedName(Environment.ProcessId, ticks + 1), longAgo);
        var deadOwner = Create(scratch, OwnedName(int.MaxValue, ticks), longAgo);
        var deadOwnerWal = deadOwner + "-wal";
        File.WriteAllText(deadOwnerWal, "x");
        File.SetLastWriteTimeUtc(deadOwnerWal, longAgo);
        var freshOrphan = Create(scratch, OwnedName(int.MaxValue, ticks), DateTime.UtcNow);
        var legacyOld = Create(legacy, $"contact-1-{Guid.NewGuid():N}.db", longAgo);
        var legacyYoung = Create(legacy, $"contact-1-{Guid.NewGuid():N}.db", DateTime.UtcNow);

        WeChatProfileService.CleanupTemporaryArtifacts(TimeSpan.FromMinutes(10));

        Assert(File.Exists(liveOwner),
            "a copy whose creator is still the same live process must be kept, whatever the process is named");
        Assert(!File.Exists(reusedPid),
            "a recycled PID with a different start time must not be treated as the live creator");
        Assert(!File.Exists(deadOwner), "a copy whose creator no longer exists must be removed");
        Assert(!File.Exists(deadOwnerWal), "the -wal companion of an orphaned copy must be removed too");
        Assert(File.Exists(freshOrphan), "a just-written orphan gets a short grace period");
        Assert(!File.Exists(legacyOld), "old copies without an owner marker are removed by age");
        Assert(File.Exists(legacyYoung), "recent copies without an owner marker are kept");
    }
    finally
    {
        WeChatProfileService.ScratchDirectoryOverride = null;
        WeChatProfileService.LegacyScratchDirectoryOverride = null;
        try { Directory.Delete(root, recursive: true); } catch { }
    }
});

Run("FailureThrottle reports a repeating failure once per interval", () =>
{
    var throttle = new FailureThrottle(TimeSpan.FromSeconds(30));
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    Assert(throttle.ShouldReport("scan", start), "the first failure must be reported");
    Assert(!throttle.ShouldReport("scan", start.AddSeconds(5)), "a repeat inside the interval must be suppressed");
    Assert(throttle.ShouldReport("badge", start.AddSeconds(5)), "a different source is throttled independently");
    Assert(throttle.ShouldReport("scan", start.AddSeconds(31)), "the failure is reported again after the interval");
});

Run("GuardedRunner isolates a failing scan and keeps later scans working", () =>
{
    var captured = new List<Exception>();
    var completedCalls = 0;

    var asyncFailure = GuardedRunner.RunAsync(
        async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("scan failed");
        },
        captured.Add).GetAwaiter().GetResult();
    var success = GuardedRunner.RunAsync(
        async () =>
        {
            await Task.Yield();
            completedCalls++;
        },
        captured.Add).GetAwaiter().GetResult();
    var syncFailure = GuardedRunner.RunAsync(
        () => throw new InvalidOperationException("failed before the first await"),
        captured.Add).GetAwaiter().GetResult();
    var failingCallback = GuardedRunner.RunAsync(
        () => throw new InvalidOperationException("boom"),
        _ => throw new InvalidOperationException("callback also fails")).GetAwaiter().GetResult();

    Assert(!asyncFailure && !syncFailure && !failingCallback, "failures must be reported as unsuccessful");
    Assert(success, "a scan after a failure must still run and succeed");
    Assert(completedCalls == 1, "the successful scan must actually have executed");
    Assert(captured.Count == 2, "each failure must be handed to the failure callback exactly once");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine("Architecture regression probe failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("Architecture regression probe passed.");
return 0;

ClientWindowEntry NewWeChatEntry()
    => new(
        new IntPtr(0x1000),
        1234,
        "WeChat",
        "微信",
        ClientKind.WeChat,
        "微信 1",
        "4.0.test",
        ClientSessionState.Main)
    {
        Status = "已嵌入",
        IsOnline = true,
        IsAttached = true
    };

static void WithSettingsDirectory(Action<string> body)
{
    var directory = Path.Combine(Path.GetTempPath(), "jnest-settings-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var previousOverride = AppSettings.SettingsPathOverride;
    var previousInterval = AppSettings.ProblemNoticeInterval;
    var settingsPath = Path.Combine(directory, "settings.json");
    AppSettings.SettingsPathOverride = settingsPath;
    AppSettings.ProblemNoticeInterval = TimeSpan.Zero;
    try
    {
        body(settingsPath);
    }
    finally
    {
        AppSettings.SettingsPathOverride = previousOverride;
        AppSettings.ProblemNoticeInterval = previousInterval;
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}

void Run(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

