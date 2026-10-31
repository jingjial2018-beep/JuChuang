using System.Diagnostics;
using System.Windows.Input;
using JuChuang.Models;
using JuChuang.Services;

var failures = new List<string>();

Run("WeChat Qt role geometry distinguishes 100%-DPI chat from login", () =>
{
    // PR #59 cold-start measurements: the previous 1,000,000-pixel area
    // heuristic incorrectly centered the 1027 x 739 main chat as a login.
    Assert(WeChatWindowRolePolicy.IsCompactLoginShape(296, 388, 96),
        "100%-DPI genuine compact login should remain centered");
    Assert(WeChatWindowRolePolicy.IsCompactLoginShape(440, 452, 96),
        "100%-DPI nearly-square login should remain centered");
    Assert(!WeChatWindowRolePolicy.IsCompactLoginShape(1027, 739, 96),
        "100%-DPI landscape main chat must fill the workspace");
    Assert(!WeChatWindowRolePolicy.IsCompactLoginShape(800, 600, 96),
        "a smaller landscape chat cannot be mistaken for a login");
});

Run("WeChat Qt login geometry uses DPI-normalized rather than physical-area limits", () =>
{
    Assert(WeChatWindowRolePolicy.IsCompactLoginShape(592, 776, 192),
        "200%-DPI compact login should remain centered");
    Assert(WeChatWindowRolePolicy.IsCompactLoginShape(444, 582, 144),
        "150%-DPI compact login should remain centered");
    Assert(!WeChatWindowRolePolicy.IsCompactLoginShape(2054, 1478, 192),
        "200%-DPI main chat should fill the workspace");
    Assert(!WeChatWindowRolePolicy.IsCompactLoginShape(1027, 739, 192),
        "landscape main chat should never be classified by physical area alone");
    Assert(!WeChatWindowRolePolicy.IsCompactLoginShape(0, 0, 96),
        "invalid window geometry must never imply Login");
});

Run("Account list ignores secondary click but keeps primary selection", () =>
{
    Assert(AccountSelectionInputPolicy.ShouldSuppress(MouseButton.Right),
        "right mouse button must not change account selection");
    Assert(!AccountSelectionInputPolicy.ShouldSuppress(MouseButton.Left),
        "left-button selection must remain available");
    Assert(!AccountSelectionInputPolicy.ShouldSuppress(MouseButton.Middle),
        "middle-button input must not be blocked as secondary click");
});

Run("Lock states are fail closed and transitions are authenticated", () =>
{
    var state = new JNestLockState();
    Assert(!state.IsProtected && state.Current == JNestLockState.Phase.Unlocked,
        "fresh lock state must be unlocked");
    Assert(state.BeginLock(), "cannot start lock transition");
    Assert(state.IsProtected, "lock transition must immediately protect content");
    state.LockCompleted();
    Assert(!state.BeginUnlock(credentialVerified: false), "unauthenticated unlock was accepted");
    Assert(state.IsProtected, "wrong password changed lock status");
    Assert(state.BeginUnlock(credentialVerified: true), "authenticated unlock denied");
    Assert(state.IsProtected, "unlock transition exposed content too early");
    state.FailClosed();
    Assert(state.IsProtected && state.Current == JNestLockState.Phase.Locked,
        "failed restore must remain locked");
    Assert(state.BeginUnlock(credentialVerified: true), "retry after failed restore denied");
    state.UnlockCompleted();
    Assert(!state.IsProtected, "successful restore failed to complete unlock");
});

Run("Modal activation only follows an intentional hosted client press", () =>
{
    Assert(WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, true, true, false),
        "a real hosted-client button press should restore the dialog");
    Assert(!WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, false, true, false),
        "clicking another application must never steal foreground");
    Assert(WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, false, true, false, isManagerTarget: true),
        "clicking the disabled WPF owner must also restore the active dialog");
    Assert(!WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, false, false, false, isManagerTarget: true),
        "mouse wheel over disabled modal owner must not force activation");
    Assert(!WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, true, false, false),
        "mouse wheel and mouse-up must not refocus the dialog");
    Assert(!WindowActivityPolicy.ShouldRestoreModalFromHostedClick(false, true, true, false),
        "a closed dialog must not be reactivated");
    Assert(!WindowActivityPolicy.ShouldRestoreModalFromHostedClick(true, true, true, true),
        "a foreground dialog should not repeatedly activate");
});

