using System.Runtime.InteropServices;
using System.Text;

namespace JuChuang.Services;

/// <summary>
/// Best-effort, injection-free observer for WeChat notification-area activity.
///
/// It never opens a WeChat process. The direct path inspects Explorer's notification-area
/// toolbar metadata and maps tray owner HWNDs back to WeChat PIDs. When Windows 11 does
/// not expose a classic toolbar, an out-of-context WinEvent hook on Explorer is used only
/// as a coarse "tray changed" trigger. Neither path reads chat data or message text.
/// </summary>
internal sealed class WeChatTrayActivityMonitor : IDisposable
{
    private const int MaxToolbarButtons = 256;
    private const int RemoteButtonBytes = 32;
    private const int RemoteTrayDataBytes = 32;
    private const int RemoteTextBytes = 512;
    private static readonly TimeSpan GenericFallbackDebounce = TimeSpan.FromMilliseconds(1500);

    private readonly Dictionary<TrayIconIdentity, TrayIconFingerprint> _previousIcons = [];
    private readonly HashSet<int> _knownTrackedProcesses = [];
    private readonly List<IntPtr> _eventHooks = [];
    private Native.WinEventDelegate? _eventCallback;
    private int _explorerProcessId;
    private int _genericActivityVersion;
    private int _lastConsumedGenericActivityVersion;
    private DateTime _lastGenericFallbackUtc = DateTime.MinValue;
    private bool _disposed;

    public void Start() => EnsureExplorerHooks();

    public TrayActivityPollResult Poll(IReadOnlyCollection<int> trackedProcessIds)
    {
        if (_disposed)
        {
            return TrayActivityPollResult.Unavailable("Disposed");
        }

        EnsureExplorerHooks();

        var tracked = trackedProcessIds
            .Where(processId => processId > 0)
            .ToHashSet();

        _knownTrackedProcesses.RemoveWhere(processId => !tracked.Contains(processId));
        var newlyTracked = tracked
            .Where(processId => _knownTrackedProcesses.Add(processId))
            .ToHashSet();

        if (tracked.Count == 0)
        {
            _previousIcons.Clear();
            return new TrayActivityPollResult(
                DirectEnumerationAvailable: false,
                MatchingIconCount: 0,
                ChangedProcessIds: [],
                GenericTrayActivity: false,
                StatusCode: _eventHooks.Count > 0 ? "IdleWinEventFallback" : "Idle");
        }

        var snapshot = EnumerateTrayIcons(tracked);
        var current = new Dictionary<TrayIconIdentity, TrayIconFingerprint>();
        foreach (var icon in snapshot.Icons)
        {
            current[icon.Identity] = icon.Fingerprint;
        }

        var changedProcesses = new HashSet<int>();
        foreach (var pair in current)
        {
            var processId = pair.Key.ProcessId;
            if (newlyTracked.Contains(processId))
            {
                continue;
            }

            if (_previousIcons.TryGetValue(pair.Key, out var previous)
                && !previous.Equals(pair.Value))
            {
                changedProcesses.Add(processId);
            }
        }

        // If Explorer rebuilt a target tray button, the stable PID/uID identity may
        // disappear and reappear. Do not treat a first observation as activity, but
        // once a PID already had a baseline, replacement of all its icon identities
        // is useful as a best-effort trigger.
        foreach (var processId in tracked)
        {
            if (newlyTracked.Contains(processId)
                || changedProcesses.Contains(processId))
            {
                continue;
            }

            var hadPrevious = _previousIcons.Keys.Any(key => key.ProcessId == processId);
            var hasCurrent = current.Keys.Any(key => key.ProcessId == processId);
            if (hadPrevious && hasCurrent)
            {
                var previousKeys = _previousIcons.Keys
                    .Where(key => key.ProcessId == processId)
                    .ToHashSet();
                var currentKeys = current.Keys
                    .Where(key => key.ProcessId == processId)
                    .ToHashSet();
                if (!previousKeys.SetEquals(currentKeys))
                {
                    changedProcesses.Add(processId);
                }
            }
        }

        _previousIcons.Clear();
        foreach (var pair in current)
        {
            _previousIcons[pair.Key] = pair.Value;
        }

        var genericVersion = Volatile.Read(ref _genericActivityVersion);
        var genericChanged = genericVersion != _lastConsumedGenericActivityVersion;
        _lastConsumedGenericActivityVersion = genericVersion;

        // Exact PID mapping wins. The generic Explorer event path is only used when
        // Windows exposes no tray icon that can be mapped to the tracked WeChat PIDs.
        var genericFallback = false;
        if (snapshot.MatchingIconCount == 0
            && genericChanged
            && _eventHooks.Count > 0
            && DateTime.UtcNow - _lastGenericFallbackUtc >= GenericFallbackDebounce)
        {
            genericFallback = true;
            _lastGenericFallbackUtc = DateTime.UtcNow;
        }

        var statusCode = snapshot.DirectEnumerationAvailable
            ? snapshot.MatchingIconCount > 0 ? "DirectMapped" : "DirectNoWeChatIcon"
            : _eventHooks.Count > 0 ? "WinEventFallback" : "Unavailable";

        return new TrayActivityPollResult(
            snapshot.DirectEnumerationAvailable,
            snapshot.MatchingIconCount,
            changedProcesses.OrderBy(value => value).ToArray(),
            genericFallback,
            statusCode);
    }

