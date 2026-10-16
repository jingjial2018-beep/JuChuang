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

