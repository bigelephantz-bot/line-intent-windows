namespace LineIntent.Core;

public sealed record ChatRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width > 0 && Height > 0
        && double.IsFinite(Right) && double.IsFinite(Bottom);
}

public sealed record ChatOcrLine(string Text, ChatRect Bounds);
public sealed record ChatBubble(ChatRect Bounds, string Speaker);
public sealed record ChatRecognizedMessage(string Speaker, string Text, ChatRect Bounds);
public sealed record ChatRecognitionResult(string Transcript,
    IReadOnlyList<ChatRecognizedMessage> Messages, IReadOnlyList<string> Warnings, int IgnoredLineCount);

/// <summary>
/// Pure geometry assembly of OCR fragments. Bubble speakers come from an external detector;
/// this class never infers a speaker from text, horizontal position, or an OCR column's order.
/// </summary>
public static class ChatLayout
{
    public static ChatRecognitionResult Assemble(IReadOnlyList<ChatOcrLine> lines,
        IReadOnlyList<ChatBubble> bubbles, double imageWidth, double imageHeight)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(bubbles);
        bool imageValid = double.IsFinite(imageWidth) && double.IsFinite(imageHeight)
            && imageWidth > 0 && imageHeight > 0;
        var usable = bubbles.Select((bubble, index) => (Bubble: bubble, Index: index))
            .Where(item => imageValid && item.Bubble.Bounds is { IsValid: true }
                && IntersectArea(item.Bubble.Bounds, new(0, 0, imageWidth, imageHeight)) > 0)
            .ToArray();
        var assigned = usable.ToDictionary(item => item.Index, _ => new List<ChatOcrLine>());
        var recognized = new List<(ChatRecognizedMessage Message, int Sequence)>();
        int ignored = 0, unknownLines = 0, ambiguousLines = 0, badLineGeometry = 0;

        for (int index = 0; index < lines.Count; index++)
        {
            ChatOcrLine line = lines[index];
            string text = NormalizeText(line.Text);
            if (text.Length == 0) { ignored++; continue; }
            ChatRect bounds = line.Bounds;
            if (bounds is not { IsValid: true })
            {
                badLineGeometry++;
                unknownLines++;
                recognized.Add((new("unknown", text, new(0, imageValid ? imageHeight : index, 0, 0)), index));
                continue;
            }
            ChatOcrLine normalized = new(text, bounds);
            var candidates = usable.Where(item => FitsLine(bounds, item.Bubble.Bounds)).ToArray();
            if (candidates.Length == 1)
            {
                assigned[candidates[0].Index].Add(normalized);
                continue;
            }
            if (candidates.Length > 1) ambiguousLines++;
            bool contactsBubble = usable.Any(item => IntersectArea(bounds, item.Bubble.Bounds) > 0);
            // Even timestamp-looking text remains content if it touches a possible bubble.
            if (!contactsBubble && IsOutsideMetadata(text)) { ignored++; continue; }
            unknownLines++;
            recognized.Add((new("unknown", text, bounds), index));
        }

        int emptyBubbles = 0, unknownBubbles = 0, clippedBubbles = 0;
        foreach (var item in usable)
        {
            double edgeTolerance = Math.Max(1, Math.Min(3, imageHeight * .002));
            if (item.Bubble.Bounds.Y <= edgeTolerance || item.Bubble.Bounds.Bottom >= imageHeight - edgeTolerance)
                clippedBubbles++;
            List<ChatOcrLine> contents = assigned[item.Index];
            string speaker = item.Bubble.Speaker is "me" or "other" ? item.Bubble.Speaker : "unknown";
            if (speaker == "unknown") unknownBubbles++;
            if (contents.Count == 0)
            {
                emptyBubbles++;
                recognized.Add((new(speaker, "", item.Bubble.Bounds), lines.Count + item.Index));
                continue;
            }
            string body = string.Join(' ', SortIntoRows(contents).Select(line => line.Text));
            recognized.Add((new(speaker, body, item.Bubble.Bounds), lines.Count + item.Index));
        }

