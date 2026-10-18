using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
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
        var body = new Grid { Margin = new Thickness(0, 8, 0, 7) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DockPanel.SetDock(body, Dock.Top);
        root.Children.Add(body);
        var warning = new TextBlock
        {
            Text = "⚠", FontSize = 32, Foreground = new SolidColorBrush(Color.FromRgb(212, 154, 54)),
            Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(warning, 0);
        body.Children.Add(warning);
        var messageText = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            FontSize = 13, LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 50, 56)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(messageText, 1);
        body.Children.Add(messageText);
        return dialog;
    }

    // Mirrors AboutDialog.xaml / AboutConfirmStyle, including hover and pressed states.
    private static Button CreateExitButton(string label)
    {
        var button = new Button
        {
            Content = label, Width = label == "仅退出 JNest" ? 132 : 112,
            Height = 40, Margin = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(248, 249, 251)),
            Foreground = new SolidColorBrush(Color.FromRgb(38, 48, 65)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 222, 229)),
            BorderThickness = new Thickness(1),
            FontSize = 13, FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand, FocusVisualStyle = null
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Surface";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.EffectProperty, new DropShadowEffect
        {
            Color = Color.FromRgb(51, 65, 85),
            Opacity = 0.12, BlurRadius = 5, ShadowDepth = 1
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(238, 242, 246)), "Surface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(227, 233, 240)), "Surface"));
        template.Triggers.Add(pressed);
        button.Template = template;
        return button;
    }

    private bool ShowExitConfirmation(string name, int pid, int affected)
    {
        var dialog = CreateExitDialog("退出客户端",
            $"确定退出“{name}”吗？\n将结束对应客户端进程（PID {pid}），未保存的内容可能丢失。"
            + (affected > 1 ? $"\n该进程关联的 {affected} 个账号项都会退出。" : ""));
        var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var confirm = CreateExitButton("确认退出");
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        buttons.Children.Add(confirm);
        dialog.ShowDialog();
        return result;
    }

    private ExitChoice ShowExitChoice()
    {
        var dialog = CreateExitDialog("退出 JNest",
            "请选择退出方式。\n仅退出 JNest：恢复客户端独立窗口并保持运行。\n全部退出：结束当前管理的微信与 WhatsApp 进程。");
        var result = ExitChoice.Cancel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var jnest = CreateExitButton("仅退出 JNest");
        var all = CreateExitButton("全部退出");
        all.Margin = new Thickness(20, 0, 0, 0);
        jnest.Click += (_, _) => { result = ExitChoice.JNestOnly; dialog.Close(); };
        // The exit-choice dialog itself is the confirmation; do not open another prompt.
        all.Click += (_, _) => { result = ExitChoice.All; dialog.Close(); };
        buttons.Children.Add(jnest); buttons.Children.Add(all);
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