Run("Password removal requires change mode and two empty fields", () =>
{
    Assert(LockCredentialService.IsRemovalRequest(true, "", ""), "both empty fields must request removal");
    Assert(!LockCredentialService.IsRemovalRequest(false, "", ""), "new password setup cannot remove a credential");
    Assert(!LockCredentialService.IsRemovalRequest(true, "abc123", ""), "a partially filled form cannot remove a credential");
    Assert(!LockCredentialService.IsRemovalRequest(true, "", "abc123"), "a partially filled confirmation cannot remove a credential");
    Assert(!LockCredentialService.IsRemovalRequest(true, "abc123", "abc123"), "a new password must not be treated as removal");
});

Run("Do not disturb handles daytime and overnight boundaries", () =>
{
    var settings = new AppSettings
    {
        DndEnabled = true,
        DndStartMinutes = 22 * 60,
        DndEndMinutes = 8 * 60
    };
    Assert(!settings.IsDoNotDisturbActive(new TimeOnly(21, 59)), "before overnight start");
    Assert(settings.IsDoNotDisturbActive(new TimeOnly(22, 0)), "overnight start");
    Assert(settings.IsDoNotDisturbActive(new TimeOnly(7, 59)), "before overnight end");
    Assert(!settings.IsDoNotDisturbActive(new TimeOnly(8, 0)), "overnight end");

    settings.DndStartMinutes = 9 * 60;
    settings.DndEndMinutes = 17 * 60;
    Assert(!settings.IsDoNotDisturbActive(new TimeOnly(8, 59)), "before daytime start");
    Assert(settings.IsDoNotDisturbActive(new TimeOnly(9, 0)), "daytime start");
    Assert(!settings.IsDoNotDisturbActive(new TimeOnly(17, 0)), "daytime end");

    settings.DndEndMinutes = settings.DndStartMinutes;
    Assert(settings.IsDoNotDisturbActive(new TimeOnly(1, 0)), "equal endpoints mean all day");
    settings.DndEnabled = false;
    Assert(!settings.IsDoNotDisturbActive(new TimeOnly(12, 0)), "disabled schedule");
});

Run("Window rebind requires matching client process identity", () =>
{
    var entry = NewWeChatEntry();
    entry.ProcessId = 321;
    entry.ProcessStartUtcTicks = 1000;
    var candidate = new WindowCandidate(new IntPtr(0x2222), 321, "Weixin", "微信",
        "QWindowIcon", ClientKind.WeChat, ClientWindowRole.Main, 500000, null, 1000);

    Assert(ClientWindowDiscovery.IsSameProcessInstance(entry, candidate), "same process instance");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { ProcessStartUtcTicks = 2000 }),
        "reused PID must not inherit the previous account");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { ProcessStartUtcTicks = null }),
        "unknown process start must not authorize a rebind");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { Kind = ClientKind.WhatsApp }),
        "different client kind must not share an account");
});

Run("Exit completion reconciles bounded timeouts with observed original process", () =>
{
    var requestTimedOut = new ExitProcessResult(701, false, false, "GracefulRequestTimeout");
    var confirmationTimedOut = new ExitProcessResult(702, true, false, "KillTimeout");
    var denied = new ExitProcessResult(703, false, false, "GracefulRequestFailed: UnauthorizedAccessException");
    Assert(!ExitCompletionPolicy.Reconcile(requestTimedOut, false).Confirmed,
        "request timeout cannot claim a running original process exited");
    var requestNowExited = ExitCompletionPolicy.Reconcile(requestTimedOut, true);
    Assert(requestNowExited.Confirmed && requestNowExited.Error is null
        && !requestNowExited.Requested,
        "a verified exited process must supersede a stale request timeout");
    Assert(ExitResultPresentation.Status(requestNowExited) == "已退出",
        "reconciled completion should show the confirmed status");
    Assert(ExitCompletionPolicy.Reconcile(confirmationTimedOut, true).Confirmed,
        "a verified exited process must supersede a stale wait timeout");
    Assert(!ExitCompletionPolicy.Reconcile(denied, false).Confirmed,
        "a request failure without observed exit must remain unconfirmed");
});

