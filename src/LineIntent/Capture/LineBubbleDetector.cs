using System.Drawing.Imaging;
using System.Buffers;

namespace LineIntent.Capture;

public sealed record DetectedBubble(Rectangle Bounds, string Speaker, double Confidence);

/// <summary>
/// Finds flat filled chat bubbles, not text. Input must include both conversation edges.
/// Coordinates refer to the original bitmap; no image or recognized content is persisted.
/// </summary>
public static class LineBubbleDetector
{
    public static IReadOnlyList<DetectedBubble> Detect(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width < 80 || bitmap.Height < 24)
            return [];
        if ((long)bitmap.Width * bitmap.Height > 64_000_000) throw new InvalidOperationException("影像過大，請縮小擷取範圍。");
        Bitmap? converted = null;
        var source = bitmap;
        try
        {
            if (bitmap.PixelFormat is not (PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb))
            {
                converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppRgb);
                using var graphics = Graphics.FromImage(converted);
                graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    new Rectangle(Point.Empty, bitmap.Size), GraphicsUnit.Pixel);
                source = converted;
            }
            var data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, source.PixelFormat);
            try { return DetectLocked(data, source.Width, source.Height, source.PixelFormat == PixelFormat.Format32bppArgb); }
            finally { source.UnlockBits(data); }
        }
        finally { converted?.Dispose(); }
    }

    private static IReadOnlyList<DetectedBubble> DetectLocked(BitmapData data, int imageWidth, int imageHeight, bool alpha)
    {
        var background = FindBackground(data, imageWidth, imageHeight, alpha);
        var step = Math.Max(imageWidth, imageHeight) >= 3200 ? 3 : 2;
        var width = (imageWidth + step - 1) / step;
        var height = (imageHeight + step - 1) / step;
        var sampleCount = checked(width * height);
        var types = ArrayPool<byte>.Shared.Rent(sampleCount);
        int[]? queue = null;
        try
        {
            queue = ArrayPool<int>.Shared.Rent(sampleCount);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = ReadPixel(data, x * step, y * step, alpha);
                    types[y * width + x] = Classify(pixel.R, pixel.G, pixel.B, background);
                }

            var candidates = new List<Candidate>();
            for (var seed = 0; seed < sampleCount; seed++)
            {
                var type = types[seed];
                if (type == 0) continue;
                types[seed] = 0;
                var head = 0;
                var count = 1;
                queue[0] = seed;
                var left = seed % width;
                var right = left;
                var top = seed / width;
                var bottom = top;
                while (head < count)
                {
                    var cell = queue[head++];
                    var x = cell % width;
                    var y = cell / width;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                            var next = ny * width + nx;
                            if (types[next] != type) continue;
                            types[next] = 0;
                            queue[count++] = next;
                        }
                }

                var boxWidth = (right - left + 1) * step;
                var boxHeight = (bottom - top + 1) * step;
                var fill = (double)count / ((right - left + 1) * (bottom - top + 1));
                if (count < 36 || boxWidth < 24 || boxHeight < 18 || boxWidth < boxHeight * .35
                    || boxWidth > imageWidth * .9 || boxHeight > imageHeight * .85 || fill < .43)
                    continue;

                var palette = new Dictionary<int, int>();
                var rowLeft = Enumerable.Repeat(int.MaxValue, bottom - top + 1).ToArray();
                var rowRight = Enumerable.Repeat(int.MinValue, bottom - top + 1).ToArray();
                for (var i = 0; i < count; i++)
                {
                    var x = queue[i] % width;
                    var y = queue[i] / width;
                    var pixel = ReadPixel(data, x * step, y * step, alpha);
                    var key = ColorKey(pixel.R, pixel.G, pixel.B);
                    palette[key] = palette.GetValueOrDefault(key) + 1;
                    rowLeft[y - top] = Math.Min(rowLeft[y - top], x);
                    rowRight[y - top] = Math.Max(rowRight[y - top], x);
                }
                // Photographic avatars have varied pixels; a filled bubble has a dominant flat color.
                if ((double)palette.Values.Max() / count < .60) continue;
                var bodyLeft = MedianCenter(rowLeft, int.MaxValue) * step;
                var bodyRight = (MedianCenter(rowRight, int.MinValue) + 1) * step;
                var box = Rectangle.Intersect(new Rectangle(0, 0, imageWidth, imageHeight),
                    Rectangle.FromLTRB(left * step - 1, top * step - 1, (right + 1) * step + 1, (bottom + 1) * step + 1));
                candidates.Add(new Candidate(box, type, bodyLeft, bodyRight, fill));
            }

            var result = new List<DetectedBubble>();
            foreach (var candidate in candidates)
            {
                var center = (candidate.BodyLeft + candidate.BodyRight) / 2d;
                var atLeft = candidate.BodyLeft <= imageWidth * .22 && center < imageWidth * .5;
                var atRight = candidate.BodyRight >= imageWidth * .78 && center > imageWidth * .5;
                var aspect = (double)candidate.Bounds.Width / candidate.Bounds.Height;
                // Avatars occupy the outer left rail, before the repeated bubble left edge.
                var hasBubbleToRight = candidates.Any(other => !ReferenceEquals(other, candidate)
                    && other.Type == 1 && other.BodyLeft > candidate.BodyRight - 4
                    && other.BodyLeft < imageWidth * .22 && other.Bounds.Width >= other.Bounds.Height * 1.6);
                if (candidate.Type == 1 && aspect is > .65 and < 1.25
                    && candidate.BodyLeft < imageWidth * .04 && hasBubbleToRight)
                    continue;

                // Small centered neutral capsules are date/unread separators, not speaker messages.
                if (candidate.Type == 1 && !atLeft && !atRight && candidate.Bounds.Width < imageWidth * .2
                    && aspect > 1.15 && candidate.Bounds.Height < 70)
                    continue;

                var speaker = "unknown";
                var confidence = .35;
                if (atRight && candidate.Type == 2) { speaker = "me"; confidence = .97; }
                else if (atLeft && candidate.Type == 1) { speaker = "other"; confidence = .91; }
                else if (atRight && candidate.Type == 1) { speaker = "me"; confidence = .78; }
                result.Add(new DetectedBubble(candidate.Bounds, speaker, confidence));
            }
            return result.OrderBy(bubble => bubble.Bounds.Top).ThenBy(bubble => bubble.Bounds.Left).ToArray();
        }
        finally
        {
            // Geometry masks can still describe private content; clear pooled storage on return.
            ArrayPool<byte>.Shared.Return(types, clearArray: true);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue, clearArray: true);
        }
    }

    private sealed record Candidate(Rectangle Bounds, byte Type, int BodyLeft, int BodyRight, double Fill);

    private static int MedianCenter(int[] rows, int missing)
    {
        // Avoid capturing row arrays in LINQ lambdas inside the per-pixel seed loop: C# would
        // allocate a closure even for seeds rejected before reaching the candidate calculation.
        var count = Math.Max(1, rows.Length / 2);
        var ordered = new int[count];
        var used = 0;
        for (var row = rows.Length / 4; row < rows.Length / 4 + count; row++)
            if (rows[row] != missing) ordered[used++] = rows[row];
        Array.Sort(ordered, 0, used);
        return used == 0 ? 0 : ordered[used / 2];
    }

    private static (int R, int G, int B) FindBackground(BitmapData data, int width, int height, bool alpha)
    {
        var counts = new Dictionary<int, int>();
        for (var y = 0; y < height; y += Math.Max(1, height / 80))
            for (var x = 0; x < width; x += Math.Max(1, width / 80))
            {
                var pixel = ReadPixel(data, x, y, alpha);
                var key = ColorKey(pixel.R, pixel.G, pixel.B);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        var dominant = counts.MaxBy(pair => pair.Value).Key;
        return (((dominant >> 10) & 31) * 8 + 4, ((dominant >> 5) & 31) * 8 + 4, (dominant & 31) * 8 + 4);
    }

    private static int ColorKey(int red, int green, int blue) => ((red / 8) << 10) | ((green / 8) << 5) | (blue / 8);

    private static unsafe (int R, int G, int B) ReadPixel(BitmapData data, int x, int y, bool alpha)
    {
        var pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
        var a = alpha ? pixel[3] : 255;
        return (pixel[2] * a / 255, pixel[1] * a / 255, pixel[0] * a / 255);
    }

    private static byte Classify(int red, int green, int blue, (int R, int G, int B) background)
    {
        if (green >= 65 && green - red >= 12 && green - blue >= 18)
            return 2;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var contrast = (Math.Abs(red - background.R) + Math.Abs(green - background.G) + Math.Abs(blue - background.B)) / 3;
        if (maximum - minimum <= 18 && minimum >= 24 && contrast >= 10)
            return 1;
        return 0;
    }
}
