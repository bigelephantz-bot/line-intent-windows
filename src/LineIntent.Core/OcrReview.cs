namespace LineIntent.Core;

public sealed record OcrReviewEntry(string Speaker, string Text, bool Skip = false);
public sealed record OcrReviewIssue(int RowIndex, string Field, string Message);
public sealed record OcrReviewResult(string Transcript, IReadOnlyList<OcrReviewIssue> Issues, int SkippedCount)
{
    public bool CanApply => Issues.Count == 0;
}

/// <summary>Validates explicit user decisions; skipped rows never need a speaker or text.</summary>
public static class OcrReview
{
    public static OcrReviewResult Validate(IReadOnlyList<OcrReviewEntry> entries)
    {
        var issues = new List<OcrReviewIssue>();
        var lines = new List<string>();
        var skipped = 0;
        var hasOther = false;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Skip) { skipped++; continue; }
            var text = string.Join(' ', (entry.Text ?? "").ReplaceLineEndings("\n").Split('\n')
                .Select(part => part.Trim()).Where(part => part.Length > 0));
            if (entry.Speaker is not ("me" or "other"))
                issues.Add(new(index, "speaker", $"第 {index + 1} 列：請確認說話者或勾選「略過」。"));
            if (text.Length == 0)
                issues.Add(new(index, "text", $"第 {index + 1} 列：文字空白，請補字或勾選「略過」。"));
            if (entry.Speaker is not ("me" or "other") || text.Length == 0) continue;
            hasOther |= entry.Speaker == "other";
            lines.Add((entry.Speaker == "me" ? "我：" : "對方：") + text);
        }
        if (issues.Count == 0 && !hasOther)
            issues.Add(new(-1, "conversation", "至少需要一則有文字、未略過的對方訊息。"));
        var transcript = string.Join(Environment.NewLine, lines);
        if (transcript.Length > Transcript.MaximumCharacters)
            issues.Add(new(-1, "conversation", $"對話超過 {Transcript.MaximumCharacters:N0} 字元，請縮小範圍。"));
        return new(issues.Count == 0 ? transcript : "", issues.AsReadOnly(), skipped);
    }
}
