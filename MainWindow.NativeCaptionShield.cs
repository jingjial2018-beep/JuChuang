using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using JuChuang.Models;

namespace JuChuang;

public partial class MainWindow
{
    // Experimental beta-only click shield. It does not inject into native clients,
    // subclass foreign HWNDs, or change ExternalWindowHost's overlay strategy.
    // WPF Popup owns a separate HWND above the embedded client and swallows mouse
    // input in the three caption-button cells. Native clients retain their UI.
    private const double NativeCaptionButtonShieldWidth = 138;
    private const double NativeCaptionButtonShieldHeight = 34;
    private readonly DispatcherTimer _nativeCaptionShieldTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private Popup? _nativeCaptionShield;

    private void StartNativeCaptionShield()
    {
        if (_nativeCaptionShield is not null) return;
        var surface = new Border
        {
            Width = NativeCaptionButtonShieldWidth,
            Height = NativeCaptionButtonShieldHeight,
            // Fully transparent layered windows can become mouse transparent on
            // Windows. Nearly invisible nonzero alpha keeps the hit-test surface.
            Background = new SolidColorBrush(Color.FromArgb(2, 245, 247, 250)),
            IsHitTestVisible = true,
            Focusable = false,
            ToolTip = "窗口已接入 JNest，请使用 JNest 的窗口控制按钮"
        };
        _nativeCaptionShield = new Popup
        {
            Child = surface,
            AllowsTransparency = true,
            StaysOpen = true,
            PopupAnimation = PopupAnimation.None,
            PlacementTarget = WindowHost,
            Placement = PlacementMode.Relative,
            VerticalOffset = 0
        };
        _nativeCaptionShieldTimer.Tick += (_, _) => RefreshNativeCaptionShield();
        _nativeCaptionShieldTimer.Start();
        RefreshNativeCaptionShield();
    }

    private void RefreshNativeCaptionShield()
    {
        var shield = _nativeCaptionShield;
        if (shield is null) return;
        var show = !_isClosing && !_isPreviewMode && IsVisible
            && WindowState != WindowState.Minimized
            && SelectedEntry is { IsAttached: true, Handle: var handle }
            && handle != IntPtr.Zero
            && NativeMethods.IsWindow(handle)
            && WindowHost.IsReady && WindowHost.IsVisible
            && WindowHost.ActualWidth >= NativeCaptionButtonShieldWidth + 20
            && (IsActive || WindowHost.IsManagerContextForeground);

        if (!show)
        {
            shield.IsOpen = false;
            return;
        }

        var x = Math.Max(0, WindowHost.ActualWidth - NativeCaptionButtonShieldWidth);
        if (Math.Abs(shield.HorizontalOffset - x) > 0.25)
            shield.HorizontalOffset = x;
        if (!shield.IsOpen) shield.IsOpen = true;
    }

    private void StopNativeCaptionShield()
    {
        _nativeCaptionShieldTimer.Stop();
        if (_nativeCaptionShield is { } shield)
        {
            shield.IsOpen = false;
            shield.Child = null;
            _nativeCaptionShield = null;
        }
    }
}