    private TraySnapshot EnumerateTrayIcons(HashSet<int> trackedProcessIds)
    {
        var toolbars = FindNotificationToolbars();
        if (toolbars.Count == 0)
        {
            return new TraySnapshot(false, 0, []);
        }

        var icons = new List<TrayIconSnapshot>();
        var anyToolbarReadable = false;

        foreach (var toolbar in toolbars)
        {
            Native.GetWindowThreadProcessId(toolbar, out var rawExplorerPid);
            var explorerPid = unchecked((int)rawExplorerPid);
            if (explorerPid <= 0)
            {
                continue;
            }

            using var process = SafeNativeHandle.OpenProcess(
                Native.ProcessVmOperation | Native.ProcessVmRead | Native.ProcessQueryLimitedInformation,
                explorerPid);
            if (process.IsInvalid)
            {
                continue;
            }

            if (!TrySend(toolbar, Native.TbButtonCount, UIntPtr.Zero, IntPtr.Zero, out var countResult))
            {
                continue;
            }

            var buttonCount = unchecked((int)countResult.ToUInt64());
            if (buttonCount < 0 || buttonCount > MaxToolbarButtons)
            {
                continue;
            }

            anyToolbarReadable = true;
            var remoteButton = Native.VirtualAllocEx(
                process.DangerousGetHandle(),
                IntPtr.Zero,
                (nuint)RemoteButtonBytes,
                Native.MemCommit | Native.MemReserve,
                Native.PageReadWrite);
            var remoteText = Native.VirtualAllocEx(
                process.DangerousGetHandle(),
                IntPtr.Zero,
                (nuint)RemoteTextBytes,
                Native.MemCommit | Native.MemReserve,
                Native.PageReadWrite);

            if (remoteButton == IntPtr.Zero)
            {
                if (remoteText != IntPtr.Zero)
                {
                    Native.VirtualFreeEx(process.DangerousGetHandle(), remoteText, 0, Native.MemRelease);
                }
                continue;
            }

            try
            {
                var buttonBytes = new byte[RemoteButtonBytes];
                var trayBytes = new byte[RemoteTrayDataBytes];
                var textBytes = new byte[RemoteTextBytes];

                for (var index = 0; index < buttonCount; index++)
                {
                    if (!TrySend(
                            toolbar,
                            Native.TbGetButton,
                            new UIntPtr(unchecked((uint)index)),
                            remoteButton,
                            out _)
                        || !TryRead(
                            process.DangerousGetHandle(),
                            remoteButton,
                            buttonBytes,
                            RemoteButtonBytes))
                    {
                        continue;
                    }

                    var button = ToolbarButton64.Parse(buttonBytes);
                    if (button.Data.ToInt64() <= 0x10000)
                    {
                        continue;
                    }

                    if (!TryRead(
                            process.DangerousGetHandle(),
                            button.Data,
                            trayBytes,
                            RemoteTrayDataBytes))
                    {
                        continue;
                    }

                    var tray = TrayData64.Parse(trayBytes);
                    if (tray.OwnerWindow == IntPtr.Zero || !Native.IsWindow(tray.OwnerWindow))
                    {
                        continue;
                    }

                    Native.GetWindowThreadProcessId(tray.OwnerWindow, out var rawOwnerPid);
                    var ownerPid = unchecked((int)rawOwnerPid);
                    if (!trackedProcessIds.Contains(ownerPid))
                    {
                        continue;
                    }

                    var textHash = 0UL;
                    if (remoteText != IntPtr.Zero
                        && TrySend(
                            toolbar,
                            Native.TbGetButtonTextW,
                            new UIntPtr(unchecked((uint)button.CommandId)),
                            remoteText,
                            out var textLengthResult))
                    {
                        var textLength = unchecked((long)textLengthResult.ToUInt64());
                        if (textLength > 0 && textLength < (RemoteTextBytes / 2) - 1)
                        {
                            var bytesToRead = checked((int)((textLength + 1) * 2));
                            if (TryRead(
                                    process.DangerousGetHandle(),
                                    remoteText,
                                    textBytes,
                                    bytesToRead))
                            {
                                textHash = HashUtf16Bytes(textBytes, bytesToRead);
                            }
                        }
                    }

                    var identity = new TrayIconIdentity(ownerPid, tray.IconId);
                    var fingerprint = new TrayIconFingerprint(
                        tray.OwnerWindow.ToInt64(),
                        tray.IconHandle.ToInt64(),
                        tray.CallbackMessage,
                        tray.Reserved0,
                        tray.Reserved1,
                        button.BitmapIndex,
                        button.CommandId,
                        button.State,
                        button.Style,
                        textHash);
                    icons.Add(new TrayIconSnapshot(identity, fingerprint));
                }
            }
            finally
            {
                Native.VirtualFreeEx(
                    process.DangerousGetHandle(),
                    remoteButton,
                    0,
                    Native.MemRelease);
                if (remoteText != IntPtr.Zero)
                {
                    Native.VirtualFreeEx(
                        process.DangerousGetHandle(),
                        remoteText,
                        0,
                        Native.MemRelease);
                }
            }
        }

        return new TraySnapshot(anyToolbarReadable, icons.Count, icons);
    }

