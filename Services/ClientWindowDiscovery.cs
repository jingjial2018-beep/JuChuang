using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using JuChuang.Models;

namespace JuChuang.Services;

public sealed record WindowCandidate(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string WindowClass,
    ClientKind Kind,
    ClientWindowRole Role,
    long PixelArea,
    string? ClientVersion = null,
    long? ProcessStartUtcTicks = null);

public static class ClientWindowDiscovery
{
    internal sealed record ManagedWindowIdentity(
        int ProcessId, long ProcessStartUtcTicks, ClientWindowRole Role = ClientWindowRole.Unknown);
    private static readonly string[] WeChatProcessHints = ["wechat", "weixin"];
    private static readonly ConcurrentDictionary<int, VersionCacheEntry> VersionCache = [];

    // 进程名缓存只保留很短时间。Windows 会复用 PID，如果永久按 PID 缓存，
    // 一个已经退出的 Chrome/微信 PID 被新进程复用后可能造成误识别。
    private static readonly ConcurrentDictionary<int, ProcessNameCacheEntry> ProcessNameCache = [];
    private static readonly TimeSpan ProcessNameCacheLifetime = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<WindowCandidate> FindClientWindows(int? selfTestTargetProcessId = null)
        => FindClientWindowsCore(selfTestTargetProcessId, includeHiddenProcessId: null);

    internal static IReadOnlyList<WindowCandidate> FindClientWindows(
        int? selfTestTargetProcessId, IntPtr managerOwner,
        IReadOnlyDictionary<IntPtr, ManagedWindowIdentity> managedWindows)
        => FindClientWindowsCore(selfTestTargetProcessId, null, managerOwner, managedWindows);

    // Explicit recovery and the manual hidden-window check may inspect invisible
    // windows for one known PID. Background discovery remains visible-only.
    public static IReadOnlyList<WindowCandidate> FindRecoverableMainWindows(int processId)
        => FindClientWindowsCore(null, processId)
            .Where(candidate => candidate.ProcessId == processId && candidate.Role == ClientWindowRole.Main)
            .ToArray();

    private static IReadOnlyList<WindowCandidate> FindClientWindowsCore(
        int? selfTestTargetProcessId, int? includeHiddenProcessId,
        IntPtr managerOwner = default,
        IReadOnlyDictionary<IntPtr, ManagedWindowIdentity>? managedWindows = null)
    {
        var candidates = new List<WindowCandidate>();
        var ownProcessId = Environment.ProcessId;

        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var rawProcessId);
                var processId = unchecked((int)rawProcessId);
                if (processId <= 0 || processId == ownProcessId)
                {
                    return true;
                }
                if (!NativeMethods.IsWindowVisible(handle) && processId != includeHiddenProcessId)
                {
                    return true;
                }

                // WhatsApp Store/WebView builds can briefly expose a normal main window
                // before its title text is populated. Resolve the process first so a blank
                // title does not make an otherwise valid WhatsApp main window invisible to
                // the startup scan. WeChat keeps the stricter title requirement below.
                var processName = TryGetProcessName(processId);
                var title = ReadWindowTitle(handle);
                var processLooksLikeWhatsApp = processName.Contains(
                    "whatsapp",
                    StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(title) && !processLooksLikeWhatsApp)
                {
                    return true;
                }