Run("Exit batch reports timeout without claiming completion", () =>
{
    var target = new FakeExitTarget(101, () => { }, () => Task.FromResult(false));
    var result = ExitBatchService.RunAsync([target], TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult().Single();
    Assert(result.Requested && !result.Confirmed && result.Error == "KillTimeout",
        "timed-out Kill fallback must remain unconfirmed");
});

Run("Exit batch continues after a partial request failure", () =>
{
    var requested = 0;
    var failed = new FakeExitTarget(201,
        () => throw new InvalidOperationException("denied"),
        () => throw new InvalidOperationException("must not wait"));
    var successful = new FakeExitTarget(202,
        () => requested++,
        () => Task.FromResult(requested == 1));
    var results = ExitBatchService.RunAsync([failed, successful],
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
    Assert(results.Count == 2 && !results[0].Requested && !results[0].Confirmed,
        "failed process must be reported separately");
    Assert(results[1].Requested && results[1].Confirmed,
        "another process must still be requested and confirmed");
});

Run("Exit batch requests every process before awaiting any", () =>
{
    var requests = 0;
    var first = new FakeExitTarget(301, () => requests++,
        () => Task.FromResult(requests == 2));
    var second = new FakeExitTarget(302, () => requests++,
        () => Task.FromResult(requests == 2));
    var results = ExitBatchService.RunAsync([first, second],
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
    Assert(results.All(result => result.Confirmed),
        "all termination requests must precede confirmation waits");
});

Run("Exit request never blocks the caller thread", () =>
{
    using var gate = new ManualResetEventSlim(false);
    var target = new FakeExitTarget(401,
        () => gate.Wait(TimeSpan.FromSeconds(2)),
        () => Task.FromResult(true));
    var timer = Stopwatch.StartNew();
    var task = ExitBatchService.RunAsync([target], TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    var elapsed = timer.Elapsed;
    gate.Set();
    Assert(elapsed < TimeSpan.FromSeconds(1), "Kill request blocked the UI caller");
    Assert(task.GetAwaiter().GetResult().Single().Confirmed, "worker request did not complete");
});

Run("Managed exit target includes manually popped-out clients", () =>
{
    Assert(ExitTargetPolicy.IsManaged(true, false), "attached client omitted");
    Assert(ExitTargetPolicy.IsManaged(false, true), "manually popped-out client omitted");
    Assert(!ExitTargetPolicy.IsManaged(false, false), "unmanaged client included");
});

Run("Exit results distinguish failure and unconfirmed state", () =>
{
    Assert(ExitResultPresentation.Status(new ExitProcessResult(1, true, true, null)) == "已退出",
        "confirmed process label is wrong");
    Assert(ExitResultPresentation.Status(new ExitProcessResult(2, false, false, "GracefulRequestFailed: UnauthorizedAccessException")) == "正常退出请求失败",
        "graceful request failure label is wrong");
    Assert(ExitResultPresentation.Status(new ExitProcessResult(3, true, false, "KillTimeout")) == "强制退出未确认",
        "Kill timeout label is wrong");
    Assert(ExitResultPresentation.Status(new ExitProcessResult(4, true, false, "KillConfirmationFailed: InvalidOperationException")) == "强制退出确认失败",
        "Kill confirmation failure label is wrong");
    Assert(ExitResultPresentation.Status(new ExitProcessResult(5, false, false, "GracefulRequestTimeout")).StartsWith("正常退出请求超时"),
        "graceful request timeout must not be described as a definite failure");
});

Run("Blocked OS request returns an uncertain result by deadline while other requests proceed", () =>
{
    using var entered = new ManualResetEventSlim(false);
    using var release = new ManualResetEventSlim(false);
    using var otherRequested = new ManualResetEventSlim(false);
    var first = new FakeExitTarget(701, () =>
    {
        entered.Set();
        release.Wait(TimeSpan.FromSeconds(5));
    }, () => throw new InvalidOperationException("must not wait on a pending request"));
    var second = new FakeExitTarget(702, () => otherRequested.Set(), () => Task.FromResult(true));
    var timer = Stopwatch.StartNew();
    var task = ExitBatchService.RunAsync([first, second], TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
    try
    {
        Assert(entered.Wait(TimeSpan.FromSeconds(2)), "blocked request did not start");
        Assert(otherRequested.Wait(TimeSpan.FromSeconds(2)), "other request did not start");
        var results = task.GetAwaiter().GetResult();
        Assert(timer.Elapsed < TimeSpan.FromSeconds(3), "batch waited indefinitely for a native request");
        Assert(results.Count == 2 && !results[0].Requested && !results[0].Confirmed
            && results[0].Error == "GracefulRequestTimeout", "pending request must be uncertain");
        Assert(results[1].Requested && results[1].Confirmed,
            "other process must complete despite a blocked request");
    }
    finally
    {
        release.Set();
    }
});

Run("Exit batch deduplicates a shared process", () =>
{
    var requests = 0;
    var first = new FakeExitTarget(501, () => requests++, () => Task.FromResult(true));
    var duplicate = new FakeExitTarget(501, () => requests++, () => Task.FromResult(true));
    var results = ExitBatchService.RunAsync([first, duplicate],
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
    Assert(requests == 1 && results.Count == 1 && results[0].Confirmed,
        "shared PID must receive one termination request");
});

Run("Exit wait failure remains unconfirmed", () =>
{
    var target = new FakeExitTarget(601, () => { },
        () => throw new InvalidOperationException("wait failed"));
    var result = ExitBatchService.RunAsync([target], TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1)).GetAwaiter().GetResult().Single();
    Assert(result.Requested && !result.Confirmed
        && result.Error!.StartsWith("KillConfirmationFailed: InvalidOperationException", StringComparison.Ordinal),
        "wait failure after Kill must not claim exit");
});

Run("Confirmed all-exit Close enters cleanup without another prompt", () =>
{
    Assert(ExitClosePolicy.ShouldPromptForChoice(false, false, true),
        "initial close must ask for a choice");
    Assert(ExitClosePolicy.ShouldBeginExitAll(false, true),
        "all-exit choice must start the batch");
    Assert(!ExitClosePolicy.ShouldPromptForChoice(true, true, true)
        && !ExitClosePolicy.ShouldBeginExitAll(true, false),
        "Close after confirmation must proceed directly to cleanup");
    Assert(ExitClosePolicy.ShouldPromptForChoice(false, false, true),
        "failed exit must allow a new choice");
});

Run("Exit dialogs fit the owner monitor at multiple DPI scales", () =>
{
    foreach (var scale in new[] { 1.0, 1.5, 2.0, 2.5 })
    {
        var size = ExitDialogLayout.Calculate(800, 600, scale, scale);
        Assert(size.Width <= 800 / scale - 32 && size.Height <= 600 / scale - 32,
            "dialog exceeds monitor work area");
        Assert(ExitDialogLayout.ButtonWidth(size.Width) * 2 + ExitDialogLayout.ButtonGap(size.Width) + 36 <= size.Width,
            "two exit buttons exceed dialog width");
    }
    Assert(ExitDialogLayout.ButtonWidth(420) == 104 && ExitDialogLayout.ButtonGap(420) == 32,
        "standard dialog must use 104 DIP buttons and 32 DIP spacing");
    var narrow = ExitDialogLayout.Calculate(400, 700, 2, 2);
    Assert(narrow.Width <= 168 && ExitDialogLayout.StackButtons(narrow.Width)
        && ExitDialogLayout.ButtonWidth(narrow.Width) <= narrow.Width - 36,
        "narrow monitor must stack buttons within dialog width");
});

Run("RM identifies owner for two live WeChat accounts", () =>
{
    var first = WeChatDatabaseOwnershipService.Decide(["A"], 1, 0, 0);
    var second = WeChatDatabaseOwnershipService.Decide(["B"], 1, 0, 0);
    Assert(first.Status == WeChatDatabaseOwnershipService.Verdict.Unique && first.AccountId == "A", "A was not resolved");
    Assert(second.Status == WeChatDatabaseOwnershipService.Verdict.Unique && second.AccountId == "B", "B was not resolved");
});

Run("RM ignores confirmed unowned historical accounts", () =>
{
    var result = WeChatDatabaseOwnershipService.Decide(["A"], 1, 3, 0);
    Assert(result.Status == WeChatDatabaseOwnershipService.Verdict.Unique && result.AccountId == "A",
        "Unowned historical account should not block sole live owner");
});

Run("RM refuses positive owner with indeterminate peer", () =>
{
    var result = WeChatDatabaseOwnershipService.Decide(["A"], 0, 0, 1);
    Assert(result.Status == WeChatDatabaseOwnershipService.Verdict.Unknown && result.AccountId is null,
        "API failure must not be mistaken for an unowned account");
});

Run("RM refuses two positive account owners", () =>
{
    var result = WeChatDatabaseOwnershipService.Decide(["A", "B"], 0, 0, 0);
    Assert(result.Status == WeChatDatabaseOwnershipService.Verdict.Conflict,
        "two positive target owners must conflict");
});

Run("RM refuses a case with no positive ownership", () =>
{
    var result = WeChatDatabaseOwnershipService.Decide([], 2, 1, 0);
    Assert(result.Status == WeChatDatabaseOwnershipService.Verdict.Unknown && result.AccountId is null,
        "no owner must never produce an identity");
});

Run("RM invalid process identity rejects before probing", () =>
{
    var result = WeChatDatabaseOwnershipService.Resolve(0, null, ["A"]);
    Assert(result.Status == WeChatDatabaseOwnershipService.Verdict.Unknown,
        "invalid process identity must not be resolved");
});


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

        WeChatProfileService.CleanupCurrentProcessArtifacts();
        Assert(!File.Exists(liveOwner), "normal shutdown must remove this process's remaining plaintext copy");
        Assert(File.Exists(freshOrphan), "shutdown cleanup must not remove another process's copy");
    }
    finally
    {
        WeChatProfileService.ScratchDirectoryOverride = null;
        WeChatProfileService.LegacyScratchDirectoryOverride = null;
        try { Directory.Delete(root, recursive: true); } catch { }
    }
});

Run("Activation pipe uses bounded input and current-user named pipe", () =>
{
    Assert(ActivationMessagePolicy.IsSafe("activate"), "normal activation must be allowed");
    Assert(ActivationMessagePolicy.IsSafe("account=wxid_123&pid=42"), "toast account args must remain compatible");
    Assert(!ActivationMessagePolicy.IsSafe(new string('a', ActivationMessagePolicy.MaximumCharacters + 1)),
        "oversized activation must be rejected");
    Assert(!ActivationMessagePolicy.IsSafe("activate\\nother".Replace("\\n", "\n")),
        "embedded newline must be rejected");

    string? Read(string line)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line));
        return ActivationMessagePolicy.ReadBoundedLineAsync(stream, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }
    Assert(Read("activate\\r\\n".Replace("\\r", "\r").Replace("\\n", "\n")) == "activate",
        "CRLF-delimited activation frame should decode");
    Assert(Read("partial") is null, "truncated frame cannot activate JNest");
    Assert(Read(new string('x', ActivationMessagePolicy.MaximumCharacters + 1) + "\n") is null,
        "overlength frame cannot allocate an unbounded activation argument");
    Assert(Read("activate\rBAD\n") is null, "CR injection must be rejected");

    // CI validates framing and that the Windows-only current-user pipe mode
    // is available. The complete second-process activation roundtrip is left
    // to the Windows real-device checklist, not an interactive CI pipe wait.
    var pipeName = "jnest-acl-probe-" + Guid.NewGuid().ToString("N");
    using var server = new System.IO.Pipes.NamedPipeServerStream(
        pipeName, System.IO.Pipes.PipeDirection.In, 1,
        System.IO.Pipes.PipeTransmissionMode.Byte,
        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
    Assert(server.CanRead && !server.CanWrite, "current-user activation pipe must be read-only");
});

Run("Plaintext scratch ACL is private and cleanup refuses unrelated files", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "jnest-acl-probe-" + Guid.NewGuid().ToString("N"));
    var scratch = Path.Combine(root, "scratch");
    Directory.CreateDirectory(root);
    WeChatProfileService.ScratchDirectoryOverride = scratch;
    try
    {
        Assert(WeChatProfileService.EnsureScratchDirectoryForTest() == scratch,
            "scratch ACL setup refused a valid private directory");
        var acl = new DirectoryInfo(scratch).GetAccessControl(
            System.Security.AccessControl.AccessControlSections.Access);
        Assert(acl.AreAccessRulesProtected, "scratch ACL still inherits broader access rules");
        Assert(!WeChatProfileService.IsScratchCleanupCandidate("contact-passwords.txt"),
            "unrelated files must not be eligible for cleanup");
        var unrelated = Path.Combine(scratch, "contact-passwords.txt");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddHours(-1));
        WeChatProfileService.CleanupTemporaryArtifacts(TimeSpan.FromMinutes(1));
        Assert(File.Exists(unrelated), "cleanup deleted an unrelated file");
    }
    finally
    {
        WeChatProfileService.ScratchDirectoryOverride = null;
        try { Directory.Delete(root, recursive: true); } catch { }
    }
});

