using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace JuChuang.Services;

public static class ClientLauncher
{
    private static readonly string[] WeChatProcessNames = ["Weixin", "WeChat"];

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

    public static void StartWeChat(string executablePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
        });
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
}
