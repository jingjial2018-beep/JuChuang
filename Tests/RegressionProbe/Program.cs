using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using JuChuang;
using JuChuang.Controls;
using JuChuang.Models;
using JuChuang.Services;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "jnest-regression-" + Guid.NewGuid().ToString("N"));
    private static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--fixture")) { RunFixture(); return 0; }
        if (args.Contains("--session-probe"))
            return !LockCredentialService.IsConfigured && !JNestLockPersistence.IsLocked
                && !LockCredentialService.Verify("abcdef") ? 0 : 1;
        Directory.CreateDirectory(Root);
        LockCredentialService.DirectoryPathOverride = Path.Combine(Root, "credentials");
        JNestLockPersistence.DirectoryPathOverride = Path.Combine(Root, "lock");
        AppSettings.SettingsPathOverride = Path.Combine(Root, "settings.json");
        AppSettings.ProblemNoticeInterval = TimeSpan.Zero;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Run("Session credential and legacy file cleanup", Credentials);
                await Run("Unlock gate and relock preserve the newer session lock", RelockAndGate);
                await Run("Old restore failure cannot replace a newer lock", StaleRestoreFailure);
                await Run("Settings write failure reports failure and later recovers", SettingsFailure);
                await Run("Parked window detaches and reattaches without losing its desktop state", ParkedDetachAndReattach);
                await Run("Owned primary discovery validates owner, HWND, PID and start", DiscoveryAndAttach);
                await Run("Toast and second launch use complete tray restore", TrayRestore);
                await Run("Settings dialog shows save failure and retry result", SettingsDialogFailure);
                await Run("DND equal endpoints show all-day warning without changing policy", DndAllDayHint);
                await Run("Status clock has no separators and retains seconds", StatusClock);
                await Run("Password dialog collapses empty error space", PasswordDialogSpacing);
                await Run("Detached account removal leaves its process running", DetachedAccountRemoval);
                await Run("Exit-all queues JNest close after the original Closing event", ExitAllDeferredClose);
                await Run("Main-window save failures never report success and settings retry persists pending changes", MainWindowSettingsFailure);
                await Run("Guarded runner isolates asynchronous event failures", CoverageHarness.GuardedRunnerIsolation);
                await Run("DND policy time-window matrix", CoverageHarness.DndPolicy);
                await Run("WeChat database ownership decision matrix", CoverageHarness.OwnershipDecide);
                await Run("Exit close policy truth tables", CoverageHarness.ExitPolicyTables);
                await Run("Graceful client exit precedes Kill fallback and preserves process identity", CoverageHarness.ExitBatchLifecycle);
                await Run("Settings persistence roundtrip, backup and merge", CoverageHarness.SettingsPersistence);
                await Run("Diagnostic log rotation, retention and bounds", CoverageHarness.LogRotationAndBounds);
            }
            finally
            {
                LockCredentialService.DirectoryPathOverride = null;
                JNestLockPersistence.DirectoryPathOverride = null;
                AppSettings.SettingsPathOverride = null;
                AppSettings.ProblemNoticeInterval = TimeSpan.FromSeconds(60);
                // Root is a unique probe-owned temporary directory.
                try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return _failures == 0 ? 0 : 1;
    }

    private static Task Credentials()
    {
        LockCredentialService.SetPassword("abcdef");
        Assert(!Directory.Exists(LockCredentialService.DirectoryPathOverride!), "credential wrote to disk");
        Assert(LockCredentialService.Verify("abcdef"), "valid credential rejected");
        Assert(!LockCredentialService.Verify("wrong"), "wrong password accepted");
        Assert(LockCredentialService.IsRemovalRequest(true, "", ""), "removal request rejected");
        Assert(!LockCredentialService.IsRemovalRequest(false, "", ""), "setup treated as removal");
        AssertThrows<InvalidOperationException>(() => LockCredentialService.SetPassword("another"));
        AssertThrows<UnauthorizedAccessException>(() => LockCredentialService.ChangePassword("wrong", "ghijkl"));
        LockCredentialService.ChangePassword("abcdef", "ghijkl");
        Assert(!LockCredentialService.Verify("abcdef") && LockCredentialService.Verify("ghijkl"), "password change failed");
        JNestLockPersistence.SaveLocked();
        AssertThrows<InvalidOperationException>(() => LockCredentialService.RemovePassword("ghijkl"));
        JNestLockPersistence.SaveUnlocked();
        AssertThrows<UnauthorizedAccessException>(() => LockCredentialService.RemovePassword("wrong"));
        LockCredentialService.RemovePassword("ghijkl");
        Assert(!LockCredentialService.IsConfigured && !LockCredentialService.Verify("ghijkl"), "removal failed");
        LockCredentialService.SetPassword("abcdef");

        var credentialDirectory = LockCredentialService.DirectoryPathOverride!;
        var markerDirectory = JNestLockPersistence.DirectoryPathOverride!;
        Directory.CreateDirectory(credentialDirectory);
        Directory.CreateDirectory(markerDirectory);
        var credentialPath = Path.Combine(credentialDirectory, "lock-credential.json");
        var markerPath = Path.Combine(markerDirectory, "lock-active.marker");
        File.WriteAllText(credentialPath, "{broken");
        File.WriteAllText(markerPath, "locked-v1");
        JNestLockPersistence.SaveLocked();
        using (var probe = StartChild("--session-probe"))
        {
            Assert(probe.WaitForExit(10000) && probe.ExitCode == 0, "new process retained session lock");
        }
        Assert(LockCredentialService.Verify("abcdef") && JNestLockPersistence.IsLocked, "legacy files replaced current session");
        LockCredentialService.DeleteLegacyCredential();
        JNestLockPersistence.DeleteLegacyMarker();
        Assert(!File.Exists(credentialPath) && !File.Exists(markerPath), "legacy files not removed");
        Assert(LockCredentialService.Verify("abcdef") && JNestLockPersistence.IsLocked, "cleanup altered current session");
        JNestLockPersistence.SaveUnlocked();
        return Task.CompletedTask;
    }

    private static async Task RelockAndGate()
    {
        var window = new MainWindow();
        window.LockForReview();
        Assert(JNestLockPersistence.IsLocked, "first session lock missing");
        window.LockPasswordInput.Password = "abcdef";
        var first = window.UnlockForReview();
        Assert(!window.UnlockJNestButton.IsEnabled && !window.LockPasswordInput.IsEnabled, "verification UI not gated");
        for (var i = 0; i < 10; i++)
            Assert(window.UnlockForReview().IsCompletedSuccessfully, "duplicate submission entered verification");
        await window.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        window.LockForReview();
        Assert(window.ProtectedForReview && JNestLockPersistence.IsLocked, "second lock failed");
        window.ScanCompletion.SetResult();
        await first;
        Assert(window.ProtectedForReview && JNestLockPersistence.IsLocked, "old restore cleared newer lock");
        Assert(window.WindowHost.Suspensions == 1, "relock suspension is unbalanced");
        window.LockPasswordInput.Password = "abcdef";
        await window.UnlockForReview();
        Assert(!window.ProtectedForReview && !JNestLockPersistence.IsLocked, "fresh unlock failed");
        Assert(window.WindowHost.Suspensions == 0, "final suspension is unbalanced");
    }

    private static async Task StaleRestoreFailure()
    {
        var window = new MainWindow();
        window.LockForReview();
        window.LockPasswordInput.Password = "abcdef";
        var first = window.UnlockForReview();
        await window.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        window.LockForReview();
        window.ScanCompletion.SetException(new IOException("old scan failed"));
        await first;
        Assert(window.ProtectedForReview && JNestLockPersistence.IsLocked, "old failure damaged new lock");
        Assert(window.LockErrorText.Text == "", "old failure overwrote newer UI");
        Assert(window.WindowHost.Suspensions == 1, "old failure duplicated suspension");
        JNestLockPersistence.SaveUnlocked();
    }

    private static Task SettingsFailure()
    {
        var normal = AppSettings.SettingsPathOverride;
        var blocked = Path.Combine(Root, "blocked-settings-directory");
        File.WriteAllText(blocked, "not a directory");
        AppSettings.SettingsPathOverride = Path.Combine(blocked, "settings.json");
        var notices = new List<string>();
        void Notice(string message) => notices.Add(message);
        AppSettings.SaveProblem += Notice;
        try
        {
            var settings = new AppSettings { DndEnabled = true };
            Assert(!settings.Save(), "write failure reported success");
            Assert(notices.Count == 1 && notices[0].Contains("保存失败"), "failure notification missing");
            File.Delete(blocked);
            Assert(settings.Save() && AppSettings.Load().DndEnabled, "retry lost settings");
        }
        finally { AppSettings.SaveProblem -= Notice; AppSettings.SettingsPathOverride = normal; }
        return Task.CompletedTask;
    }

    private static async Task ParkedDetachAndReattach()
    {
        using var fixture = StartFixture();
        var hwnd = new IntPtr(long.Parse((await fixture.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10)))!));
        try
        {
            using var source = new HwndSource(new HwndSourceParameters("JNest detach regression owner")
                { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x80000000) });
            using var host = new ExternalWindowHost();
            source.RootVisual = host;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert(host.AttachWindow(hwnd, false), "background attach failed");
                Assert(host.DetachWindow(hwnd, bringToFront: false), "parked client did not detach");
                Assert(!host.Contains(hwnd), "detached client remained hosted");
                Assert(NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == IntPtr.Zero,
                    "detached client retained manager owner");
                Assert(NativeMethods.IsWindowVisible(hwnd), "detached client remained hidden");
                Assert(NativeMethods.GetWindowRect(hwnd, out var rect)
                    && rect.Left > -10000 && rect.Top > -10000,
                    "detached client remained parked off screen");
                Assert(NativeMethods.GetWindowRgnBox(hwnd, out _) == 0,
                    "detached client retained the host clipping region");
            }
            Assert(host.AttachWindow(hwnd, false), "selected client background attach failed");
            Assert(host.ShowOnly(hwnd), "selected client did not reach the host bounds");
            Assert(NativeMethods.GetWindowRgnBox(hwnd, out _) != 0,
                "selected client lost the host clipping region");
            Assert(host.DetachWindow(hwnd, bringToFront: false),
                "selected client did not return to the desktop");
        }
        finally
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
            await fixture.StandardInput.WriteLineAsync("quit");
            await fixture.StandardInput.FlushAsync();
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task DiscoveryAndAttach()
    {
        using var fixture = StartFixture();
        var hwnd = new IntPtr(long.Parse((await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!));
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        Assert(pid == fixture.Id, "unexpected fixture owner");
        try
        {
            using var source = new HwndSource(new HwndSourceParameters("JNest regression owner")
                { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
            using var host = new ExternalWindowHost();
            source.RootVisual = host;
            Assert(host.IsReady, "host not initialized");
            var start = fixture.StartTime.ToUniversalTime().Ticks;
            var known = new Dictionary<IntPtr, ClientWindowDiscovery.ManagedWindowIdentity>
                { [hwnd] = new(fixture.Id, start) };
            Assert(ClientWindowDiscovery.FindClientWindows(fixture.Id).Any(c => c.Handle == hwnd), "fixture not discoverable");
            var foreground = NativeMethods.GetForegroundWindow();
            Assert(host.AttachWindow(hwnd, false), "background attach failed");
            Assert(NativeMethods.GetForegroundWindow() == foreground, "background attach stole foreground");
            Assert(NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == source.Handle, "manager owner missing");
            Assert(!ClientWindowDiscovery.FindClientWindows(fixture.Id).Any(c => c.Handle == hwnd), "ordinary discovery admitted owned window");
            Assert(Find(known, source.Handle), "verified managed primary excluded");
            Assert(Find(known, source.Handle), "managed primary lost on subsequent scan");
            Assert(!Find(known, IntPtr.Zero), "unknown manager owner accepted");
            Assert(!Find(new(), source.Handle), "unregistered owned popup accepted");
            Assert(!Find(new() { [hwnd] = new(fixture.Id, start + 1) }, source.Handle), "stale process start accepted");
            Assert(!Find(new() { [hwnd] = new(fixture.Id + 1, start) }, source.Handle), "wrong PID accepted");
            host.ForgetWindow(hwnd);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
            NativeMethods.ShowWindow(hwnd, 7); // SW_SHOWMINNOACTIVE: minimize the fixture only.
            var minimizedForeground = NativeMethods.GetForegroundWindow();
            Assert(host.AttachWindow(hwnd, false), "minimized background attach failed");
            Assert((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64() & NativeMethods.WS_MINIMIZE) == 0,
                "minimized client was not restored");
            var afterMinimizedAttach = NativeMethods.GetForegroundWindow();
            Assert(afterMinimizedAttach == minimizedForeground,
                $"minimized attach changed foreground: before=0x{minimizedForeground:X}, after=0x{afterMinimizedAttach:X}, fixture=0x{hwnd:X}, manager=0x{source.Handle:X}");
            host.ForgetWindow(hwnd);

            bool Find(Dictionary<IntPtr, ClientWindowDiscovery.ManagedWindowIdentity> identities, IntPtr owner)
                => ClientWindowDiscovery.FindClientWindows(fixture.Id, owner, identities).Any(c => c.Handle == hwnd);
        }
        finally
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
            await fixture.StandardInput.WriteLineAsync("quit");
            await fixture.StandardInput.FlushAsync();
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Task TrayRestore()
    {
        EnsureResources();
        // Instantiate the application type, not the linked lock coordinator harness.
        var type = typeof(App).Assembly.GetType("JuChuang.MainWindow")!;
        var window = (Window)Activator.CreateInstance(type)!;
        try
        {
            // Never show/load the actual main window: that would start client discovery.
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
                type.GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
            window.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
                type.GetMethod("Window_SourceInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.ShowActivated = false;
            var host = (ExternalWindowHost)type.GetField("WindowHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (var state in new[] { WindowState.Normal, WindowState.Minimized })
            {
                window.WindowState = state;
                Call(window, "HideToTray");
                Call(window, "ActivateFromToast", "activate");
                Assert(window.ShowInTaskbar && window.IsVisible && window.WindowState != WindowState.Minimized, "second-launch restore incomplete");
                Assert(!(bool)Field(host, "_ownerMinimized")!, "native host left parked");
                Assert(((DispatcherTimer)Field(window, "_clockTimer")!).IsEnabled, "clock not restarted");
            }
            Call(window, "HideToTray");
            Call(window, "ActivateFromToast", "account=missing&pid=0");
            Assert(window.ShowInTaskbar && !(bool)Field(host, "_ownerMinimized")!, "toast restore incomplete");
            Call(window, "LockJNest_Click", window, new RoutedEventArgs());
            Call(window, "HideToTray");
            Call(window, "ActivateFromToast", "account=missing&pid=0");
            Assert(((System.Windows.Controls.Border)Field(window, "LockOverlay")!).Visibility == Visibility.Visible,
                "locked toast restore exposed workspace");
            Assert((int)Field(host, "_modalDialogSuppressionCount")! == 1, "locked restore resumed hosted clients");
            Assert(((DispatcherTimer)Field(window, "_clockTimer")!).IsEnabled, "locked restore left clock stopped for later unlock");
            var lockState = (JNestLockState)Field(window, "_lockState")!;
            Assert(lockState.BeginUnlock(true), "locked restore lost lock state");
            lockState.UnlockCompleted(); // Avoid actual workspace scanning in this isolated UI test.
            JNestLockPersistence.SaveUnlocked();
        }
        finally
        {
            var lockState = (JNestLockState)Field(window, "_lockState")!;
            if (lockState.Current == JNestLockState.Phase.Locked)
            { lockState.BeginUnlock(true); lockState.UnlockCompleted(); }
            CloseWithoutPrompt(window);
        }
        return Task.CompletedTask;
    }

    private static Task SettingsDialogFailure()
    {
        EnsureResources();
        var normal = AppSettings.SettingsPathOverride;
        var blocked = Path.Combine(Root, "blocked-settings-ui-directory");
        File.WriteAllText(blocked, "not a directory");
        AppSettings.SettingsPathOverride = Path.Combine(blocked, "settings.json");
        var dialog = new SettingsDialog(new AppSettings(), () => { }, () => { });
        try
        {
            Call(dialog, "SaveSettings");
            var status = (TextBlock)Field(dialog, "SaveStatusText")!;
            var retry = (Button)Field(dialog, "RetrySaveButton")!;
            Assert(status.Text.Contains("保存失败") && retry.Visibility == Visibility.Visible, "save failure UI missing");
            File.Delete(blocked);
            Call(dialog, "RetrySave_Click", dialog, new RoutedEventArgs());
            Assert(!status.Text.Contains("失败") && retry.Visibility == Visibility.Collapsed, "retry did not clear failure UI");
        }
        finally { dialog.Close(); AppSettings.SettingsPathOverride = normal; }
        return Task.CompletedTask;
    }

    private static Task DndAllDayHint()
    {
        EnsureResources();
        var dialog = new SettingsDialog(new AppSettings(), () => { }, () => { });
        try
        {
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)Field(dialog, "DndCheck")!;
            var start = (ComboBox)Field(dialog, "DndStartTime")!;
            var end = (ComboBox)Field(dialog, "DndEndTime")!;
            var hint = (TextBlock)Field(dialog, "DndAllDayHint")!;
            Assert(hint.Visibility == Visibility.Collapsed, "DND hint shown while disabled");
            toggle.IsChecked = true;
            start.SelectedValue = 22 * 60;
            end.SelectedValue = 22 * 60;
            Assert(hint.Visibility == Visibility.Visible, "Equal DND endpoints did not show warning");
            end.SelectedValue = 8 * 60;
            Assert(hint.Visibility == Visibility.Collapsed, "DND hint not cleared after end changed");
            end.SelectedValue = 22 * 60;
            toggle.IsChecked = false;
            Assert(hint.Visibility == Visibility.Collapsed, "DND hint shown when disabled");
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    }

    private static Task StatusClock()
    {
        EnsureResources();
        var window = (Window)Activator.CreateInstance(typeof(App).Assembly.GetType("JuChuang.MainWindow")!)!;
        try
        {
            Call(window, "UpdateLocalClock");
            var date = (TextBlock)Field(window, "LocalDateText")!;
            var time = (TextBlock)Field(window, "LocalTimeText")!;
            Assert(System.Text.RegularExpressions.Regex.IsMatch(date.Text, @"^\d{1,2}月\d{1,2}日  周[一二三四五六日]$"),
                "status date or weekday format changed");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(time.Text, @"^\d{2}:\d{2}:\d{2}$"),
                "status time omitted seconds");
            Assert(date.FontSize == time.FontSize && date.FontWeight == time.FontWeight
                && date.FontFamily.Source == time.FontFamily.Source
                && date.Foreground.ToString() == time.Foreground.ToString(),
                "status clock typography is inconsistent");
            Assert(window.FindName("LocalDateTimeSeparatorText") is null,
                "status clock still contains a separator");
        }
        finally { CloseWithoutPrompt(window); }
        return Task.CompletedTask;
    }

    private static Task PasswordDialogSpacing()
    {
        EnsureResources();
        var dialog = new LockPasswordDialog();
        try
        {
            var error = (TextBlock)Field(dialog, "ErrorText")!;
            Assert(error.Visibility == Visibility.Collapsed,
                "empty password error reserves space");
            error.Text = "测试错误";
            Assert(error.Visibility == Visibility.Visible,
                "password error is hidden when populated");
            error.Text = "";
            Assert(error.Visibility == Visibility.Collapsed,
                "cleared password error still reserves space");
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    }

    private static Task DetachedAccountRemoval()
    {
        EnsureResources();
        var window = (Window)Activator.CreateInstance(typeof(App).Assembly.GetType("JuChuang.MainWindow")!)!;
        using var process = Process.GetCurrentProcess();
        var start = process.StartTime.ToUniversalTime().Ticks;
        var entry = new ClientWindowEntry(new IntPtr(0x123456), process.Id, "Weixin", "Weixin",
            ClientKind.WeChat, "测试账号")
        {
            ProcessStartUtcTicks = start,
            IsManuallyDetached = true,
            IsAttached = false
        };
        try
        {
            var entries = (System.Collections.ObjectModel.ObservableCollection<ClientWindowEntry>)
                window.GetType().GetProperty("Entries")!.GetValue(window)!;
            entries.Add(entry);
            var button = new Button { CommandParameter = entry };
            Call(window, "CloseEntry_Click", button, new RoutedEventArgs());
            Assert(!entries.Contains(entry), "detached account remained in list");
            var suppressed = (HashSet<(int ProcessId, long StartUtcTicks)>)Field(window, "_removedDetachedProcesses")!;
            Assert(suppressed.Contains((process.Id, start)), "background scan could immediately reinsert detached process");
            Assert(!process.HasExited, "detached account removal terminated its process");
        }
        finally { CloseWithoutPrompt(window); }
        return Task.CompletedTask;
    }

    private static Task MainWindowSettingsFailure()
    {
        EnsureResources();
        var normal = AppSettings.SettingsPathOverride;
        var blocked = Path.Combine(Root, "blocked-main-settings");
        File.WriteAllText(blocked, "not a directory");
        AppSettings.SettingsPathOverride = Path.Combine(blocked, "settings.json");
        var type = typeof(App).Assembly.GetType("JuChuang.MainWindow")!;
        var window = (Window)Activator.CreateInstance(type)!;
        var interval = AppSettings.ProblemNoticeInterval;
        AppSettings.ProblemNoticeInterval = TimeSpan.FromDays(1);
        try
        {
            var settings = (AppSettings)Field(window, "_settings")!;
            settings.WeChatExecutablePath = "pending-wechat.exe";
            settings.AccountNamesByIdentity["test-account"] = "Pending name";
            Assert(!(bool)Call(window, "TrySaveSettings")!, "failed main-window save reported success");
            Assert(!(bool)Call(window, "SaveAccountOrder")!, "failed order save reported success");
            var status = (TextBlock)Field(window, "StatusMessageText")!;
            Assert(status.Text.Contains("保存失败") && status.Text.Contains("重试"),
                "repeated failure was hidden by notification throttling");
            File.Delete(blocked);
            var dialog = new SettingsDialog(settings, () => { }, () => { });
            try { Call(dialog, "RetrySave_Click", dialog, new RoutedEventArgs()); }
            finally { dialog.Close(); }
            var loaded = AppSettings.Load();
            Assert(loaded.WeChatExecutablePath == "pending-wechat.exe"
                && loaded.AccountNamesByIdentity["test-account"] == "Pending name",
                "settings-page retry lost pending main-window changes");
            Assert((bool)Call(window, "SaveAccountOrder")!, "order save did not recover");
        }
        finally
        {
            CloseWithoutPrompt(window);
            AppSettings.SettingsPathOverride = normal;
            AppSettings.ProblemNoticeInterval = interval;
        }
        return Task.CompletedTask;
    }

    private static void EnsureResources()
    {
        if (Application.Current is not null) return;
        // Load styles only. Constructing App would schedule its real startup and
        // could launch window discovery when the dispatcher pumps again.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProbeAppResources.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            document.Root!.Element(ns + "Application.Resources")!.Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
    }

    private static async Task ExitAllDeferredClose()
    {
        EnsureResources();
        var window = new Window { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        Task? deferredClose = null;
        var closingCount = 0;
        var closed = false;
        window.Closing += (_, e) =>
        {
            closingCount++;
            if (closingCount != 1) return;
            e.Cancel = true;
            deferredClose = DeferredWindowClose.ScheduleAsync(window);
        };
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.Close();
        if (deferredClose is null)
            throw new InvalidOperationException("Deferred close task was not scheduled.");
        Assert(!closed && closingCount == 1,
            "exit-all close ran inside the original Closing event");
        await deferredClose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(closed && closingCount == 2, "deferred exit-all close did not finish");
    }

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void CloseWithoutPrompt(Window window)
    {
        window.GetType().GetField("_exitAllCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.GetType().GetField("_trayExitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
    }

    private static Process StartChild(string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture");
    }
    private static Process StartFixture() => StartChild("--fixture");
    private static void RunFixture()
    {
        using var source = new HwndSource(new HwndSourceParameters("聚窗窗口托管测试")
            { Width = 1200, Height = 1000, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000) });
        source.RootVisual = new Border();
        SetWindowText(source.Handle, "聚窗窗口托管测试");
        Console.WriteLine(source.Handle.ToInt64()); Console.Out.Flush();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(() => { Console.ReadLine(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
        Dispatcher.Run();
    }
    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine($"PASS: {name}"); }
        catch (Exception exception) { _failures++; Console.Error.WriteLine($"FAIL: {name}: {exception}"); }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hwnd, string title);
}