Run("Window discovery metadata caches discard stale and excess processes", () =>
{
    ClientWindowDiscovery.SeedExpiredMetadataForTest(400);
    var sizes = ClientWindowDiscovery.MetadataCacheSizesForTest();
    Assert(sizes.Names <= 256 && sizes.Versions <= 128,
        "metadata caches must remain bounded under short-lived PID churn");
});

Run("Diagnostic error throttling preserves first and distinct errors", () =>
{
    var gate = new RepeatedErrorGate(TimeSpan.FromSeconds(30), maximumEntries: 2);
    var now = DateTime.UtcNow;
    Assert(gate.ShouldLog("capture|timeout", now), "first error was unexpectedly discarded");
    Assert(!gate.ShouldLog("capture|timeout", now.AddSeconds(1)), "duplicate error was not throttled");
    Assert(gate.ShouldLog("capture|access-denied", now.AddSeconds(1)),
        "distinct error must not be hidden");
    Assert(gate.ShouldLog("capture|timeout", now.AddSeconds(31)), "error did not recover after cooldown");
    Assert(gate.ShouldLog("pipe|timeout", now.AddSeconds(32)), "third distinct error was suppressed");
    Assert(gate.ShouldLog("capture|access-denied", now.AddSeconds(33)),
        "evicted old fingerprint must be eligible again");
});

