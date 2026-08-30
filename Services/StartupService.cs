using System.Diagnostics;
using Microsoft.Win32;

namespace JuChuang.Services;

/// <summary>
/// Manages the "launch at Windows startup" preference through the current user's
/// Run registry key. Failures are swallowed so restricted accounts still work.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JuChuang";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    key.SetValue(ValueName, $"\"{executablePath}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry writes can fail under a restricted account; ignore.
        }
    }
}
