using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Interop;
using JuChuang.Models;
using JuChuang.Services;

namespace JuChuang;

public partial class MainWindow
{
    private enum ExitChoice { Cancel, JNestOnly, All }

    private Window CreateExitDialog(string title, string message, bool showWarning = true)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var layout = ExitDialogLayout.Calculate(
            screen.WorkingArea.Width, screen.WorkingArea.Height, dpi.DpiScaleX, dpi.DpiScaleY);
        var width = layout.Width;
        var height = layout.Height;
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = width, Height = height, MinWidth = width, MaxWidth = width,
            MinHeight = height, MaxHeight = height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Background = Brushes.White, FontFamily = FontFamily,
        };
        var root = new DockPanel { Margin = new Thickness(18, 15, 18, 14), LastChildFill = true };
        dialog.Content = root;
        // Keep the button row visible while long names and translated text scroll.
        var lines = message.Split('\n', 2);
        var textGroup = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = Math.Min(365, Math.Max(1, width - 36)),
            Margin = new Thickness(0, 8, 0, 7)
        };
        var textScroll = new ScrollViewer
        {
            Content = textGroup,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false
        };
        root.Children.Add(textScroll);
        textGroup.Children.Add(new TextBlock
        {
            Text = lines[0], TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15, LineHeight = 23,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 50, 56)),
            Margin = new Thickness(0, 0, 0, 7)
        });
        if (lines.Length > 1)
        {
            textGroup.Children.Add(new TextBlock
            {
                Text = lines[1], TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5, LineHeight = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(45, 50, 56))
            });
        }
        return dialog;
    }

    // Mirrors AboutDialog.xaml / AboutConfirmStyle, including hover and pressed states.
    private static Button CreateExitButton(string label, bool destructive = false, bool filled = false)
    {
        var button = new Button
        {
            Content = label, Width = 112,
            Height = 40, Margin = new Thickness(0),
            Background = filled ? new SolidColorBrush(Color.FromRgb(255, 77, 79)) : new SolidColorBrush(destructive ? Colors.White : Color.FromRgb(248, 249, 251)),
            Foreground = filled ? Brushes.White : new SolidColorBrush(destructive ? Color.FromRgb(255, 77, 79) : Color.FromRgb(38, 48, 65)),
            BorderBrush = new SolidColorBrush(destructive ? Color.FromRgb(255, 77, 79) : Color.FromRgb(217, 222, 229)),
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
            new SolidColorBrush(filled ? Color.FromRgb(230, 57, 59) : destructive ? Color.FromRgb(255, 244, 244) : Color.FromRgb(238, 242, 246)), "Surface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(filled ? Color.FromRgb(204, 43, 45) : destructive ? Color.FromRgb(255, 231, 231) : Color.FromRgb(227, 233, 240)), "Surface"));
        template.Triggers.Add(pressed);
        button.Template = template;
        return button;
    }

    private bool ShowExitConfirmation(string name, int pid, int affected)
    {
        var dialog = CreateExitDialog("退出客户端",
            $"确定退出“{name}”吗？\n将结束 {name} 进程并退出登录，\n未保存的内容可能丢失。"
            + (affected > 1 ? $"\n该进程关联的 {affected} 个账号项都会退出。" : ""), showWarning: false);
        var result = false;
        var stacked = ExitDialogLayout.StackButtons(dialog.Width);
        var buttons = new StackPanel { Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var reconsider = CreateExitButton("我再想想");
        reconsider.Margin = stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 16, 0);
        reconsider.Click += (_, _) => dialog.Close();
        var confirm = CreateExitButton("确认退出", destructive: true, filled: true);
        reconsider.Width = confirm.Width = ExitDialogLayout.ButtonWidth(dialog.Width);
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        buttons.Children.Add(reconsider);
        buttons.Children.Add(confirm);
        ShowHostedModalDialog(dialog);
        return result;
    }

    private ExitChoice ShowExitChoice()
    {
        var dialog = CreateExitDialog("退出 JNest",
            "请选择退出方式。\n仅退出 JNest：恢复客户端独立窗口并保持运行。\n全部退出：结束当前管理的微信与 WhatsApp 进程。", showWarning: false);
        var result = ExitChoice.Cancel;
        var stacked = ExitDialogLayout.StackButtons(dialog.Width);
        var buttons = new StackPanel { Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var jnest = CreateExitButton("仅退出 JNest");
        var all = CreateExitButton("全部退出", destructive: true);
        jnest.Width = all.Width = ExitDialogLayout.ButtonWidth(dialog.Width);
        all.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(16, 0, 0, 0);
        jnest.Click += (_, _) => { result = ExitChoice.JNestOnly; dialog.Close(); };
        // The exit-choice dialog itself is the confirmation; do not open another prompt.
        all.Click += (_, _) => { result = ExitChoice.All; dialog.Close(); };
        buttons.Children.Add(jnest); buttons.Children.Add(all);
        ShowHostedModalDialog(dialog);
        return result;
    }

    private void ShowExitResults(string message)
    {
        var dialog = CreateExitDialog("全部退出未完成", message);
        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        ((DockPanel)dialog.Content).Children.Insert(0, buttons);
        var confirm = CreateExitButton("确定");
        confirm.Width = ExitDialogLayout.ButtonWidth(dialog.Width);
        confirm.Click += (_, _) => dialog.Close();
        buttons.Children.Add(confirm);
        ShowHostedModalDialog(dialog);
    }

    private bool _allExitInProgress;
    private readonly HashSet<int> _exitingClientProcessIds = [];
    private readonly Dictionary<int, int> _processExitVersions = [];

    private int GetProcessExitVersion(int processId)
        => _processExitVersions.GetValueOrDefault(processId);

    private void MarkProcessExitRequested(int processId)
        => _processExitVersions[processId] = GetProcessExitVersion(processId) + 1;

    private bool IsProcessExiting(int processId)
        => _allExitInProgress || processId == _exitingClientProcessId
           || _exitingClientProcessIds.Contains(processId);

    private async Task<bool> TryExitAllHostedClientsAsync()
    {
        // Validate the entire set before terminating any PID: never infer ownership from PID alone.
        var groups = Entries.Where(e => ExitTargetPolicy.IsManaged(e.IsAttached, e.IsManuallyDetached)
                && e.ProcessId > 0)
            .GroupBy(e => e.ProcessId).ToArray();
        var targets = groups.Select(group => group.First()).ToArray();
        ShowTemporaryStatus($"正在退出 {targets.Length} 个客户端进程…", TimeSpan.FromHours(1));
        var validated = new List<Process>();
        try
        {
            foreach (var entry in targets)
            {
                if (!entry.ProcessStartUtcTicks.HasValue
                    || !WindowBelongsToProcess(entry.Handle, entry.ProcessId)
                    || TryGetProcessStartUtcTicks(entry.ProcessId) != entry.ProcessStartUtcTicks)
                    throw new InvalidOperationException($"无法验证“{entry.DisplayName}”（PID {entry.ProcessId}）");
                var process = Process.GetProcessById(entry.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.ProcessStartUtcTicks.Value
                    || !WindowBelongsToProcess(entry.Handle, entry.ProcessId))
                {
                    process.Dispose();
                    throw new InvalidOperationException($"“{entry.DisplayName}”进程状态已变化（PID {entry.ProcessId}）");
                }
                validated.Add(process);
            }
            foreach (var process in validated)
            {
                _exitingClientProcessIds.Add(process.Id);
                MarkProcessExitRequested(process.Id);
            }
            var exitTask = ExitBatchService.RunAsync(
                validated.Select(process => (IExitProcessTarget)new SystemExitProcessTarget(process)).ToArray(),
                TimeSpan.FromSeconds(3));
            // ExitBatchService now bounds both the native request and confirmation phases.
            var results = await exitTask;
            foreach (var result in results)
            {
                ReconnectDiagnostics.Write($"exit all pid={result.ProcessId} requested={result.Requested} confirmed={result.Confirmed} error={result.Error ?? "none"}");
            }
            var requested = results.Count(result => result.Requested);
            var confirmed = results.Count(result => result.Confirmed);
            ReconnectDiagnostics.Write($"exit all result total={targets.Length} requested={requested} confirmed={confirmed}");
            if (confirmed == targets.Length) return true;
            var lines = groups.Select((group, index) =>
            {
                var result = results[index];
                var names = string.Join("、", group.Select(entry => entry.DisplayName).Distinct());
                var status = ExitResultPresentation.Status(result);
                return $"{names}：{status}";
            });
            ClearTemporaryStatus();
            ShowExitResults($"共 {targets.Length} 个进程，已确认退出 {confirmed} 个。\n"
                + string.Join("\n", lines)
                + "\nJNest 将保持运行。请求未确认的进程可能仍在完成退出，请先检查状态再重试。");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or ArgumentException or System.UnauthorizedAccessException)
        {
            ReconnectDiagnostics.Write($"exit all validation failed total={targets.Length} error={ex.GetType().Name} {ex.Message}");
            ClearTemporaryStatus();
            ShowOwnedMessageBox(
                $"全部退出未完成：进程身份验证失败，未请求结束任何进程。\nJNest 将保持运行，请检查客户端状态。\n" + ex.Message,
                "全部退出未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            ClearTemporaryStatus();
            _exitingClientProcessIds.Clear();
            foreach (var process in validated) process.Dispose();
        }
    }
}
