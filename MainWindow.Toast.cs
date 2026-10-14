using System.IO;
using System.Windows;
using CommunityToolkit.WinUI.Notifications;
using JuChuang.Models;
using WinForms = System.Windows.Forms;

namespace JuChuang;

public partial class MainWindow
{
    /// <summary>
    /// Native Windows toast: the account name is the title, never a duplicate JNest heading.
    /// Use the existing locally cached account avatar without reading chat content.
    /// </summary>
    private void ShowAccountNotification(ClientWindowEntry entry)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(entry.DisplayName)
                .AddText("收到新消息。");

            var avatar = ResolveNotificationAvatar(entry);
            if (avatar is not null)
            {
                builder.AddAppLogoOverride(new Uri(avatar), ToastGenericAppLogoCrop.Circle);
            }

            builder.Show();
        }
        catch (Exception ex)
        {
            // Notifications are secondary to reliable unread detection and taskbar flashing.
            DiagnosticLogService.WriteActivityEvent("toast-fallback: " + ex.GetType().Name, entry.ProcessId);
            _notifyIcon?.ShowBalloonTip(3000, entry.DisplayName,
                "收到新消息。", WinForms.ToolTipIcon.None);
        }
    }

    private static string? ResolveNotificationAvatar(ClientWindowEntry entry)
    {
        // WhatsApp currently has no independently verified account avatar mapping.
        // Never display an avatar from another account.
        if (entry.Kind == ClientKind.WeChat
            && !string.IsNullOrWhiteSpace(entry.AvatarPath)
            && File.Exists(entry.AvatarPath))
        {
            var extension = Path.GetExtension(entry.AvatarPath);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(entry.AvatarPath);
            }
        }

        // A PNG source works as the default image in Windows toast notifications.
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JuChuang", "Notifications");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "jnest-logo.png");
            if (!File.Exists(path))
            {
                var resource = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/app-logo.png"));
                using var input = resource?.Stream;
                if (input is null) return null;
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            return path;
        }
        catch
        {
            return null;
        }
    }
}
