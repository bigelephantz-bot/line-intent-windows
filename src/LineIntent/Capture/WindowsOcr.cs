using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using LineIntent.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace LineIntent.Capture;

/// <summary>Windows language-pack OCR. Images remain in memory and are never uploaded or saved.</summary>
public static class WindowsOcr
{
    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();

    public static async Task<string> RecognizeAsync(Bitmap bitmap, string? languageTag = null,
        CancellationToken ct = default)
    {
        var lines = await ReadPositionedLinesAsync(bitmap, languageTag, ct);
        return string.Join(Environment.NewLine, lines.Select(line => NormalizeSpacing(line.Text)));
    }

    /// <summary>Retains bubble geometry and reading order; never infers a sender from plain text.</summary>
    public static async Task<ChatRecognitionResult> RecognizeChatAsync(Bitmap bitmap, string? languageTag = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var detected = await Task.Run(() => LineBubbleDetector.Detect(bitmap), ct);
        ct.ThrowIfCancellationRequested();
        var lines = await ReadPositionedLinesAsync(bitmap, languageTag, ct);
        var bubbles = detected.Select(bubble => new ChatBubble(
            new ChatRect(bubble.Bounds.X, bubble.Bounds.Y, bubble.Bounds.Width, bubble.Bounds.Height), bubble.Speaker)).ToArray();
        var fragments = new List<ChatOcrLine>();
        foreach (var line in lines)
        {
            // Windows OCR may put timestamps and two columns in one line. Split by bubble first.
            var groups = line.Words.GroupBy(word =>
            {
                var matches = bubbles.Select((bubble, index) => (bubble, index)).Where(entry =>
                {
                    var box = entry.bubble.Bounds;
                    var centerX = word.Bounds.X + word.Bounds.Width / 2;
                    var centerY = word.Bounds.Y + word.Bounds.Height / 2;
                    return centerX >= box.X && centerX <= box.Right && centerY >= box.Y && centerY <= box.Bottom;
                }).ToArray();
                return matches.Length == 1 ? matches[0].index : -1;
            });
            foreach (var group in groups)
            {
                var sorted = group.OrderBy(word => word.Bounds.X).ToArray();
                // Separate distant UI fragments, so a time/read marker cannot absorb another message.
                var run = new List<PositionedWord>();
                foreach (var word in sorted)
                {
                    if (run.Count > 0 && group.Key < 0 && word.Bounds.X - run[^1].Bounds.Right > Math.Max(32, word.Bounds.Height * 2))
                    {
                        fragments.Add(ToFragment(run));
                        run.Clear();
                    }
                    run.Add(word);
                }
                if (run.Count > 0) fragments.Add(ToFragment(run));
            }
        }
        var initial = ChatLayout.Assemble(fragments, bubbles, bitmap.Width, bitmap.Height);
        var emptyBubbles = bubbles.Where(bubble => !initial.Messages.Any(message => message.Bounds == bubble.Bounds
            && !string.IsNullOrWhiteSpace(message.Text))).Take(6).ToArray();
        foreach (var bubble in emptyBubbles)
        {
            ct.ThrowIfCancellationRequested();
            var box = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), Rectangle.FromLTRB(
                (int)Math.Floor(bubble.Bounds.X), (int)Math.Floor(bubble.Bounds.Y),
                (int)Math.Ceiling(bubble.Bounds.Right), (int)Math.Ceiling(bubble.Bounds.Bottom)));
            if (box.Width < 1 || box.Height < 1) continue;
            // A one-character acknowledgment can disappear in whole-window OCR. Retry a bounded,
            // padded crop locally, keeping its already detected bubble/speaker rather than guessing text.
            var factor = Math.Min(3d, 1800d / Math.Max(box.Width, box.Height));
            var width = Math.Max(1, (int)(box.Width * factor));
            var height = Math.Max(1, (int)(box.Height * factor));
            using var enlarged = new Bitmap(width + 48, height + 48, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(enlarged))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(bitmap, new Rectangle(24, 24, width, height), box, GraphicsUnit.Pixel);
            }
            var text = await RecognizeAsync(enlarged, languageTag, ct);
            if (string.IsNullOrWhiteSpace(text))
                text = await Task.Run(() => RecognizeShortBubbleAsync(bitmap, box, languageTag, ct), ct);
            if (!string.IsNullOrWhiteSpace(text))
                fragments.Add(new ChatOcrLine(text.ReplaceLineEndings(" "), bubble.Bounds));
        }
        return ChatLayout.Assemble(fragments, bubbles, bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Windows OCR can discard isolated glyphs. A neutral printed anchor helps line detection;
    /// only words spatially inside the original glyph area are eligible, never the anchor text.
    /// </summary>
    public static async Task<string> RecognizeShortBubbleAsync(Bitmap source, Rectangle bounds,
        string? languageTag = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var box = Rectangle.Intersect(bounds, new Rectangle(Point.Empty, source.Size));
        if (box.Width < 8 || box.Height < 8 || (long)box.Width * box.Height > 160_000) return "";
        using var patch = source.Clone(box, PixelFormat.Format32bppRgb);
        var histogram = new Dictionary<int, int>();
        for (var y = 0; y < patch.Height; y++)
        for (var x = 0; x < patch.Width; x++)
        {
            var color = patch.GetPixel(x, y);
            var key = (color.R / 16 << 8) | (color.G / 16 << 4) | color.B / 16;
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        var dominant = histogram.MaxBy(pair => pair.Value).Key;
        var background = .2126 * (((dominant >> 8) & 15) * 16 + 8)
            + .7152 * (((dominant >> 4) & 15) * 16 + 8) + .0722 * ((dominant & 15) * 16 + 8);
        var backgroundMin = Math.Min(((dominant >> 8) & 15) * 16 + 8,
            Math.Min(((dominant >> 4) & 15) * 16 + 8, (dominant & 15) * 16 + 8));
        var darkThreshold = Math.Min(155, backgroundMin - 40);
        bool IsInk(Color color, bool includeColorFringe = false)
        {
            var luminance = .2126 * color.R + .7152 * color.G + .0722 * color.B;
            return background >= 128
                ? (includeColorFringe ? Math.Min(color.R, Math.Min(color.G, color.B)) : Math.Max(color.R, Math.Max(color.G, color.B))) < darkThreshold
                : luminance > background + 65;
        }
        var left = patch.Width; var top = patch.Height; var right = -1; var bottom = -1; var inkCount = 0;
        for (var y = 0; y < patch.Height; y++)
        for (var x = 0; x < patch.Width; x++)
            if (IsInk(patch.GetPixel(x, y)))
            {
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); inkCount++;
            }
        if (inkCount < 8 || right - left < 2 || bottom - top < 4) return "";
        var inkBox = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        if (inkBox.Width > inkBox.Height * 2.8 || inkBox.Height > inkBox.Width * 3) return "";
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binary in new[] { false, true })
        {
            ct.ThrowIfCancellationRequested();
            using var glyph = new Bitmap(inkBox.Width, inkBox.Height, PixelFormat.Format32bppRgb);
            for (var y = 0; y < glyph.Height; y++)
            for (var x = 0; x < glyph.Width; x++)
            {
                var color = patch.GetPixel(inkBox.X + x, inkBox.Y + y);
                glyph.SetPixel(x, y, binary ? (IsInk(color, true) ? Color.Black : Color.White) : color);
            }
            var glyphWidth = Math.Max(8, 60 * glyph.Width / glyph.Height);
            var anchorX = Math.Max(100, 16 + glyphWidth + 24);
            using var canvas = new Bitmap(Math.Max(300, anchorX + 160), 110, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(canvas))
            using (var font = new Font("Microsoft JhengHei", 52, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(glyph, new Rectangle(16, 25, glyphWidth, 60), new Rectangle(Point.Empty, glyph.Size), GraphicsUnit.Pixel);
                graphics.DrawString("文字", font, Brushes.Black, anchorX, 16);
            }
            var lines = await ReadPositionedLinesAsync(canvas, languageTag, ct);
            var allWords = lines.SelectMany(line => line.Words).ToArray();
            var anchor = string.Concat(allWords.Where(word => word.Bounds.X >= anchorX - 5)
                .OrderBy(word => word.Bounds.X).Select(word => word.Text)).Trim();
            if (anchor != "文字") continue;
            var words = allWords.Where(word => word.Bounds.X >= 8
                && word.Bounds.Right <= anchorX - 5 && word.Bounds.Y + word.Bounds.Height / 2 is >= 17 and <= 93)
                .OrderBy(word => word.Bounds.X).ToArray();
            var value = NormalizeSpacing(string.Concat(words.Select(word => word.Text))).Trim();
            var limit = inkBox.Width <= inkBox.Height * 1.35 ? 1 : 3;
            if (value.Length >= 1 && value.Length <= limit && !value.Any(char.IsControl)) candidates.Add(value);
        }
        // Conflicting recognitions remain blank for review rather than selecting a guess.
        return candidates.Count == 1 ? candidates.Single() : "";
    }

    private static ChatOcrLine ToFragment(IReadOnlyList<PositionedWord> words)
    {
        var left = words.Min(word => word.Bounds.X);
        var top = words.Min(word => word.Bounds.Y);
        var right = words.Max(word => word.Bounds.Right);
        var bottom = words.Max(word => word.Bounds.Bottom);
        return new ChatOcrLine(NormalizeSpacing(string.Join(' ', words.Select(word => word.Text))),
            new ChatRect(left, top, right - left, bottom - top));
    }

    private sealed record PositionedWord(string Text, ChatRect Bounds);
    private sealed record PositionedLine(string Text, IReadOnlyList<PositionedWord> Words);

    private static async Task<IReadOnlyList<PositionedLine>> ReadPositionedLinesAsync(Bitmap bitmap, string? languageTag,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ct.ThrowIfCancellationRequested();
        var engine = CreateEngine(languageTag);
        using var scaled = ScaleForOcr(bitmap, checked((int)OcrEngine.MaxImageDimension));
        using var softwareBitmap = BitmapPixels.ToSoftwareBitmap(scaled);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        return result.Lines.Select(line => new PositionedLine(line.Text, line.Words.Select(word =>
        {
            var box = word.BoundingRect;
            // WinRT reports deskewed boxes. Map TextAngle back before matching screen bubbles.
            return new PositionedWord(word.Text, OcrGeometry.ToImageCoordinates(
                new ChatRect(box.X, box.Y, box.Width, box.Height), scaled.Width, scaled.Height,
                bitmap.Width, bitmap.Height, result.TextAngle));
        }).ToArray())).ToArray();
    }

    /// <summary>Removes OCR word separators inside Chinese text without correcting recognized characters.</summary>
    internal static string NormalizeSpacing(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runes = text.EnumerateRunes().ToArray();
        var normalized = new StringBuilder(text.Length);
        for (var index = 0; index < runes.Length; index++)
        {
            if (runes[index].Value is not (0x20 or 0xA0 or 0x3000))
            {
                normalized.Append(runes[index].ToString());
                continue;
            }

            var end = index + 1;
            while (end < runes.Length && runes[end].Value is 0x20 or 0xA0 or 0x3000)
                end++;

            var remove = index > 0 && end < runes.Length
                && ((IsHan(runes[index - 1]) && IsHan(runes[end]))
                    || (runes[index - 1].Value is not (0x0D or 0x0A)
                        && "，。！？；：、（）「」『』【】《》〈〉〔〕［］｛｝…".Contains(runes[end].ToString(), StringComparison.Ordinal)));
            if (!remove)
                for (var space = index; space < end; space++)
                    normalized.Append(runes[space].ToString());
            index = end - 1;
        }
        return normalized.ToString();
    }

    private static bool IsHan(Rune value) => value.Value is
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x323AF;

    private static OcrEngine CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            OcrEngine? selected;
            try { selected = OcrEngine.TryCreateFromLanguage(new Language(languageTag)); }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"OCR 語言代碼無效：{languageTag}。請從已安裝語言中選取。", exception);
            }
            return selected ?? throw MissingLanguage(languageTag);
        }

        foreach (var tag in new[] { "zh-Hant", "zh-TW", "zh-Hans", "en-US" })
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine is not null)
                return engine;
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine is not null)
            ?? throw MissingLanguage(null);
    }

    private static InvalidOperationException MissingLanguage(string? tag) => new(
        $"Windows 未安裝可用的 OCR 語言{(tag is null ? "" : $"（{tag}）")}。" +
        "請到 Windows 設定 → 時間與語言 → 語言與地區，安裝繁體中文或英文語言套件的基本輸入／光學字元辨識功能，再重新啟動本工具。也可先手動貼上對話文字。");

    private static Bitmap ScaleForOcr(Bitmap source, int maximumDimension)
    {
        if (maximumDimension <= 0)
            throw new InvalidOperationException("Windows OCR 回報無效的影像大小上限。");
        var factor = Math.Min(1d, (double)maximumDimension / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Floor(source.Width * factor));
        var height = Math.Max(1, (int)Math.Floor(source.Height * factor));
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(target);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }
}
