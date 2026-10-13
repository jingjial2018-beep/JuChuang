using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using JuChuang.Models;

namespace JuChuang;

public partial class MainWindow
{
    private static readonly ImageSource UnreadTaskbarOverlay = CreateUnreadTaskbarOverlay();

    private void UpdateTaskbarUnreadOverlay()
    {
        if (_isClosing) return;

        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = Entries.Any(entry =>
            entry.AlertDisplayMode != AlertDisplayMode.None)
            ? UnreadTaskbarOverlay : null;
    }

    private static ImageSource CreateUnreadTaskbarOverlay()
    {
        var circle = new EllipseGeometry(new Point(8, 8), 6, 6);
        var drawing = new GeometryDrawing(
            Brushes.OrangeRed,
            new Pen(Brushes.White, 1.5),
            circle);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}

