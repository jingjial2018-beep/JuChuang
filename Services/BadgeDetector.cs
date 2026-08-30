using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using JuChuang.Models;

namespace JuChuang.Services;

/// <summary>
/// 只分析客户端已经绘制出来的导航角标，不读取数据库或聊天内容。
/// </summary>
internal sealed class BadgeDetector
{
    private const int NormalizedWidth = 20;
    private const int NormalizedHeight = 28;
    private static readonly Lazy<IReadOnlyDictionary<char, GlyphTemplate[]>> Templates =
        new(CreateTemplates, LazyThreadSafetyMode.ExecutionAndPublication);

    public BadgeResult? Detect(IntPtr hwnd, ClientKind kind)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)
            || !NativeMethods.GetWindowRect(hwnd, out var rect)
            || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        try
        {
            using var bitmap = Capture(hwnd, rect.Width, rect.Height);
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            var dpiScale = dpi > 0 ? Math.Clamp(dpi / 96.0, 0.75, 4.0) : 1.0;
            return bitmap is null ? null : AnalyzeBitmap(bitmap, kind, dpiScale, null);
        }
        catch
        {
            // 消息提示是辅助功能；截图或识别失败时不能影响窗口管理主流程。
            return null;
        }
    }

    internal static BadgeResult DetectBitmap(Bitmap bitmap, ClientKind kind)
        => AnalyzeBitmap(bitmap, kind, 1.0, null);

    internal static BadgeResult DetectBitmap(Bitmap bitmap, ClientKind kind, double dpiScale)
        => AnalyzeBitmap(bitmap, kind, dpiScale, null);

    internal static (BadgeResult Result, string Trace) DiagnoseBitmap(Bitmap bitmap, ClientKind kind)
        => DiagnoseBitmap(bitmap, kind, 1.0);

    internal static (BadgeResult Result, string Trace) DiagnoseBitmap(
        Bitmap bitmap,
        ClientKind kind,
        double dpiScale)
    {
        var trace = new StringBuilder();
        var result = AnalyzeBitmap(bitmap, kind, dpiScale, message => trace.AppendLine(message));
        return (result, trace.ToString());
    }

    private static BadgeResult AnalyzeBitmap(
        Bitmap bitmap,
        ClientKind kind,
        double dpiScale,
        Action<string>? trace)
    {
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return DetectPixels(pixels, bitmap.Width, bitmap.Height, stride, kind, dpiScale, trace);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap? Capture(IntPtr hwnd, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 5000 || height > 5000)
        {
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                {
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private static BadgeResult DetectPixels(
        byte[] pixels,
        int width,
        int height,
        int stride,
        ClientKind kind,
        double dpiScale,
        Action<string>? trace)
    {
        // 只扫描左侧导航栏的聊天入口。这样不会把会话列表、通话页或归档页的
        // 角标误当成账号总未读数，也大幅减少截图识别开销。
        var region = kind == ClientKind.WeChat
            ? RelativeRegion.Create(width, height, 0.025, 0.14, 0.12, 0.38)
            : RelativeRegion.CreateLogicalPixels(width, height, dpiScale, 18, 180, 38, 235);

        var candidates = FindBadgeCandidates(pixels, width, height, stride, region, kind, dpiScale);
        trace?.Invoke($"candidates={candidates.Count}");
        if (candidates.Count == 0)
        {
            return BadgeResult.NoBadge;
        }

        // 左侧聊天导航栏内一般只会存在一个总未读角标；若动画帧中出现多个，
        // 优先选择尺寸、填充率和椭圆比例最接近角标的候选。
        BadgeBox? selectedBadge = kind == ClientKind.WhatsApp
            ? SelectWhatsAppChatBadge(candidates, pixels, width, height, stride, dpiScale, trace)
            : candidates.MaxBy(ScoreCandidate);
        if (!selectedBadge.HasValue)
        {
            trace?.Invoke("no candidate belongs to the WhatsApp chat entry");
            return BadgeResult.NoBadge;
        }

        var badge = selectedBadge.Value;
        trace?.Invoke($"badge=({badge.Left},{badge.Top},{badge.Width},{badge.Height})");
        var digitMask = ExtractDigitMask(pixels, width, height, stride, badge, kind);
        var segments = FindCharacterSegments(digitMask, badge.Width, badge.Height);
        trace?.Invoke($"segments={string.Join(';', segments)}");
        if (segments.Count == 0)
        {
            return BadgeResult.Dot;
        }

        // WhatsApp 在 100% 缩放下会把单个“1”绘制成非常窄的竖线，通用字体
        // 模板容易把它误判成“+”。角标中只有一个细长笔画时可无歧义地判为 1。
        if (kind == ClientKind.WhatsApp
            && segments.Count == 1
            && IsNarrowWhatsAppOne(segments[0], badge))
        {
            trace?.Invoke("narrow WhatsApp glyph resolved as 1");
            return new BadgeResult(true, 1, BadgeConfidenceLevel.High);
        }

        var recognized = new List<CharacterMatch>(Math.Min(segments.Count, 3));
        foreach (var segment in segments.Take(3))
        {
            var normalized = NormalizeMask(digitMask, badge.Width, badge.Height, segment);
            recognized.Add(RecognizeCharacter(normalized));
        }
        trace?.Invoke($"matches={string.Join(';', recognized)}");

        if (recognized.Any(match => match.Character == '\0'))
        {
            return BadgeResult.Dot;
        }

        var characters = recognized.Select(match => match.Character).ToArray();
        var plusIndex = Array.IndexOf(characters, '+');
        if (plusIndex >= 0 && plusIndex != characters.Length - 1)
        {
            return BadgeResult.Dot;
        }

        IEnumerable<char> digitCharacters = plusIndex >= 0
            ? characters.Take(plusIndex)
            : characters;
        var digitText = new string(digitCharacters.ToArray());
        if (digitText.Length == 0 || !int.TryParse(digitText, out var number) || number <= 0)
        {
            return BadgeResult.Dot;
        }

        // 99+ 在界面统一显示为 99+；内部使用 100 作为上限标记。
        if (plusIndex >= 0 || number > 99)
        {
            number = 100;
        }

        var highConfidence = recognized.All(match =>
            match.Score >= 0.82 || (match.Score >= 0.68 && match.Margin >= 0.015));
        return highConfidence
            ? new BadgeResult(true, number, BadgeConfidenceLevel.High)
            : BadgeResult.Dot;
    }

    private static List<BadgeBox> FindBadgeCandidates(
        byte[] pixels,
        int width,
        int height,
        int stride,
        RelativeRegion region,
        ClientKind kind,
        double dpiScale)
    {
        var scanWidth = region.Right - region.Left + 1;
        var scanHeight = region.Bottom - region.Top + 1;
        if (scanWidth <= 0 || scanHeight <= 0)
        {
            return [];
        }

        var badgeMask = new bool[scanWidth * scanHeight];
        for (var y = region.Top; y <= region.Bottom; y++)
        {
            for (var x = region.Left; x <= region.Right; x++)
            {
                ReadPixel(pixels, stride, x, y, out var red, out var green, out var blue);
                badgeMask[(y - region.Top) * scanWidth + x - region.Left] =
                    IsBadgeColor(red, green, blue, kind);
            }
        }

        var visited = new bool[badgeMask.Length];
        var candidates = new List<BadgeBox>(4);
        var minHeight = kind == ClientKind.WhatsApp
            ? Math.Max(8, (int)Math.Round(12 * dpiScale))
            : Math.Max(8, (int)Math.Round(height * 0.012));
        var maxHeight = kind == ClientKind.WhatsApp
            ? Math.Max(minHeight, (int)Math.Round(58 * dpiScale))
            : Math.Max(minHeight, (int)Math.Round(height * 0.09));
        var maxWidth = kind == ClientKind.WhatsApp
            ? Math.Max(minHeight, (int)Math.Round(120 * dpiScale))
            : Math.Max(minHeight, (int)Math.Round(height * 0.18));

        for (var localY = 0; localY < scanHeight; localY++)
        {
            for (var localX = 0; localX < scanWidth; localX++)
            {
                var start = localY * scanWidth + localX;
                if (visited[start] || !badgeMask[start])
                {
                    continue;
                }

                var stack = new Stack<int>();
                stack.Push(start);
                visited[start] = true;
                var minX = localX;
                var maxX = localX;
                var minY = localY;
                var maxY = localY;
                var count = 0;

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    var currentY = current / scanWidth;
                    var currentX = current % scanWidth;
                    count++;
                    minX = Math.Min(minX, currentX);
                    maxX = Math.Max(maxX, currentX);
                    minY = Math.Min(minY, currentY);
                    maxY = Math.Max(maxY, currentY);

                    for (var offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (var offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            var nextX = currentX + offsetX;
                            var nextY = currentY + offsetY;
                            if (nextX < 0 || nextX >= scanWidth || nextY < 0 || nextY >= scanHeight)
                            {
                                continue;
                            }

                            var next = nextY * scanWidth + nextX;
                            if (!visited[next] && badgeMask[next])
                            {
                                visited[next] = true;
                                stack.Push(next);
                            }
                        }
                    }
                }

                var candidateWidth = maxX - minX + 1;
                var candidateHeight = maxY - minY + 1;
                var aspect = (double)candidateWidth / candidateHeight;
                var fill = (double)count / (candidateWidth * candidateHeight);
                if (candidateHeight >= minHeight
                    && candidateHeight <= maxHeight
                    && candidateWidth <= maxWidth
                    && aspect is >= 0.65 and <= 3.20
                    && fill >= 0.30)
                {
                    candidates.Add(new BadgeBox(
                        region.Left + minX,
                        region.Top + minY,
                        candidateWidth,
                        candidateHeight,
                        count,
                        fill));
                }
            }
        }

        return candidates;
    }

    private static BadgeBox? SelectWhatsAppChatBadge(
        IReadOnlyList<BadgeBox> candidates,
        byte[] pixels,
        int width,
        int height,
        int stride,
        double dpiScale,
        Action<string>? trace)
    {
        // WhatsApp 的聊天入口和通话入口都可能显示相同的绿色数字角标。
        // 聊天入口左侧是实心深色按钮，而通话入口只有细线电话图标；用这一局部
        // 特征锁定聊天角标，可避免窗口变高后把通话数当作未读消息数。
        var matches = new List<(BadgeBox Badge, double DarkDensity)>();
        foreach (var candidate in candidates)
        {
            var centerX = candidate.Left + candidate.Width / 2;
            var centerY = candidate.Top + candidate.Height / 2;
            var probeLeft = centerX - (int)Math.Round(54 * dpiScale);
            var probeRight = centerX - (int)Math.Round(7 * dpiScale);
            var probeTop = centerY - (int)Math.Round(15 * dpiScale);
            var probeBottom = centerY + (int)Math.Round(27 * dpiScale);
            var density = MeasureDarkDensity(
                pixels,
                width,
                height,
                stride,
                probeLeft,
                probeTop,
                probeRight,
                probeBottom);
            trace?.Invoke(
                $"whatsapp candidate=({candidate.Left},{candidate.Top},{candidate.Width},{candidate.Height}), " +
                $"chat-icon-density={density:F3}");
            if (density >= 0.10)
            {
                matches.Add((candidate, density));
            }
        }

        return matches
            .OrderByDescending(match => match.DarkDensity)
            .ThenBy(match => match.Badge.Top)
            .ThenByDescending(match => ScoreCandidate(match.Badge))
            .Select(match => (BadgeBox?)match.Badge)
            .FirstOrDefault();
    }

    private static double MeasureDarkDensity(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int left,
        int top,
        int right,
        int bottom)
    {
        left = Math.Clamp(left, 0, width - 1);
        right = Math.Clamp(right, 0, width - 1);
        top = Math.Clamp(top, 0, height - 1);
        bottom = Math.Clamp(bottom, 0, height - 1);
        if (right < left || bottom < top)
        {
            return 0;
        }

        var dark = 0;
        var total = (right - left + 1) * (bottom - top + 1);
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                ReadPixel(pixels, stride, x, y, out var red, out var green, out var blue);
                if (red <= 105 && green <= 105 && blue <= 105)
                {
                    dark++;
                }
            }
        }

        return total > 0 ? (double)dark / total : 0;
    }

    private static double ScoreCandidate(BadgeBox badge)
    {
        var idealAspect = badge.Width > badge.Height ? 1.45 : 1.0;
        var aspectPenalty = Math.Abs((double)badge.Width / badge.Height - idealAspect);
        return badge.PixelCount + badge.FillRatio * 80 - aspectPenalty * 12;
    }

    private static bool IsNarrowWhatsAppOne(CharacterBox character, BadgeBox badge)
    {
        var glyphWidth = character.Right - character.Left + 1;
        var glyphHeight = character.Bottom - character.Top + 1;
        return glyphWidth <= Math.Max(3, (int)Math.Round(badge.Width * 0.16))
               && glyphHeight >= Math.Max(6, (int)Math.Round(badge.Height * 0.24));
    }

    private static bool[] ExtractDigitMask(
        byte[] pixels,
        int width,
        int height,
        int stride,
        BadgeBox badge,
        ClientKind kind)
    {
        var mask = new bool[badge.Width * badge.Height];
        for (var localY = 0; localY < badge.Height; localY++)
        {
            var y = badge.Top + localY;
            var left = -1;
            var right = -1;

            // 先找这一行角标颜色的左右边界，再只在边界内部寻找白色笔画。
            // 这样可排除圆角外侧原窗口本身的白色背景。
            for (var localX = 0; localX < badge.Width; localX++)
            {
                var x = badge.Left + localX;
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    continue;
                }

                ReadPixel(pixels, stride, x, y, out var red, out var green, out var blue);
                if (!IsBadgeColor(red, green, blue, kind))
                {
                    continue;
                }

                left = left < 0 ? localX : left;
                right = localX;
            }

            if (left < 0 || right - left < 2)
            {
                continue;
            }

            for (var localX = left + 1; localX < right; localX++)
            {
                if (!IsInsideBadgeInterior(localX, localY, badge.Width, badge.Height))
                {
                    continue;
                }

                ReadPixel(pixels, stride, badge.Left + localX, y, out var red, out var green, out var blue);
                var minimum = Math.Min(red, Math.Min(green, blue));
                var maximum = Math.Max(red, Math.Max(green, blue));
                if (minimum >= 185 && maximum - minimum <= 45)
                {
                    mask[localY * badge.Width + localX] = true;
                }
            }
        }

        return mask;
    }

    private static bool IsInsideBadgeInterior(int x, int y, int width, int height)
    {
        var padding = Math.Max(1.5, height * 0.06);
        if (y < padding || y > height - 1 - padding)
        {
            return false;
        }

        var radius = Math.Min(width, height) / 2.0;
        var centerY = (height - 1) / 2.0;
        var leftCenterX = radius - 0.5;
        var rightCenterX = width - radius - 0.5;
        var innerRadius = Math.Max(1, radius - padding);
        if (x >= leftCenterX && x <= rightCenterX)
        {
            return true;
        }

        var centerX = x < leftCenterX ? leftCenterX : rightCenterX;
        var deltaX = x - centerX;
        var deltaY = y - centerY;
        return deltaX * deltaX + deltaY * deltaY <= innerRadius * innerRadius;
    }

    private static List<CharacterBox> FindCharacterSegments(bool[] mask, int width, int height)
    {
        var minimumColumnPixels = Math.Max(1, height / 12);
        var columns = new int[width];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                if (mask[y * width + x])
                {
                    columns[x]++;
                }
            }
        }

        var result = new List<CharacterBox>(3);
        var start = -1;
        for (var x = 0; x <= width; x++)
        {
            var occupied = x < width && columns[x] >= minimumColumnPixels;
            if (occupied && start < 0)
            {
                start = x;
            }
            else if (!occupied && start >= 0)
            {
                var end = x - 1;
                if (end - start + 1 >= 2)
                {
                    var top = height;
                    var bottom = -1;
                    for (var segmentX = start; segmentX <= end; segmentX++)
                    {
                        for (var y = 0; y < height; y++)
                        {
                            if (!mask[y * width + segmentX])
                            {
                                continue;
                            }

                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                        }
                    }

                    if (bottom >= top)
                    {
                        result.Add(new CharacterBox(start, top, end, bottom));
                    }
                }

                start = -1;
            }
        }

        return result;
    }

    private static bool[] NormalizeMask(bool[] source, int sourceWidth, int sourceHeight, CharacterBox box)
    {
        var result = new bool[NormalizedWidth * NormalizedHeight];
        var glyphWidth = box.Right - box.Left + 1;
        var glyphHeight = box.Bottom - box.Top + 1;
        var scale = Math.Min(
            (double)(NormalizedWidth - 2) / glyphWidth,
            (double)(NormalizedHeight - 2) / glyphHeight);
        var targetWidth = Math.Max(1, (int)Math.Round(glyphWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(glyphHeight * scale));
        var offsetX = (NormalizedWidth - targetWidth) / 2;
        var offsetY = (NormalizedHeight - targetHeight) / 2;

        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            var sourceY = box.Top + Math.Min(glyphHeight - 1, targetY * glyphHeight / targetHeight);
            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                var sourceX = box.Left + Math.Min(glyphWidth - 1, targetX * glyphWidth / targetWidth);
                if (sourceY >= 0 && sourceY < sourceHeight
                    && sourceX >= 0 && sourceX < sourceWidth
                    && source[sourceY * sourceWidth + sourceX])
                {
                    result[(offsetY + targetY) * NormalizedWidth + offsetX + targetX] = true;
                }
            }
        }

        return result;
    }

    private static CharacterMatch RecognizeCharacter(bool[] observed)
    {
        var observedDilated = Dilate(observed);
        var matches = new List<(char Character, double Score)>();
        foreach (var pair in Templates.Value)
        {
            var best = pair.Value.Max(template => Compare(observed, observedDilated, template));
            matches.Add((pair.Key, best));
        }

        var ordered = matches.OrderByDescending(item => item.Score).Take(2).ToArray();
        if (ordered.Length == 0 || ordered[0].Score < 0.52)
        {
            return new CharacterMatch('\0', ordered.FirstOrDefault().Score, 0);
        }

        var second = ordered.Length > 1 ? ordered[1].Score : 0;
        return new CharacterMatch(ordered[0].Character, ordered[0].Score, ordered[0].Score - second);
    }

    private static double Compare(bool[] observed, bool[] observedDilated, GlyphTemplate template)
    {
        var observedCount = 0;
        var observedMatches = 0;
        var templateMatches = 0;
        for (var index = 0; index < observed.Length; index++)
        {
            if (observed[index])
            {
                observedCount++;
                if (template.Dilated[index])
                {
                    observedMatches++;
                }
            }

            if (template.Mask[index] && observedDilated[index])
            {
                templateMatches++;
            }
        }

        if (observedCount == 0 || template.ForegroundCount == 0)
        {
            return 0;
        }

        var precision = (double)observedMatches / observedCount;
        var recall = (double)templateMatches / template.ForegroundCount;
        return precision + recall <= double.Epsilon
            ? 0
            : 2 * precision * recall / (precision + recall);
    }

    private static IReadOnlyDictionary<char, GlyphTemplate[]> CreateTemplates()
    {
        var result = "0123456789+".ToDictionary(character => character, _ => new List<GlyphTemplate>());
        var families = new[] { "Microsoft YaHei UI", "Segoe UI", "Arial" };
        var styles = new[] { FontStyle.Regular, FontStyle.Bold };
        var sizes = new[] { 18f, 20f, 22f, 24f, 26f, 28f, 30f };

        foreach (var familyName in families)
        {
            FontFamily? family = null;
            try
            {
                family = new FontFamily(familyName);
                foreach (var style in styles)
                {
                    if (!family.IsStyleAvailable(style))
                    {
                        continue;
                    }

                    foreach (var size in sizes)
                    {
                        using var font = new Font(family, size, style, GraphicsUnit.Pixel);
                        foreach (var character in result.Keys.ToArray())
                        {
                            var template = RenderTemplate(character, font);
                            if (template.HasValue)
                            {
                                result[character].Add(template.Value);
                            }
                        }
                    }
                }
            }
            catch
            {
                // 某个字体在精简系统中不存在时继续使用其余字体。
            }
            finally
            {
                family?.Dispose();
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DistinctBy(template => Convert.ToBase64String(PackBits(template.Mask))).ToArray());
    }

    private static GlyphTemplate? RenderTemplate(char character, Font font)
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.White);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoClip;
            graphics.DrawString(character.ToString(), font, brush, new PointF(3, 0), format);
        }

        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).R < 100)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return null;
        }

        var sourceWidth = right - left + 1;
        var sourceHeight = bottom - top + 1;
        var source = new bool[sourceWidth * sourceHeight];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                source[y * sourceWidth + x] = bitmap.GetPixel(left + x, top + y).R >= 100;
            }
        }

        var normalized = NormalizeMask(
            source,
            sourceWidth,
            sourceHeight,
            new CharacterBox(0, 0, sourceWidth - 1, sourceHeight - 1));
        return new GlyphTemplate(normalized, Dilate(normalized), normalized.Count(value => value));
    }

    private static bool[] Dilate(bool[] source)
    {
        var result = new bool[source.Length];
        for (var y = 0; y < NormalizedHeight; y++)
        {
            for (var x = 0; x < NormalizedWidth; x++)
            {
                if (!source[y * NormalizedWidth + x])
                {
                    continue;
                }

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var targetX = x + offsetX;
                        var targetY = y + offsetY;
                        if (targetX >= 0 && targetX < NormalizedWidth
                            && targetY >= 0 && targetY < NormalizedHeight)
                        {
                            result[targetY * NormalizedWidth + targetX] = true;
                        }
                    }
                }
            }
        }

        return result;
    }

    private static byte[] PackBits(bool[] values)
    {
        var result = new byte[(values.Length + 7) / 8];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index])
            {
                result[index / 8] |= (byte)(1 << index % 8);
            }
        }

        return result;
    }

    private static bool IsBadgeColor(byte red, byte green, byte blue, ClientKind kind)
    {
        return kind == ClientKind.WeChat
            ? red >= 175 && red - green >= 55 && red - blue >= 35
            : green >= 130 && green - red >= 30 && green - blue >= 10;
    }

    private static void ReadPixel(
        byte[] pixels,
        int stride,
        int x,
        int y,
        out byte red,
        out byte green,
        out byte blue)
    {
        var offset = y * stride + x * 4;
        blue = pixels[offset];
        green = pixels[offset + 1];
        red = pixels[offset + 2];
    }

    private readonly record struct RelativeRegion(int Left, int Top, int Right, int Bottom)
    {
        internal static RelativeRegion Create(
            int width,
            int height,
            double left,
            double right,
            double top,
            double bottom)
        {
            return new RelativeRegion(
                Math.Clamp((int)(width * left), 0, width - 1),
                Math.Clamp((int)(height * top), 0, height - 1),
                Math.Clamp((int)(width * right), 0, width - 1),
                Math.Clamp((int)(height * bottom), 0, height - 1));
        }

        internal static RelativeRegion CreateLogicalPixels(
            int width,
            int height,
            double dpiScale,
            double left,
            double right,
            double top,
            double bottom)
        {
            return new RelativeRegion(
                Math.Clamp((int)Math.Round(left * dpiScale), 0, width - 1),
                Math.Clamp((int)Math.Round(top * dpiScale), 0, height - 1),
                Math.Clamp((int)Math.Round(right * dpiScale), 0, width - 1),
                Math.Clamp((int)Math.Round(bottom * dpiScale), 0, height - 1));
        }
    }

    private readonly record struct BadgeBox(
        int Left,
        int Top,
        int Width,
        int Height,
        int PixelCount,
        double FillRatio);

    private readonly record struct CharacterBox(int Left, int Top, int Right, int Bottom);
    private readonly record struct CharacterMatch(char Character, double Score, double Margin);
    private readonly record struct GlyphTemplate(bool[] Mask, bool[] Dilated, int ForegroundCount);
}

internal readonly record struct BadgeResult(
    bool HasAlert,
    int Number,
    BadgeConfidenceLevel Confidence)
{
    internal static BadgeResult NoBadge => new(false, 0, BadgeConfidenceLevel.NoBadge);
    internal static BadgeResult Dot => new(true, 0, BadgeConfidenceLevel.Medium);
}

internal enum BadgeConfidenceLevel
{
    NoBadge,
    High,
    Medium,
}
