using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JuChuang;

// Menu icon images are embedded as PNG bytes to preserve supplied artwork
// (rather than approximating them with hand-written WPF Path geometry).
public static partial class MenuIconAssets
{
    private static ImageSource Decode(string data)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
