using System.Windows.Input;

namespace JuChuang.Services;

/// <summary>
/// Sidebar account switching uses WPF ListBox's normal left-button and
/// keyboard selection. Right-click is not an account-selection gesture.
/// </summary>
internal static class AccountSelectionInputPolicy
{
    internal static bool ShouldSuppress(MouseButton button) => button == MouseButton.Right;
}
