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
    long PixelArea,
    string? ClientVersion = null);

public static class ClientWindowDiscovery
{
    private static readonly string[] WeChatProcessHints = ["wechat", "weixin"];
    private static readonly ConcurrentDictionary<int, VersionCacheEntry> VersionCache = [];

    // 进程名在进程生命周期内不变，缓存后可避免每 5 秒扫描时为每个候选窗口
    // 重复调用 Process.GetProcessById（打开进程句柄的开销远高于一次字典查找）。
    private static readonly ConcurrentDictionary<int, string> ProcessNameCache = [];

    public static IReadOnlyList<WindowCandidate> FindClientWindows(int? selfTestTargetProcessId = null)
    {
        var candidates = new List<WindowCandidate>();
        var ownProcessId = Environment.ProcessId;

        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(handle))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(handle, out var rawProcessId);
                var processId = unchecked((int)rawProcessId);
                if (processId <= 0 || processId == ownProcessId)
                {
                    return true;
                }

                var title = ReadWindowTitle(handle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(handle, out var rect) || rect.Width < 280 || rect.Height < 200)
                {
                    return true;
                }

                // WeChat media viewers and other transient windows are normally owned by
                // the main chat window. They must remain regular desktop popups.
                if (NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((style & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }

                var processName = TryGetProcessName(processId);
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

                candidates.Add(new WindowCandidate(
                    handle,
                    processId,
                    processName,
                    title.Trim(),
                    windowClass,
                    kind,
                    (long)rect.Width * rect.Height,
                    TryReadClientVersion(processId, kind)));
            }
            catch
            {
                // A process may exit while windows are being enumerated. Skip it and continue.
            }

            return true;
        }, IntPtr.Zero);

        return candidates
            .GroupBy(candidate => candidate.Handle)
            .Select(group => group.First())
            .GroupBy(candidate => (candidate.Kind, candidate.ProcessId))
            .Select(group => group
                .OrderByDescending(candidate => IsGenericMainTitle(candidate.WindowTitle))
                .ThenByDescending(candidate => candidate.PixelArea)
                .First())
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.ProcessId)
            .ToArray();
    }

    private static bool TryGetKind(string processName, string title, string windowClass, out ClientKind kind)
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

        if (normalizedProcess.Contains("whatsapp") || normalizedTitle.Contains("whatsapp"))
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

    private static bool IsAuxiliaryWeChatTitle(string normalizedTitle)
    {
        string[] auxiliaryTitleHints =
        [
            "图片", "图像", "照片", "视频", "文件", "预览", "浏览",
            "image", "photo", "picture", "video", "file", "preview", "viewer"
        ];

        return auxiliaryTitleHints.Any(normalizedTitle.Contains);
    }

    private static string TryGetProcessName(int processId)
    {
        if (ProcessNameCache.TryGetValue(processId, out var cachedName))
        {
            return cachedName;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            ProcessNameCache[processId] = name;
            return name;
        }
        catch
        {
            // 进程可能在枚举窗口期间退出。
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
