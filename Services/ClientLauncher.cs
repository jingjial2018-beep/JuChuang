using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace JuChuang.Services;

public sealed record WeChatLaunchResult(
    bool Success,
    int? ProcessId,
    bool UsedMultiInstanceBypass,
    string Strategy,
    int? Win32Error = null);

public static class ClientLauncher
{
    private static readonly string[] WeChatProcessNames = ["Weixin", "WeChat"];

    // EV多聊 1.1.9 静态分析中确认的微信 4.x 单实例 Mutex。
    // 只在已经存在微信进程时尝试短时调整 DACL；失败则回退普通启动。
    private const string WeChatInstanceMutexName = "XWeChat_App_Instance_Identity_Mutex_Name";
    private const uint MutexAllAccess = 0x001F0001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int SeKernelObject = 6;
    private const uint AclRevision = 2;
    private const int WinWorldSid = 1;
    private const int SecurityMaxSidSize = 68;

    public static string? FindWeChatExecutable(string? configuredPath)
    {
        if (IsExecutable(configuredPath))
        {
            return configuredPath;
        }

        foreach (var processName in WeChatProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var runningPath = process.MainModule?.FileName;
                        if (IsExecutable(runningPath))
                        {
                            return runningPath;
                        }
                    }
                    catch
                    {
                        // Some packaged processes do not expose MainModule to this process.
                    }
                }
            }
        }

        foreach (var registryPath in FindRegistryCandidates())
        {
            if (IsExecutable(registryPath))
            {
                return registryPath;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] commonPaths =
        [
            Path.Combine(programFiles, "Tencent", "Weixin", "Weixin.exe"),
            Path.Combine(programFiles, "Tencent", "WeChat", "WeChat.exe"),
            Path.Combine(programFilesX86, "Tencent", "Weixin", "Weixin.exe"),
            Path.Combine(programFilesX86, "Tencent", "WeChat", "WeChat.exe"),
            Path.Combine(localAppData, "Tencent", "Weixin", "Weixin.exe"),
            Path.Combine(localAppData, "Tencent", "WeChat", "WeChat.exe")
        ];

        return commonPaths.FirstOrDefault(IsExecutable);
    }

    /// <summary>
    /// 启动一个新的微信进程。
    ///
    /// 当已经存在微信进程时，优先使用 EV 多聊同类思路：
    /// 短时修改微信单实例 Mutex 的 DACL，使新进程越过单实例检测；
    /// CreateProcessW 成功并给子进程留出初始化时间后立即恢复原 DACL。
    ///
    /// 此处不修改微信 EXE、不注入 DLL、不修改登录配置。
    /// </summary>
    public static WeChatLaunchResult StartWeChat(string executablePath)
    {
        if (!IsExecutable(executablePath))
        {
            throw new FileNotFoundException("微信客户端不存在。", executablePath);
        }

        var hasExistingWeChat = HasRunningWeChatProcess();
        if (hasExistingWeChat)
        {
            var bypassResult = TryStartWeChatWithMutexBypass(executablePath);
            if (bypassResult is not null)
            {
                return bypassResult;
            }
        }

        return StartWeChatProcess(
            executablePath,
            usedMultiInstanceBypass: false,
            strategy: hasExistingWeChat ? "CreateProcessW-Fallback" : "CreateProcessW-Normal");
    }

    public static void StartWhatsApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "whatsapp:",
                UseShellExecute = true
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App",
                UseShellExecute = true
            });
        }
    }

    // 多开策略的每个失败点都会让调用方悄悄回退到普通启动；记录下是哪一步失败，
    // 否则用户反馈“多开不生效”时无从判断原因。
    private static WeChatLaunchResult? LogBypassFailure(string stage)
    {
        DiagnosticLogService.WriteActivityEvent($"mutex-bypass-failed:{stage}");
        return null;
    }

    private static WeChatLaunchResult? TryStartWeChatWithMutexBypass(string executablePath)
    {
        IntPtr mutexHandle = IntPtr.Zero;
        IntPtr originalSecurityDescriptor = IntPtr.Zero;
        IntPtr originalDacl = IntPtr.Zero;
        IntPtr everyoneSid = IntPtr.Zero;
        IntPtr denyAcl = IntPtr.Zero;
        var daclChanged = false;

        try
        {
            // Existing WeChat should already own this named object. CreateMutexW opens
            // the existing object when present and gives us the handle needed for DACL work.
            mutexHandle = CreateMutexW(IntPtr.Zero, false, WeChatInstanceMutexName);
            if (mutexHandle == IntPtr.Zero)
            {
                return LogBypassFailure("CreateMutexW");
            }

            var getSecurityResult = GetSecurityInfo(
                mutexHandle,
                SeKernelObject,
                DaclSecurityInformation,
                out _,
                out _,
                out originalDacl,
                out _,
                out originalSecurityDescriptor);
            if (getSecurityResult != 0 || originalSecurityDescriptor == IntPtr.Zero)
            {
                return LogBypassFailure("GetSecurityInfo");
            }

            everyoneSid = Marshal.AllocHGlobal(SecurityMaxSidSize);
            uint sidSize = SecurityMaxSidSize;
            if (!CreateWellKnownSid(WinWorldSid, IntPtr.Zero, everyoneSid, ref sidSize))
            {
                return LogBypassFailure("CreateWellKnownSid");
            }

            var sidLength = GetLengthSid(everyoneSid);
            if (sidLength <= 0)
            {
                return LogBypassFailure("GetLengthSid");
            }

            // ACL header (8) + ACCESS_DENIED_ACE without SidStart payload (8)
            // + complete SID length. Add a little alignment room.
            var aclSize = checked((uint)(16 + sidLength + 8));
            denyAcl = Marshal.AllocHGlobal((int)aclSize);
            if (!InitializeAcl(denyAcl, aclSize, AclRevision))
            {
                return LogBypassFailure("InitializeAcl");
            }

            if (!AddAccessDeniedAce(denyAcl, AclRevision, MutexAllAccess, everyoneSid))
            {
                return LogBypassFailure("AddAccessDeniedAce");
            }

            var setSecurityResult = SetSecurityInfo(
                mutexHandle,
                SeKernelObject,
                DaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                denyAcl,
                IntPtr.Zero);
            if (setSecurityResult != 0)
            {
                return LogBypassFailure("SetSecurityInfo");
            }

            daclChanged = true;

            // EV 1.1.9 在修改 Mutex 后有短暂等待。保留一个较小但明确的窗口，
            // 避免 ACL 更新与新进程单实例检测发生竞态。
            Thread.Sleep(500);

            var result = StartWeChatProcess(
                executablePath,
                usedMultiInstanceBypass: true,
                strategy: "MutexDacl+CreateProcessW");

            // 给 Weixin.exe 足够时间越过非常早期的单实例检查，然后恢复系统对象。
            Thread.Sleep(1200);
            return result;
        }
        catch (Exception exception)
        {
            // 多开策略失败时由调用方回退到普通 CreateProcessW。
            return LogBypassFailure($"exception:{exception.GetType().Name}");
        }
        finally
        {
            if (daclChanged
                && mutexHandle != IntPtr.Zero
                && originalSecurityDescriptor != IntPtr.Zero)
            {
                try
                {
                    var restoreResult = SetSecurityInfo(
                        mutexHandle,
                        SeKernelObject,
                        DaclSecurityInformation,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        originalDacl,
                        IntPtr.Zero);
                    if (restoreResult != 0)
                    {
                        DiagnosticLogService.WriteActivityEvent($"mutex-bypass-restore-failed:{restoreResult}");
                    }
                }
                catch
                {
                    // Handle rights are fixed when opened; restore should normally succeed.
                    // Failure is non-fatal for JuChuang, and the mutex disappears when all
                    // WeChat handles close.
                }
            }

            if (denyAcl != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(denyAcl);
            }

            if (everyoneSid != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(everyoneSid);
            }

            if (originalSecurityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(originalSecurityDescriptor);
            }

            if (mutexHandle != IntPtr.Zero)
            {
                _ = CloseHandle(mutexHandle);
            }
        }
    }

    private static WeChatLaunchResult StartWeChatProcess(
        string executablePath,
        bool usedMultiInstanceBypass,
        string strategy)
    {
        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>()
        };

        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (!CreateProcessW(
                executablePath,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"无法启动微信（{strategy}）。");
        }

        try
        {
            return new WeChatLaunchResult(
                true,
                unchecked((int)processInformation.dwProcessId),
                usedMultiInstanceBypass,
                strategy);
        }
        finally
        {
            if (processInformation.hThread != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.hThread);
            }

            if (processInformation.hProcess != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.hProcess);
            }
        }
    }

    private static bool HasRunningWeChatProcess()
    {
        foreach (var processName in WeChatProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            if (processes.Length == 0)
            {
                continue;
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<string> FindRegistryCandidates()
    {
        string[] subKeys =
        [
            @"Software\Tencent\Weixin",
            @"Software\Tencent\WeChat"
        ];

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var subKey in subKeys)
                {
                    RegistryKey? key = null;
                    try
                    {
                        key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKey);
                        if (key is null)
                        {
                            continue;
                        }

                        foreach (var valueName in key.GetValueNames())
                        {
                            if (key.GetValue(valueName) is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
                            {
                                continue;
                            }

                            var value = Environment.ExpandEnvironmentVariables(rawValue.Trim('"'));
                            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                yield return value;
                            }
                            else
                            {
                                yield return Path.Combine(value, "Weixin.exe");
                                yield return Path.Combine(value, "WeChat.exe");
                            }
                        }
                    }
                    finally
                    {
                        key?.Dispose();
                    }
                }
            }
        }
    }

    private static bool IsExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && File.Exists(path);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutexW(
        IntPtr lpMutexAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner,
        string lpName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAcl(IntPtr acl, uint aclLength, uint aclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddAccessDeniedAce(
        IntPtr acl,
        uint aceRevision,
        uint accessMask,
        IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWellKnownSid(
        int wellKnownSidType,
        IntPtr domainSid,
        IntPtr sid,
        ref uint cbSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetLengthSid(IntPtr sid);
}
