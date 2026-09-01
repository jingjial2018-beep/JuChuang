using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;
using JuChuang.Models;
using JuChuang.Services;

if (args.Length == 7 && args[0].Equals("--crop", StringComparison.OrdinalIgnoreCase))
{
    using var source = new Bitmap(args[1]);
    var cropBounds = new Rectangle(
        int.Parse(args[2]),
        int.Parse(args[3]),
        int.Parse(args[4]),
        int.Parse(args[5]));
    using var crop = source.Clone(cropBounds, source.PixelFormat);
    var scale = double.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
    var result = BadgeDetector.DetectBitmap(crop, ClientKind.WhatsApp, scale);
    var diagnostic = BadgeDetector.DiagnoseBitmap(crop, ClientKind.WhatsApp, scale);
    Console.WriteLine($"crop={crop.Width}x{crop.Height}, dpiScale={scale:F2}");
    Console.WriteLine($"result=({result.HasAlert},{result.Number},{result.Confidence})");
    Console.WriteLine(diagnostic.Trace);
    return result.HasAlert && result.Number > 0 ? 0 : 1;
}

var failures = new List<string>();

var foregroundPolicySamples = new (string Name, bool Suppressed, IntPtr Foreground,
    IntPtr Manager, IntPtr Hosted, bool Expected)[]
{
    ("Manager foreground promotes overlay", false, (IntPtr)10, (IntPtr)10, (IntPtr)20, true),
    ("Hosted client foreground promotes overlay", false, (IntPtr)20, (IntPtr)10, (IntPtr)20, true),
    ("Chrome foreground never promotes overlay", false, (IntPtr)30, (IntPtr)10, (IntPtr)20, false),
    ("Modal suppression blocks promotion", true, (IntPtr)10, (IntPtr)10, (IntPtr)20, false),
    ("Missing foreground blocks promotion", false, IntPtr.Zero, (IntPtr)10, (IntPtr)20, false),
};

foreach (var sample in foregroundPolicySamples)
{
    var actual = WindowActivityPolicy.ShouldPromoteHostedWindow(
        sample.Suppressed,
        sample.Foreground,
        sample.Manager,
        sample.Hosted);
    var passed = actual == sample.Expected;
    Console.WriteLine($"{sample.Name}: expected={sample.Expected}, actual={actual} => {(passed ? "PASS" : "FAIL")}");
    if (!passed)
    {
        failures.Add(sample.Name);
    }
}

var samples = new (string Name, ClientKind Kind, Bitmap Bitmap, double DpiScale, bool HasAlert, int Number)[]
{
    ("WeChat 1", ClientKind.WeChat, CreateWeChatSample("1", 32), 1.0, true, 1),
    ("WeChat 15", ClientKind.WeChat, CreateWeChatSample("15", 42), 1.0, true, 15),
    ("WeChat 31", ClientKind.WeChat, CreateWeChatSample("31", 42), 1.0, true, 31),
    ("WeChat none", ClientKind.WeChat, CreateBlank(1130, 762), 1.0, false, 0),
    ("WhatsApp chats 5", ClientKind.WhatsApp, CreateWhatsAppChatSample("5"), 1.0, true, 5),
    ("WhatsApp calls only", ClientKind.WhatsApp, CreateWhatsAppDistractor(), 1.0, false, 0),
    ("WhatsApp tall chats + calls", ClientKind.WhatsApp, CreateWhatsAppTallSample(), 1.0, true, 1),
    ("WhatsApp tall calls only", ClientKind.WhatsApp, CreateWhatsAppTallDistractor(), 1.0, false, 0),
    ("WhatsApp 150% DPI", ClientKind.WhatsApp, CreateWhatsAppScaledSample(1.5), 1.5, true, 1),
};

foreach (var sample in samples)
{
    using (sample.Bitmap)
    {
        var result = BadgeDetector.DetectBitmap(sample.Bitmap, sample.Kind, sample.DpiScale);
        var passed = result.HasAlert == sample.HasAlert
                     && (!sample.HasAlert || result.Number == sample.Number)
                     && (!sample.HasAlert || result.Confidence == BadgeConfidenceLevel.High);
        Console.WriteLine(
            $"{sample.Name}: expected=({sample.HasAlert},{sample.Number}), " +
            $"actual=({result.HasAlert},{result.Number},{result.Confidence}) => {(passed ? "PASS" : "FAIL")}");
        if (!passed)
        {
            failures.Add(sample.Name);
            var diagnostic = BadgeDetector.DiagnoseBitmap(sample.Bitmap, sample.Kind);
            Console.WriteLine(diagnostic.Trace);
        }
    }
}

using (var benchmark = CreateWeChatSample("15", 42))
{
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < 50; index++)
    {
        _ = BadgeDetector.DetectBitmap(benchmark, ClientKind.WeChat);
    }
    stopwatch.Stop();
    Console.WriteLine($"50 次限定区域识别耗时 {stopwatch.ElapsedMilliseconds} ms");
}

