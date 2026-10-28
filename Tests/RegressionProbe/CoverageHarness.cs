using System.IO;
using JuChuang.Services;

// 纯逻辑与文件策略的补充回归覆盖（对应 JNest v1.0.10 / 5d015bb）：
// - AppSettings.IsDoNotDisturbActive 的时间窗策略（此前只有 UI 提示被测，策略本身未测）
// - WeChatDatabaseOwnershipService.Decide 的证据判定矩阵（此前未测）
// - ExitClosePolicy 的真值表（此前未测）
// - AppSettings 的保存往返、损坏隔离 + .bak 回退、三方合并（此前只测了写失败）
// - DiagnosticLogService 的轮转/保留期/总量上限/详细追踪门控（v1.0.10 新代码，此前未测）
// 断言风格与 Program.cs 一致：失败抛 InvalidOperationException，由 Run() 计数。
// 日志轮转一组需要先给 DiagnosticLogService 加 DirectoryPathOverride 测试钩子
// （与 LockCredentialService / AppSettings 的 Override 先例同款）。
internal static class CoverageHarness
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    // ---------- 免打扰时间窗策略 ----------

    public static Task DndPolicy()
    {
        static AppSettings Make(bool enabled, int start, int end) => new()
        {
            DndEnabled = enabled,
            DndStartMinutes = start,
            DndEndMinutes = end,
        };
        static bool At(AppSettings s, int h, int m) => s.IsDoNotDisturbActive(new TimeOnly(h, m));

        // 未开启：任何时刻都不生效。
        var off = Make(false, 22 * 60, 8 * 60);
        Assert(!At(off, 23, 0) && !At(off, 3, 0) && !At(off, 12, 0), "DND active while disabled");

        // 跨夜 22:00–08:00：边界逐分钟核对（左闭右开）。
        var night = Make(true, 22 * 60, 8 * 60);
        Assert(!At(night, 21, 59), "cross-night: 21:59 should be inactive");
        Assert(At(night, 22, 0), "cross-night: 22:00 should be active");
        Assert(At(night, 23, 59), "cross-night: 23:59 should be active");
        Assert(At(night, 0, 0), "cross-night: 00:00 should be active");
        Assert(At(night, 7, 59), "cross-night: 07:59 should be active");
        Assert(!At(night, 8, 0), "cross-night: 08:00 should be inactive");
        Assert(!At(night, 12, 0), "cross-night: 12:00 should be inactive");

        // 同日 09:00–17:00。
        var day = Make(true, 9 * 60, 17 * 60);
        Assert(!At(day, 8, 59), "same-day: 08:59 should be inactive");
        Assert(At(day, 9, 0), "same-day: 09:00 should be active");
        Assert(At(day, 16, 59), "same-day: 16:59 should be active");
        Assert(!At(day, 17, 0), "same-day: 17:00 should be inactive");
        Assert(!At(day, 23, 0), "same-day: 23:00 should be inactive");

        // 开始 = 结束：现行语义为全天免打扰（与设置页提示一致）。
        var allDay = Make(true, 22 * 60, 22 * 60);
        Assert(At(allDay, 0, 0) && At(allDay, 12, 0) && At(allDay, 22, 0), "equal endpoints should mean all day");
        var midnight = Make(true, 0, 0);
        Assert(At(midnight, 0, 0) && At(midnight, 23, 59), "00:00-00:00 should mean all day");

        // 越界值被钳制到 [0,1439]：start=-30 视为 0，end=2000 视为 1439。
        var clamped = Make(true, -30, 2000);
        Assert(At(clamped, 0, 0) && At(clamped, 23, 58), "clamped window should cover the whole day");
        Assert(!At(clamped, 23, 59), "clamped end 1439 leaves 23:59 outside (left-closed, right-open)");

        return Task.CompletedTask;
    }

    // ---------- 微信账号归属判定 ----------

    public static Task OwnershipDecide()
    {
        var empty = WeChatDatabaseOwnershipService.Decide([], 0, 0, 0);
        Assert(empty.Status == WeChatDatabaseOwnershipService.Verdict.Unknown && empty.AccountId is null,
            "no evidence should be Unknown");

        var unique = WeChatDatabaseOwnershipService.Decide(["wxid_a"], 0, 0, 0);
        Assert(unique.Status == WeChatDatabaseOwnershipService.Verdict.Unique && unique.AccountId == "wxid_a",
            "single owner should be Unique");
        Assert(unique.Detail.StartsWith("UniqueOwner;", StringComparison.Ordinal), "unique detail prefix changed");

        var conflict = WeChatDatabaseOwnershipService.Decide(["wxid_a", "wxid_b"], 0, 0, 0);
        Assert(conflict.Status == WeChatDatabaseOwnershipService.Verdict.Conflict && conflict.AccountId is null,
            "two owners should be Conflict");
        Assert(conflict.Detail.StartsWith("MultipleTargetOwners;", StringComparison.Ordinal), "conflict detail prefix changed");

        // 已确认的 Unowned / OwnedByOther 不阻挡唯一归属（注释口径）。
        var withOthers = WeChatDatabaseOwnershipService.Decide(["wxid_a"], otherOwned: 2, unowned: 3, indeterminate: 0);
        Assert(withOthers.Status == WeChatDatabaseOwnershipService.Verdict.Unique,
            "confirmed unowned/other-owned evidence must not block a unique owner");

        // 只要有一次探测不确定，唯一归属就不成立（缺失/失败探测阻挡）。
        var blocked = WeChatDatabaseOwnershipService.Decide(["wxid_a"], 0, 0, indeterminate: 1);
        Assert(blocked.Status == WeChatDatabaseOwnershipService.Verdict.Unknown && blocked.AccountId is null,
            "indeterminate evidence must block a unique verdict");

        var noOwner = WeChatDatabaseOwnershipService.Decide([], otherOwned: 1, unowned: 1, indeterminate: 0);
        Assert(noOwner.Status == WeChatDatabaseOwnershipService.Verdict.Unknown, "no owner at all should be Unknown");

        // 诊断明细格式是日志契约，逐字锁定。
        var detailed = WeChatDatabaseOwnershipService.Decide(["wxid_a"], 1, 2, 0,
            missing: 3, samePid: 4, startMismatch: 5, otherPid: 6, maxDeltaMs: 77);
        Assert(detailed.Detail ==
            "UniqueOwner;Owners=1;OtherOwned=1;Unowned=2;Indeterminate=0;Missing=3;SamePid=4;TimeMismatch=5;OtherPid=6;MaxDeltaMs=77",
            "detail format changed: " + detailed.Detail);

        // Resolve 的身份守卫在触碰文件系统之前返回 Unknown。
        var guardPid = WeChatDatabaseOwnershipService.Resolve(0, null, []);
        Assert(guardPid.Status == WeChatDatabaseOwnershipService.Verdict.Unknown, "Resolve guard (pid) failed");
        var guardCandidates = WeChatDatabaseOwnershipService.Resolve(5, 123, []);
        Assert(guardCandidates.Status == WeChatDatabaseOwnershipService.Verdict.Unknown, "Resolve guard (candidates) failed");

        return Task.CompletedTask;
    }

    // ---------- 退出关闭策略真值表 ----------

    public static Task ExitPolicyTables()
    {
        // ShouldPromptForChoice = !allCompleted && !choiceApproved && initialized
        (bool done, bool approved, bool ready, bool expected)[] promptCases =
        [
            (false, false, false, false),
            (false, false, true, true),
            (false, true, false, false),
            (false, true, true, false),
            (true, false, false, false),
            (true, false, true, false),
            (true, true, false, false),
            (true, true, true, false),
        ];
        foreach (var c in promptCases)
            Assert(ExitClosePolicy.ShouldPromptForChoice(c.done, c.approved, c.ready) == c.expected,
                $"ShouldPromptForChoice({c.done},{c.approved},{c.ready}) should be {c.expected}");

        // ShouldBeginExitAll = !allCompleted && exitAllSelected
        (bool done, bool selected, bool expected)[] beginCases =
        [
            (false, false, false),
            (false, true, true),
            (true, false, false),
            (true, true, false),
        ];
        foreach (var c in beginCases)
            Assert(ExitClosePolicy.ShouldBeginExitAll(c.done, c.selected) == c.expected,
                $"ShouldBeginExitAll({c.done},{c.selected}) should be {c.expected}");

        return Task.CompletedTask;
    }

    // ---------- 设置持久化：往返 / 备份回退 / 三方合并 ----------

    public static Task SettingsPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "jnest-settings-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousOverride = AppSettings.SettingsPathOverride;
        try
        {
            // 1) 保存 → 重新加载往返，含 DND、字典与列表字段。
            var roundtripPath = Path.Combine(root, "roundtrip", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(roundtripPath)!);
            AppSettings.SettingsPathOverride = roundtripPath;
            var written = new AppSettings
            {
                DndEnabled = true,
                DndStartMinutes = 23 * 60 + 30,
                DndEndMinutes = 6 * 60 + 30,
                EnableToastNotifications = false,
                SidebarWidth = 321.5,
            };
            written.AccountNamesByIdentity["wxid_a"] = "甲账号";
            written.AccountOrder.Add("wxid_b");
            written.AccountOrder.Add("wxid_a");
            Assert(written.Save(), "round-trip save failed");
            var loaded = AppSettings.Load();
            Assert(loaded.DndEnabled && loaded.DndStartMinutes == 1410 && loaded.DndEndMinutes == 390,
                "round-trip DND values changed");
            Assert(!loaded.EnableToastNotifications && loaded.SidebarWidth == 321.5,
                "round-trip scalar values changed");
            Assert(loaded.AccountNamesByIdentity.TryGetValue("WXID_A", out var name) && name == "甲账号",
                "round-trip account names changed (dictionary must stay case-insensitive)");
            Assert(loaded.AccountOrder.SequenceEqual(["wxid_b", "wxid_a"]), "round-trip account order changed");

            // 2) 主文件损坏：隔离主文件并回退到 .bak 的上一版，而不是默认值。
            var fallbackPath = Path.Combine(root, "fallback", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
            AppSettings.SettingsPathOverride = fallbackPath;
            var first = AppSettings.Load();
            first.DndStartMinutes = 111;
            Assert(first.Save(), "fallback setup save 1 failed");
            var second = AppSettings.Load();
            second.DndStartMinutes = 222;
            Assert(second.Save(), "fallback setup save 2 failed"); // 此后 .bak 应为 111 那一版
            File.WriteAllText(fallbackPath, "{ this is not json");
            var recovered = AppSettings.Load();
            Assert(recovered.DndStartMinutes == 111,
                $"corrupt main file should fall back to .bak (got {recovered.DndStartMinutes})");
            Assert(Directory.EnumerateFiles(Path.GetDirectoryName(fallbackPath)!, "settings.json.corrupt-*").Any(),
                "corrupt main file was not quarantined");

            // 3) 三方合并：两个实例改不同属性，互不覆盖；同改一项时以当前实例为准并留冲突副本 + 通知。
            var mergePath = Path.Combine(root, "merge", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mergePath)!);
            AppSettings.SettingsPathOverride = mergePath;
            var seed = AppSettings.Load();
            seed.DndStartMinutes = 100;
            Assert(seed.Save(), "merge seed save failed");

            var victim = AppSettings.Load();
            var other = AppSettings.Load();
            other.EnableNotificationSound = false;
            Assert(other.Save(), "merge other save failed");
            victim.EnableTaskbarFlash = false;
            Assert(victim.Save(), "merge victim save failed");
            var merged = AppSettings.Load();
            Assert(!merged.EnableNotificationSound && !merged.EnableTaskbarFlash && merged.DndStartMinutes == 100,
                "disjoint changes were not merged");

            var notices = new List<string>();
            Action<string> handler = message => notices.Add(message);
            AppSettings.SaveProblem += handler;
            try
            {
                var victim2 = AppSettings.Load();
                var other2 = AppSettings.Load();
                other2.DndEndMinutes = 700;
                Assert(other2.Save(), "conflict other save failed");
                victim2.DndEndMinutes = 800;
                Assert(victim2.Save(), "conflict victim save failed");
            }
            finally { AppSettings.SaveProblem -= handler; }
            var afterConflict = AppSettings.Load();
            Assert(afterConflict.DndEndMinutes == 800, "conflict should resolve to the current instance value");
            Assert(Directory.EnumerateFiles(Path.GetDirectoryName(mergePath)!, "settings.json.conflict-*").Any(),
                "conflict copy was not preserved");
            Assert(notices.Any(n => n.Contains("冲突")), "conflict notice was not raised");
        }
        finally
        {
            AppSettings.SettingsPathOverride = previousOverride;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ---------- 诊断日志：轮转 / 保留期 / 总量上限 / 追踪门控 ----------

    public static Task LogRotationAndBounds()
    {
        var root = Path.Combine(Path.GetTempPath(), "jnest-log-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DiagnosticLogService.DirectoryPathOverride = root;
        try
        {
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            var basePath = Path.Combine(root, $"diagnostics-{today}.log");
            var firstRollPath = Path.Combine(root, $"diagnostics-{today}-001.log");
            var secondRollPath = Path.Combine(root, $"diagnostics-{today}-002.log");
            const long fileCap = 10L * 1024 * 1024;
            const long totalCap = 50L * 1024 * 1024;

            static void SeedSized(string path, long bytes, DateTime lastWriteUtc)
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.SetLength(bytes);
                File.SetLastWriteTimeUtc(path, lastWriteUtc);
            }

            // 1) 详细追踪默认门控：Debug 事件不落盘，Activity 事件落盘。
            DiagnosticLogService.WriteDebugEvent("coverage-debug-marker");
            DiagnosticLogService.WriteActivityEvent("coverage-activity-marker");
            Assert(File.Exists(basePath), "activity event did not create the daily log");
            var dayOne = File.ReadAllText(basePath);
            Assert(dayOne.Contains("coverage-activity-marker"), "activity marker missing from log");
            Assert(!dayOne.Contains("coverage-debug-marker"), "debug event written while detail tracing is disabled");

            // 2) 单文件上限轮转 + 保留期清理（由轮转触发，不受 10 分钟节流影响）。
            var oldPath = Path.Combine(root, "diagnostics-20000101.log");
            SeedSized(oldPath, 128, DateTime.UtcNow.AddDays(-8));
            using (var stream = new FileStream(basePath, FileMode.Open, FileAccess.Write))
                stream.SetLength(fileCap - 40); // 再写一行必超上限
            DiagnosticLogService.WriteActivityEvent("coverage-rotation-marker");
            Assert(File.Exists(firstRollPath), "log did not rotate to -001 at the file cap");
            Assert(File.ReadAllText(firstRollPath).Contains("coverage-rotation-marker"),
                "rotated file does not contain the new entry");
            Assert(new FileInfo(basePath).Length == fileCap - 40, "full base file was modified during rotation");
            Assert(!File.Exists(oldPath), "file older than retention was not cleaned up on rotation");

            // 3) 总量上限：按最旧优先淘汰到上限以内，当前活动文件永不删。
            var filler1 = Path.Combine(root, "diagnostics-20990101.log");
            var filler2 = Path.Combine(root, "diagnostics-20990102.log");
            var filler3 = Path.Combine(root, "diagnostics-20990103.log");
            SeedSized(filler1, 20L * 1024 * 1024, DateTime.UtcNow.AddHours(-3));
            SeedSized(filler2, 20L * 1024 * 1024, DateTime.UtcNow.AddHours(-2));
            SeedSized(filler3, 20L * 1024 * 1024, DateTime.UtcNow.AddHours(-1));
            using (var stream = new FileStream(firstRollPath, FileMode.Open, FileAccess.Write))
                stream.SetLength(fileCap - 40);
            DiagnosticLogService.WriteActivityEvent("coverage-cap-marker");
            Assert(File.Exists(secondRollPath) && File.ReadAllText(secondRollPath).Contains("coverage-cap-marker"),
                "second rotation did not produce -002 with the new entry");
            Assert(!File.Exists(filler1) && !File.Exists(filler2), "oldest files were not evicted over the total cap");
            Assert(File.Exists(filler3), "newer file was evicted before the cap required it");
            long total = Directory.EnumerateFiles(root, "diagnostics-*.log").Sum(p => new FileInfo(p).Length);
            Assert(total <= totalCap + 4096, $"total log size {total} still over the cap");

            // 4) 异常明细有界：超长消息落盘后整行仍被截断在上限内。
            DiagnosticLogService.WriteErrorEvent("coverage-error", new InvalidOperationException(new string('x', 5000)));
            var line = File.ReadLines(secondRollPath).FirstOrDefault(l => l.Contains("coverage-error"));
            Assert(line is not null, "error event missing from active log");
            Assert(line!.Length < 2000, $"error line not bounded (length {line.Length})");

            DiagnosticLogService.WriteErrorEvent("coverage-privacy",
                new InvalidOperationException(@"C:\Users\ExampleUser\Documents\wxid_secret\contact.db"));
            var privacyLine = File.ReadLines(secondRollPath).FirstOrDefault(l => l.Contains("coverage-privacy"));
            Assert(privacyLine is not null && !privacyLine.Contains("ExampleUser")
                   && !privacyLine.Contains("wxid_secret")
                   && privacyLine.Contains("%USERPROFILE%"),
                "error detail exposed a Windows user or account identifier");
        }
        finally
        {
            DiagnosticLogService.DirectoryPathOverride = null;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }
}