    private static bool TryRead(IntPtr process, IntPtr address, byte[] buffer, int bytesToRead)
    {
        Array.Clear(buffer, 0, Math.Min(buffer.Length, bytesToRead));
        return Native.ReadProcessMemory(
                   process,
                   address,
                   buffer,
                   (nuint)bytesToRead,
                   out var bytesRead)
               && bytesRead == (nuint)bytesToRead;
    }

    private static bool TrySend(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        out UIntPtr result)
        => Native.SendMessageTimeout(
               window,
               message,
               wParam,
               lParam,
               Native.SmtoAbortIfHung,
               120,
               out result) != IntPtr.Zero;

    private static ulong HashUtf16Bytes(byte[] bytes, int length)
    {
        // FNV-1a. The raw tray label is never logged or persisted.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        for (var index = 0; index < length; index++)
        {
            hash ^= bytes[index];
            hash *= prime;
        }
        return hash;
    }

    private static List<IntPtr> FindNotificationToolbars()
    {
        var roots = new List<IntPtr>();
        Native.EnumWindows((window, _) =>
        {
            var className = GetClassName(window);
            if (IsTrayRootClass(className))
            {
                roots.Add(window);
            }
            return true;
        }, IntPtr.Zero);

        var toolbars = new HashSet<IntPtr>();
        foreach (var root in roots)
        {
            Native.EnumChildWindows(root, (window, _) =>
            {
                if (GetClassName(window).Equals("ToolbarWindow32", StringComparison.Ordinal))
                {
                    toolbars.Add(window);
                }
                return true;
            }, IntPtr.Zero);
        }

        return toolbars.ToList();
    }

    private void EnsureExplorerHooks()
    {
        if (_disposed)
        {
            return;
        }

        var explorerPid = ResolveExplorerProcessId();
        if (explorerPid <= 0)
        {
            RemoveExplorerHooks();
            return;
        }

        if (_explorerProcessId == explorerPid && _eventHooks.Count > 0)
        {
            return;
        }

        RemoveExplorerHooks();
        _explorerProcessId = explorerPid;
        _eventCallback ??= OnExplorerWinEvent;

        foreach (var eventType in new[]
                 {
                     Native.EventObjectReorder,
                     Native.EventObjectStateChange,
                     Native.EventObjectNameChange,
                     Native.EventObjectValueChange
                 })
        {
            var hook = Native.SetWinEventHook(
                eventType,
                eventType,
                IntPtr.Zero,
                _eventCallback,
                unchecked((uint)explorerPid),
                0,
                Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);
            if (hook != IntPtr.Zero)
            {
                _eventHooks.Add(hook);
            }
        }
    }

