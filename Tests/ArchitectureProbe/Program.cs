using JuChuang.Models;
using JuChuang.Services;

var failures = new List<string>();

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

    Assert(ClientWindowDiscovery.IsSameProcessInstance(entry, candidate),
        "same PID and process start should allow a rebind");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { ProcessStartUtcTicks = 2000 }),
        "reused PID must not inherit the previous account");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { ProcessStartUtcTicks = null }),
        "unknown process start must not authorize a rebind");
    Assert(!ClientWindowDiscovery.IsSameProcessInstance(entry, candidate with { Kind = ClientKind.WhatsApp }),
        "different client kind must not share an account");
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

Run("Account profile status is independent from HWND", () =>
{
    var entry = NewWeChatEntry();
    entry.Status = "已嵌入";
    entry.AccountId = "wxid_arch_probe";
    entry.ProfileState = AccountProfileState.Ready;
    entry.Handle = new IntPtr(0x2222);

    Assert(entry.StatusSummary == "已嵌入",
        "fully ready profile state should stay hidden in the compact status summary");

    entry.ProfileState = AccountProfileState.ReadyWithoutAvatar;
    Assert(entry.StatusSummary.Contains("头像待更新", StringComparison.Ordinal),
        "incomplete profile state should remain visible in the status summary");

    entry.ProfileState = AccountProfileState.Ready;
    var previousAccountId = entry.AccountId;
    entry.Handle = new IntPtr(0x3333);
    entry.SessionState = ClientSessionState.Rebinding;

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