                // WeChat media viewers and other transient windows are normally owned by
                // the main chat window. They must remain regular desktop popups.
                // A known primary window owned by this JNest instance must continue
                // through discovery for role changes and deferred profile retries.
                // HWND, PID and process start must all match the UI-thread snapshot.
                var owner = NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER);
                ManagedWindowIdentity? identity = null;
                var isManagedWindow = owner != IntPtr.Zero && owner == managerOwner
                    && managedWindows is not null && managedWindows.TryGetValue(handle, out identity)
                    && identity.ProcessId == processId
                    && TryReadProcessStartUtcTicks(processId) == identity.ProcessStartUtcTicks;
                if (owner != IntPtr.Zero && !isManagedWindow)
                {
                    return true;
                }

                // Attach parks nonselected clients after initially sizing them to 1x1.
                // New-window size heuristics cannot exclude an already verified primary.
                if (!NativeMethods.GetWindowRect(handle, out var rect)
                    || (!isManagedWindow && (rect.Width < 280 || rect.Height < 200)))
                    return true;

                var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((style & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }

                var windowClass = ReadWindowClass(handle);
                var isForcedSelfTestTarget = selfTestTargetProcessId == processId
                    && title.Equals("聚窗窗口托管测试", StringComparison.Ordinal)
                    && windowClass.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase);
                var kind = default(ClientKind);
                if (!isForcedSelfTestTarget && !TryGetKind(processName, title, windowClass, out kind))
                {
                    return true;
                }
                if (isForcedSelfTestTarget)
                {
                    kind = ClientKind.WeChat;
                }

                var role = ResolveWindowRole(kind, title, windowClass, rect.Width, rect.Height,
                    NativeMethods.GetDpiForWindow(handle));
                if (isManagedWindow && identity is not null && identity.Role != ClientWindowRole.Unknown
                    && (rect.Width < 280 || rect.Height < 200) && role != ClientWindowRole.Login)
                    role = identity.Role; // Parking dimensions cannot establish a login-to-main transition.
                candidates.Add(new WindowCandidate(
                    handle,
                    processId,
                    processName,
                    title.Trim(),
                    windowClass,
                    kind,
                    role,
                    (long)rect.Width * rect.Height,
                    TryReadClientVersion(processId, kind),
                    TryReadProcessStartUtcTicks(processId)));
            }
            catch
            {
                // A process may exit while windows are being enumerated. Skip it and continue.
            }

            return true;
        }, IntPtr.Zero);

        var uniqueCandidates = candidates
            .GroupBy(candidate => candidate.Handle)
            .Select(group => group.First());
        var selectedCandidates = includeHiddenProcessId.HasValue
            ? uniqueCandidates
            : uniqueCandidates.GroupBy(candidate => (candidate.Kind, candidate.ProcessId))
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Role == ClientWindowRole.Main)
                    .ThenByDescending(candidate => IsGenericMainTitle(candidate.WindowTitle))
                    .ThenByDescending(candidate => candidate.PixelArea)
                    .First());
        return selectedCandidates
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.ProcessId)
            .ToArray();
    }

    private static long? TryReadProcessStartUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSameProcessInstance(ClientWindowEntry entry, WindowCandidate candidate)
        => entry.Kind == candidate.Kind
           && entry.ProcessId == candidate.ProcessId
           && entry.ProcessStartUtcTicks is { } knownStart
           && candidate.ProcessStartUtcTicks == knownStart;

    internal static bool TryGetKind(string processName, string title, string windowClass, out ClientKind kind)
    {
        var normalizedProcess = processName.ToLowerInvariant();
        var normalizedTitle = title.ToLowerInvariant();

        // Dedicated synthetic window used by the cross-process hosting self-test.
        // Its name and title intentionally do not resemble WeChat, so a running
        // older release cannot discover and interfere with the test window.
        if ((normalizedProcess.Contains("hostselftestclient")
             || normalizedProcess.Contains("windowlocktestclientv023"))
            && normalizedTitle == "聚窗窗口托管测试"
            && windowClass.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase))
        {
            kind = ClientKind.WeChat;
            return true;
        }

        // 只认进程名，不认窗口标题：真实 WhatsApp 客户端（WhatsApp.Root.exe / 旧版
        // WhatsApp.exe）进程名必含 "whatsapp"；若再按标题匹配，Chrome 打开的网页标题
        // 只要含 "whatsapp"（例如本项目仓库名"…与 WhatsApp 多窗口管理器"）就会被误判为
        // WhatsApp 客户端，随后被 AttachWindow 移到屏幕左上角、无法点击。
        if (normalizedProcess.Contains("whatsapp"))
        {
            kind = ClientKind.WhatsApp;
            return true;
        }

        if (WeChatProcessHints.Any(normalizedProcess.Contains)
            || normalizedTitle is "微信" or "wechat" or "weixin"
            || normalizedTitle.Contains("微信"))
        {
            if (IsAuxiliaryWeChatTitle(normalizedTitle))
            {
                kind = default;
                return false;
            }

            // WeChat 4.x uses a Qt QWindowIcon class for its account window. Its built-in
            // browser uses Chrome_WidgetWin_0 and often keeps the generic title "微信";
            // requiring both signals prevents links from becoming account entries.
            var isKnownMainClass = windowClass.Contains("QWindowIcon", StringComparison.OrdinalIgnoreCase);
            if (!isKnownMainClass || !IsPrimaryWeChatTitle(title))
            {
                kind = default;
                return false;
            }

            kind = ClientKind.WeChat;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsGenericMainTitle(string title)
    {
        var normalized = title.Trim().ToLowerInvariant();
        return normalized is "微信" or "wechat" or "weixin";
    }

    private static bool IsPrimaryWeChatTitle(string title)
    {
        var normalized = title.Trim().ToLowerInvariant();
        return IsGenericMainTitle(title)
               || normalized is "微信登录" or "登录微信" or "wechat login" or "weixin login";
    }

    private static ClientWindowRole ResolveWindowRole(ClientKind kind, string title, string windowClass, int width, int height, uint dpi)
    {
        if (kind == ClientKind.WhatsApp)
        {
            return ClientWindowRole.Main;
        }

        var normalized = title.Trim().ToLowerInvariant();
        if (normalized is "微信登录" or "登录微信" or "wechat login" or "weixin login")
        {
            return ClientWindowRole.Login;
        }

        // A confirmed login-specific native class is a stronger signal than
        // the generic "微信" caption. This is additive: unknown versions
        // continue to use the compact-window fallback below.
        if (windowClass.Contains("WeChatLoginWndForPC", StringComparison.OrdinalIgnoreCase))
            return ClientWindowRole.Login;

        // Qt uses the generic WeChat title for BOTH login and chat windows.
        // The old physical-pixel area < 1,000,000 incorrectly classified an
        // ordinary 1027x739 chat window at 100% DPI as "Login" (PR #59).
        // Only a genuinely compact, near-portrait login-shaped window should
        // take the centered login path. Normalize dimensions to 96-DPI units.
        // Explicit login-only titles and classes above remain authoritative.
        var isQtWindow = windowClass.StartsWith("Qt", StringComparison.OrdinalIgnoreCase)
            && windowClass.EndsWith("QWindowIcon", StringComparison.OrdinalIgnoreCase);
        if (IsGenericMainTitle(title) && isQtWindow
            && WeChatWindowRolePolicy.IsCompactLoginShape(width, height, dpi))
            return ClientWindowRole.Login;

        return IsGenericMainTitle(title)
            ? ClientWindowRole.Main
            : ClientWindowRole.Unknown;
    }

    private static bool IsAuxiliaryWeChatTitle(string normalizedTitle)
    {
        string[] auxiliaryTitleHints =
        [
            "图片", "图像", "照片", "视频", "文件", "预览", "浏览",
            "image", "photo", "picture", "video", "file", "preview", "viewer"
        ];

        return auxiliaryTitleHints.Any(normalizedTitle.Contains);
    }

    internal static bool IsPotentialClientProcess(int processId)
    {
        var processName = TryGetProcessName(processId);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = processName.ToLowerInvariant();
        return normalized.Contains("whatsapp")
               || WeChatProcessHints.Any(normalized.Contains)
               || normalized.Contains("hostselftestclient")
               || normalized.Contains("windowlocktestclientv023");
    }

    private static string TryGetProcessName(int processId)
    {
        var now = DateTime.UtcNow;
        if (ProcessNameCache.TryGetValue(processId, out var cached)
            && now - cached.CachedUtc < ProcessNameCacheLifetime)
        {
            return cached.Name;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            ProcessNameCache[processId] = new ProcessNameCacheEntry(name, now);
            return name;
        }
        catch
        {
            // 进程可能在枚举窗口期间退出。清掉旧 PID 缓存，避免之后 PID 复用误判。
            ProcessNameCache.TryRemove(processId, out _);
            VersionCache.TryRemove(processId, out _);
            return string.Empty;
        }
    }

    private static string? TryReadClientVersion(int processId, ClientKind kind)
    {
        if (kind != ClientKind.WeChat)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var startUtc = process.StartTime.ToUniversalTime();
            if (VersionCache.TryGetValue(processId, out var cached)
                && cached.StartUtc == startUtc)
            {
                return cached.Version;
            }

            var versionText = process.MainModule?.FileVersionInfo.FileVersion?.Trim();
            var resolvedVersion = Version.TryParse(versionText, out var version)
                ? version.ToString()
                : null;
            VersionCache[processId] = new VersionCacheEntry(startUtc, resolvedVersion);
            return resolvedVersion;
        }
        catch
        {
            // Windows can deny MainModule access when the client uses a different
            // privilege level. Keep a neutral fallback label in that case.
            return null;
        }
    }

    private readonly record struct ProcessNameCacheEntry(string Name, DateTime CachedUtc);

    private readonly record struct VersionCacheEntry(DateTime StartUtc, string? Version);

    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = Math.Min(1024, NativeMethods.GetWindowTextLength(handle) + 1);
        if (length <= 1)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length);
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadWindowClass(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }
}
