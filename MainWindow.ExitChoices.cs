using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private enum ExitChoice { Cancel, JNestOnly, All }

    private Window CreateExitDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420, Height = 212, MinWidth = 420, MaxWidth = 420,
            MinHeight = 212, MaxHeight = 212,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Background = Brushes.White, FontFamily = FontFamily,
        };
        var root = new DockPanel { Margin = new Thickness(18, 15, 18, 14), LastChildFill = true };
        dialog.Content = root;
        var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 5) };
        DockPanel.SetDock(body, Dock.Top);
        root.Children.Add(body);
        // Same visual footprint as the previous Windows message-box warning icon.
        body.Children.Add(new TextBlock
        {
            Text = "⚠", FontSize = 32, Foreground = new SolidColorBrush(Color.FromRgb(212, 154, 54)),
            Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        });
        body.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, Width = 320,
            FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(45, 50, 56)),
            VerticalAlignment = VerticalAlignment.Center
        });
        return dialog;
    }

    private static Button CreateExitButton(string label, bool primary = false)
        => new()
        {
            Content = label, MinWidth = 96, Height = 32, Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(primary ? Color.FromRgb(48, 52, 59) : Color.FromRgb(241, 243, 246)),
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(48, 52, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 214, 221))
        };

    private bool ShowExitConfirmation(string name, int pid, int affected)
    {
        var dialog = CreateExitDialog("退出客户端",
            $"确定退出“{name}”吗？\n将结束对应客户端进程（PID {pid}），未保存的内容可能丢失。"
            + (affected > 1 ? $"\n该进程关联的 {affected} 个账号项都会退出。" : ""));
        var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var cancel = CreateExitButton("取消");
        var confirm = CreateExitButton("确认退出", true);
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        dialog.ShowDialog();
        return result;
    }

    private ExitChoice ShowExitChoice()
    {
        var dialog = CreateExitDialog("退出 JNest",
            "请选择退出方式。\n仅退出 JNest：恢复客户端独立窗口并保持运行。\n全部退出：结束当前管理的微信与 WhatsApp 进程。");
        var result = ExitChoice.Cancel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var cancel = CreateExitButton("取消");
        var jnest = CreateExitButton("仅退出 JNest");
        var all = CreateExitButton("全部退出", true);
        cancel.Click += (_, _) => dialog.Close();
        jnest.Click += (_, _) => { result = ExitChoice.JNestOnly; dialog.Close(); };
        all.Click += (_, _) =>
        {
            var confirm = ShowOwnedMessageBox(
                "将结束 JNest 当前管理的所有微信和 WhatsApp 客户端进程。未保存的内容可能丢失。确定全部退出吗？",
                "确认全部退出", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            result = ExitChoice.All; dialog.Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(jnest); buttons.Children.Add(all);
        dialog.ShowDialog();
        return result;
    }

    private bool TryExitAllHostedClients()
    {
        // Validate the entire set before terminating any PID: never infer ownership from PID alone.
        var targets = Entries.Where(e => e.IsAttached && e.ProcessId > 0)
            .GroupBy(e => e.ProcessId).Select(group => group.First()).ToArray();
        var validated = new List<Process>();
        try
        {
            foreach (var entry in targets)
            {
                if (!entry.ProcessStartUtcTicks.HasValue
                    || !WindowBelongsToProcess(entry.Handle, entry.ProcessId)
                    || TryGetProcessStartUtcTicks(entry.ProcessId) != entry.ProcessStartUtcTicks)
                    throw new InvalidOperationException($"无法验证进程 PID {entry.ProcessId}");
                var process = Process.GetProcessById(entry.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.ProcessStartUtcTicks.Value
                    || !WindowBelongsToProcess(entry.Handle, entry.ProcessId))
                {
                    process.Dispose();
                    throw new InvalidOperationException($"进程状态已变化 PID {entry.ProcessId}");
                }
                validated.Add(process);
            }
            foreach (var process in validated)
            {
                process.Kill(entireProcessTree: false);
                ReconnectDiagnostics.Write($"exit all requested pid={process.Id}");
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or ArgumentException or System.UnauthorizedAccessException)
        {
            ReconnectDiagnostics.Write($"exit all stopped: {ex.GetType().Name} {ex.Message}");
            ShowOwnedMessageBox(
                "部分客户端无法安全结束。JNest 将保持运行，请先检查客户端状态。\n" + ex.Message,
                "全部退出未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            foreach (var process in validated) process.Dispose();
        }
    }
}