        var warnings = new List<string>();
        if (!imageValid || usable.Length != bubbles.Count || badLineGeometry > 0)
            warnings.Add("部分影像或文字位置資料無效，無法確認的文字已保留為「未確認」，請手動檢查順序。");
        if (unknownLines > 0 || unknownBubbles > 0)
            warnings.Add($"有 {unknownLines + unknownBubbles} 段文字無法確認發話者，已保留為「未確認」；分析前請核對。");
        if (ambiguousLines > 0)
            warnings.Add($"有 {ambiguousLines} 段文字同時落在多個氣泡中，未自動指定發話者。");
        if (emptyBubbles > 0)
            warnings.Add($"有 {emptyBubbles} 個偵測到的氣泡沒有可辨識文字，可能包含圖片、貼圖或漏讀訊息；請核對畫面。");
        if (clippedBubbles > 0)
            warnings.Add("部分氣泡接觸影像上緣或下緣，內容可能不完整；請調整擷取範圍後核對。");

        ChatRecognizedMessage[] messages = recognized.OrderBy(item => item.Message.Bounds.Y)
            .ThenBy(item => item.Message.Bounds.X).ThenBy(item => item.Sequence)
            .Select(item => item.Message).ToArray();
        string transcript = string.Join(Environment.NewLine, messages.Where(message => !string.IsNullOrWhiteSpace(message.Text)).Select(message =>
            $"{(message.Speaker == "me" ? "我" : message.Speaker == "other" ? "對方" : "未確認")}：{message.Text}"));
        return new(transcript, Array.AsReadOnly(messages), warnings.AsReadOnly(), ignored);
    }

    private static string NormalizeText(string? text) => (text ?? "").Replace("\r\n", " ")
        .Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();

    private static bool FitsLine(ChatRect line, ChatRect bubble)
    {
        double centerX = line.X + line.Width / 2;
        double centerY = line.Y + line.Height / 2;
        return centerX >= bubble.X && centerX <= bubble.Right && centerY >= bubble.Y && centerY <= bubble.Bottom
            && IntersectArea(line, bubble) >= line.Width * line.Height * .5;
    }

    private static double IntersectArea(ChatRect left, ChatRect right) =>
        Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X))
        * Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));

    private static IEnumerable<ChatOcrLine> SortIntoRows(IEnumerable<ChatOcrLine> lines)
    {
        var rows = new List<List<ChatOcrLine>>();
        foreach (ChatOcrLine line in lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X))
        {
            List<ChatOcrLine>? row = rows.LastOrDefault();
            if (row is null || !SameBaseline(row[0].Bounds, line.Bounds))
            { row = []; rows.Add(row); }
            row.Add(line);
        }
        foreach (List<ChatOcrLine> row in rows)
            foreach (ChatOcrLine line in row.OrderBy(line => line.Bounds.X).ThenBy(line => line.Bounds.Y))
                yield return line;
    }

    private static bool SameBaseline(ChatRect left, ChatRect right)
    {
        double overlap = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        return overlap >= Math.Min(left.Height, right.Height) * .6;
    }

    private static bool IsOutsideMetadata(string text)
    {
        // Deliberately exact: do not strip a sentence merely because it mentions a time or read receipt.
        // OCR often separates clock digits and punctuation; only the metadata comparison is compacted.
        text = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        const string time = @"(?:(?:上午|下午|AM|PM)\s*)?(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?(?:\s*(?:AM|PM))?";
        const string weekday = @"(?:星期[一二三四五六日天]|[週周][一二三四五六日天]|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Tue|Wed|Thu|Fri|Sat|Sun)";
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^(?:已讀|已読|Read|未讀訊息|Unreadmessages|今天|昨天|Today|Yesterday|LINE)$", options)
            || System.Text.RegularExpressions.Regex.IsMatch(text, $@"^(?:(?:已讀|已読|Read)\s*)?{time}(?:\s*(?:已讀|已読|Read))?$", options)
            || System.Text.RegularExpressions.Regex.IsMatch(text,
                $@"^(?:\d{{4}}[./-]\d{{1,2}}[./-]\d{{1,2}}(?:\s*[(（]?{weekday}[)）]?)?|\d{{1,2}}[./-]\d{{1,2}}\s*[(（]?{weekday}[)）]?)$", options)
            || System.Text.RegularExpressions.Regex.IsMatch(text, $@"^\d{{1,2}}月\d{{1,2}}日(?:\s*[(（]?{weekday}[)）]?)?$", options)
            || System.Text.RegularExpressions.Regex.IsMatch(text, $@"^{weekday}$", options);
    }
}
