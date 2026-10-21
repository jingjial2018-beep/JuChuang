using System.IO;
using System.Text.Json;

namespace JuChuang.Services;

public sealed class AppSettings
{
    public string? WeChatExecutablePath { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool EnableTaskbarFlash { get; set; } = true;

    public bool EnableToastNotifications { get; set; } = true;

    public bool EnableNotificationSound { get; set; } = true;

    public bool DndEnabled { get; set; }

    public int DndStartMinutes { get; set; } = 22 * 60;

    public int DndEndMinutes { get; set; } = 8 * 60;

    public bool IsDoNotDisturbActive(TimeOnly time)
    {
        if (!DndEnabled) return false;

        var now = time.Hour * 60 + time.Minute;
        var start = Math.Clamp(DndStartMinutes, 0, 1439);
        var end = Math.Clamp(DndEndMinutes, 0, 1439);
        if (start == end) return true;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    public double? SidebarWidth { get; set; }

    public Dictionary<string, string> AccountNamesByIdentity { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> AccountOrder { get; set; } = [];

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuChuang",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                ?? new AppSettings();
            settings.AccountNamesByIdentity = new Dictionary<string, string>(
                settings.AccountNamesByIdentity ?? [],
                StringComparer.OrdinalIgnoreCase);
            settings.AccountOrder = (settings.AccountOrder ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only profile should not stop the window manager from working.
        }
    }
}