Run("Local WeChat profile read preference preserves upgraded behavior but supports opt-out", () =>
{
    var settings = new AppSettings();
    Assert(settings.EnableLocalProfileRead,
        "existing installations must preserve automatic account identity on upgrade");
    settings.EnableLocalProfileRead = false;
    Assert(!settings.EnableLocalProfileRead,
        "user opt-out must disable future automatic local profile resolution");
});

Run("Profile cache never restores a different account after identity reuse", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "jnest-profile-cache-probe-" + Guid.NewGuid().ToString("N"));
    WeChatProfileCacheService.CacheRootOverride = root;
    try
    {
        var cache = new WeChatProfileCacheService();
        cache.Store("same-slot", new WeChatLocalProfile("wxid_first", "First", null));
        Assert(!cache.TryGet("same-slot", "wxid_second", out _),
            "cached profile must not cross an account boundary");
        var switched = cache.Store("same-slot", new WeChatLocalProfile("wxid_second", "", null));
        Assert(switched.AccountId == "wxid_second" && switched.Nickname == "wxid_second",
            "missing nickname must fall back to the verified account ID, not the old nickname");
        var reloaded = new WeChatProfileCacheService();
        Assert(!reloaded.TryGet("same-slot", "wxid_first", out _),
            "a restarted process must not restore the previous account");
        Assert(reloaded.TryGet("same-slot", "wxid_second", out var current)
               && current.Nickname == "wxid_second", "current account fallback did not persist");
    }
    finally
    {
        WeChatProfileCacheService.CacheRootOverride = null;
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

sealed class FakeExitTarget(int processId, Action request, Func<Task<bool>> wait) : IExitProcessTarget
{
    public int ProcessId => processId;
    public bool RequestGracefulExit()
    {
        request();
        return true;
    }
    public Task<bool> WaitForExitAsync(TimeSpan timeout) => wait();
    public void ForceKill() { }
}

