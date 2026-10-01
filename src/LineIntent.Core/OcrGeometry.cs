namespace LineIntent.Core;

/// <summary>Maps Windows OCR's rotated coordinate frame back to the captured image.</summary>
public static class OcrGeometry
{
    /// <summary>
    /// Rotates every corner clockwise by OcrResult.TextAngle around the OCR image center,
    /// encloses the result in an axis-aligned rectangle, then scales to the original image.
    /// Coordinates are deliberately not clamped: partial/outside text must remain detectable.
    /// </summary>
    public static ChatRect ToImageCoordinates(ChatRect box, double ocrWidth, double ocrHeight,
        double originalWidth, double originalHeight, double? textAngle)
    {
        const string invalid = "OCR 座標轉換需要有效的矩形、影像尺寸與旋轉角度。";
        if (box is not { IsValid: true } || !PositiveFinite(ocrWidth) || !PositiveFinite(ocrHeight)
            || !PositiveFinite(originalWidth) || !PositiveFinite(originalHeight)
            || (textAngle is double angleValue && !double.IsFinite(angleValue)))
            throw new ArgumentException(invalid);

        double scaleX = originalWidth / ocrWidth;
        double scaleY = originalHeight / ocrHeight;
        double radians = ((textAngle ?? 0) % 360) * (Math.PI / 180);
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double centerX = ocrWidth / 2;
        double centerY = ocrHeight / 2;
        (double X, double Y)[] corners =
        [
            (box.X, box.Y), (box.Right, box.Y),
            (box.Right, box.Bottom), (box.X, box.Bottom)
        ];
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach ((double x, double y) in corners)
        {
            double relativeX = x - centerX;
            double relativeY = y - centerY;
            // Image Y increases downwards, so the conventional positive rotation is clockwise.
            double imageX = centerX + cosine * relativeX - sine * relativeY;
            double imageY = centerY + sine * relativeX + cosine * relativeY;
            minX = Math.Min(minX, imageX);
            minY = Math.Min(minY, imageY);
            maxX = Math.Max(maxX, imageX);
            maxY = Math.Max(maxY, imageY);
        }
        ChatRect mapped = new(minX * scaleX, minY * scaleY, (maxX - minX) * scaleX, (maxY - minY) * scaleY);
        if (!mapped.IsValid) throw new ArgumentException(invalid);
        return mapped;
    }

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