    private void OnExplorerWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || window == IntPtr.Zero)
        {
            return;
        }

        var root = Native.GetAncestor(window, Native.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = window;
        }

        if (IsTrayRootClass(GetClassName(root)))
        {
            Interlocked.Increment(ref _genericActivityVersion);
        }
    }

    private static int ResolveExplorerProcessId()
    {
        var shellTray = Native.FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero)
        {
            return 0;
        }

        Native.GetWindowThreadProcessId(shellTray, out var rawProcessId);
        return unchecked((int)rawProcessId);
    }

    private static bool IsTrayRootClass(string className)
        => className.Equals("Shell_TrayWnd", StringComparison.Ordinal)
           || className.Equals("NotifyIconOverflowWindow", StringComparison.Ordinal)
           || className.Contains("OverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
           || className.Contains("SystemTray", StringComparison.OrdinalIgnoreCase);

    private static string GetClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);
        return Native.GetClassName(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private void RemoveExplorerHooks()
    {
        foreach (var hook in _eventHooks)
        {
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(hook);
            }
        }
        _eventHooks.Clear();
        _explorerProcessId = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveExplorerHooks();
        _previousIcons.Clear();
        _knownTrackedProcesses.Clear();
    }

    private readonly record struct TrayIconIdentity(int ProcessId, uint IconId);

    private readonly record struct TrayIconFingerprint(
        long OwnerWindow,
        long IconHandle,
        uint CallbackMessage,
        uint Reserved0,
        uint Reserved1,
        int BitmapIndex,
        int CommandId,
        byte State,
        byte Style,
        ulong TextHash);

    private readonly record struct TrayIconSnapshot(
        TrayIconIdentity Identity,
        TrayIconFingerprint Fingerprint);

    private readonly record struct TraySnapshot(
        bool DirectEnumerationAvailable,
        int MatchingIconCount,
        IReadOnlyList<TrayIconSnapshot> Icons);

    private readonly record struct ToolbarButton64(
        int BitmapIndex,
        int CommandId,
        byte State,
        byte Style,
        IntPtr Data)
    {
        internal static ToolbarButton64 Parse(byte[] bytes)
            => new(
                BitConverter.ToInt32(bytes, 0),
                BitConverter.ToInt32(bytes, 4),
                bytes[8],
                bytes[9],
                new IntPtr(BitConverter.ToInt64(bytes, 16)));
    }

    private readonly record struct TrayData64(
        IntPtr OwnerWindow,
        uint IconId,
        uint CallbackMessage,
        uint Reserved0,
        uint Reserved1,
        IntPtr IconHandle)
    {
        internal static TrayData64 Parse(byte[] bytes)
            => new(
                new IntPtr(BitConverter.ToInt64(bytes, 0)),
                BitConverter.ToUInt32(bytes, 8),
                BitConverter.ToUInt32(bytes, 12),
                BitConverter.ToUInt32(bytes, 16),
                BitConverter.ToUInt32(bytes, 20),
                new IntPtr(BitConverter.ToInt64(bytes, 24)));
    }

    private static class Native
    {
        internal const uint ProcessVmOperation = 0x0008;
        internal const uint ProcessVmRead = 0x0010;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint MemCommit = 0x1000;
        internal const uint MemReserve = 0x2000;
        internal const uint MemRelease = 0x8000;
        internal const uint PageReadWrite = 0x04;
        internal const uint SmtoAbortIfHung = 0x0002;
        internal const uint TbGetButton = 0x0400 + 23;
        internal const uint TbButtonCount = 0x0400 + 24;
        internal const uint TbGetButtonTextW = 0x0400 + 75;
        internal const uint EventObjectReorder = 0x8004;
        internal const uint EventObjectStateChange = 0x800A;
        internal const uint EventObjectNameChange = 0x800C;
        internal const uint EventObjectValueChange = 0x800E;
        internal const uint WinEventOutOfContext = 0x0000;
        internal const uint WinEventSkipOwnProcess = 0x0002;
        internal const uint GaRoot = 2;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);
        internal delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            IntPtr parent,
            EnumWindowsProc callback,
            IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(
            IntPtr window,
            StringBuilder className,
            int maxCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookModule,
            WinEventDelegate eventProc,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(IntPtr eventHook);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(
            IntPtr process,
            IntPtr address,
            nuint size,
            uint allocationType,
            uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFreeEx(
            IntPtr process,
            IntPtr address,
            nuint size,
            uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            nuint size,
            out nuint bytesRead);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }

    private sealed class SafeNativeHandle : IDisposable
    {
        private IntPtr _handle;

        private SafeNativeHandle(IntPtr handle) => _handle = handle;

        internal bool IsInvalid => _handle == IntPtr.Zero;

        internal IntPtr DangerousGetHandle() => _handle;

        internal static SafeNativeHandle OpenProcess(uint access, int processId)
            => new(Native.OpenProcess(access, false, unchecked((uint)processId)));

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                Native.CloseHandle(handle);
            }
        }
    }
}

internal readonly record struct TrayActivityPollResult(
    bool DirectEnumerationAvailable,
    int MatchingIconCount,
    IReadOnlyList<int> ChangedProcessIds,
    bool GenericTrayActivity,
    string StatusCode)
{
    internal static TrayActivityPollResult Unavailable(string statusCode)
        => new(false, 0, [], false, statusCode);
}