return failures.Count == 0 ? 0 : 1;

static Bitmap CreateBlank(int width, int height)
{
    var bitmap = new Bitmap(width, height);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(247, 247, 247));
    return bitmap;
}

static Bitmap CreateWeChatSample(string text, int badgeWidth)
{
    var bitmap = CreateBlank(1130, 762);
    using var graphics = Graphics.FromImage(bitmap);
    DrawBadge(graphics, new Rectangle(75, 197, badgeWidth, 32), Color.FromArgb(250, 81, 81), text);
    return bitmap;
}

static Bitmap CreateWhatsAppDistractor()
{
    var bitmap = CreateBlank(1170, 749);
    using var graphics = Graphics.FromImage(bitmap);
    // 聊天入口本身没有角标；下方通话入口即使有数字，也不应计为消息未读。
    DrawChatButton(graphics, 40, 110);
    DrawBadge(graphics, new Rectangle(72, 176, 40, 40), Color.FromArgb(0, 168, 107), "1", 18);
    return bitmap;
}

static Bitmap CreateWhatsAppChatSample(string text)
{
    var bitmap = CreateBlank(1170, 749);
    using var graphics = Graphics.FromImage(bitmap);
    DrawChatButton(graphics, 40, 110);
    DrawBadge(graphics, new Rectangle(72, 110, 40, 40), Color.FromArgb(0, 168, 107), text, 18);
    return bitmap;
}

static Bitmap CreateWhatsAppTallSample()
{
    var bitmap = CreateBlank(1800, 1500);
    using var graphics = Graphics.FromImage(bitmap);
    DrawChatButton(graphics, 40, 110);
    DrawBadge(graphics, new Rectangle(72, 110, 40, 40), Color.FromArgb(0, 168, 107), "1", 18);
    DrawBadge(graphics, new Rectangle(72, 176, 40, 40), Color.FromArgb(0, 168, 107), "1", 18);
    return bitmap;
}

static Bitmap CreateWhatsAppTallDistractor()
{
    var bitmap = CreateBlank(1800, 1500);
    using var graphics = Graphics.FromImage(bitmap);
    DrawChatButton(graphics, 40, 110);
    DrawBadge(graphics, new Rectangle(72, 176, 40, 40), Color.FromArgb(0, 168, 107), "1", 18);
    return bitmap;
}

static Bitmap CreateWhatsAppScaledSample(double scale)
{
    var bitmap = CreateBlank(2400, 1500);
    using var graphics = Graphics.FromImage(bitmap);
    DrawChatButton(graphics, (int)Math.Round(40 * scale), (int)Math.Round(110 * scale), scale);
    DrawBadge(
        graphics,
        new Rectangle(
            (int)Math.Round(72 * scale),
            (int)Math.Round(110 * scale),
            (int)Math.Round(40 * scale),
            (int)Math.Round(40 * scale)),
        Color.FromArgb(0, 168, 107),
        "1",
        (float)(18 * scale));
    DrawBadge(
        graphics,
        new Rectangle(
            (int)Math.Round(72 * scale),
            (int)Math.Round(176 * scale),
            (int)Math.Round(40 * scale),
            (int)Math.Round(40 * scale)),
        Color.FromArgb(0, 168, 107),
        "1",
        (float)(18 * scale));
    return bitmap;
}

static void DrawChatButton(Graphics graphics, int x, int y, double scale = 1.0)
{
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    using var background = new SolidBrush(Color.FromArgb(25, 25, 25));
    using var path = new GraphicsPath();
    var s = (float)scale;
    path.AddArc(x, y + 7 * s, 18 * s, 18 * s, 180, 90);
    path.AddArc(x + 22 * s, y + 7 * s, 18 * s, 18 * s, 270, 90);
    path.AddArc(x + 22 * s, y + 23 * s, 18 * s, 18 * s, 0, 90);
    path.AddArc(x, y + 23 * s, 18 * s, 18 * s, 90, 90);
    path.CloseFigure();
    graphics.FillPath(background, path);
    using var foreground = new Pen(Color.White, 3 * s);
    graphics.DrawLine(foreground, x + 10 * s, y + 19 * s, x + 29 * s, y + 19 * s);
    graphics.DrawLine(foreground, x + 10 * s, y + 27 * s, x + 24 * s, y + 27 * s);
}

static void DrawBadge(Graphics graphics, Rectangle bounds, Color color, string text, float fontSize = 15)
{
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    using var path = new GraphicsPath();
    var diameter = bounds.Height;
    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
    path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
    path.CloseFigure();
    using var background = new SolidBrush(color);
    graphics.FillPath(background, path);
    using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
    using var foreground = new SolidBrush(Color.White);
    using var format = new StringFormat
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoClip,
    };
    graphics.DrawString(text, font, foreground, bounds, format);
}
