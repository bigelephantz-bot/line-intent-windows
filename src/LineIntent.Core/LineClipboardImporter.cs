using System.Globalization;
using System.Text.RegularExpressions;

namespace LineIntent.Core;

public sealed record LineImportResult(string Transcript, int MessageCount,
    bool HasSelfReplyAfterLatestOther, IReadOnlyList<string> Warnings, int SkippedRecallCount = 0);

/// <summary>
/// Converts user-confirmed, one-to-one LINE clipboard sender names to explicit speaker labels.
/// Suggestions are text heuristics, never identity decisions. No clipboard or file access occurs here.
/// </summary>
public static partial class LineClipboardImporter
{
    [GeneratedRegex(@"^\s*(?<time>(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?)(?<separator>[ \t]+)(?<rest>.*)$")]
    private static partial Regex TimestampLine();

    [GeneratedRegex(@"^\s*\d{1,2}:\d{2}(?::\d{2})?(?:\s|$)")]
    private static partial Regex TimestampLikeLine();

    [GeneratedRegex(@"^\s*(?<year>\d{4})[./-](?<month>\d{1,2})[./-](?<day>\d{1,2})(?:\s*[(（]?(?:星期[一二三四五六日天]|[週周][一二三四五六日天]|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Tue|Wed|Thu|Fri|Sat|Sun)[)）]?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DateHeading();

    private const string InvalidImport = "LINE 複製內容無法完整對應到這兩位發話者。請確認姓名與原文完全相同，且內容只包含這兩人的對話。";
    private const string RecallNotice = "已收回訊息";

    public static bool LooksLikeLineClipboard(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= Transcript.MaximumCharacters
        && Lines(text).Any(line => TimestampLine().IsMatch(line));

    public static IReadOnlyList<string> SuggestNames(string text)
    {
        ValidateText(text);
        var explicitNames = new List<string>();
        var groups = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        foreach (string line in Lines(text))
        {
            Match match = TimestampLine().Match(line);
            if (!match.Success) continue;
            string rest = match.Groups["rest"].Value;
            if (TryTabFields(match, out string tabName, out _))
            {
                if (tabName.Length > 0 && tabName.Length <= 150 && !explicitNames.Contains(tabName, StringComparer.Ordinal))
                    explicitNames.Add(tabName);
                continue;
            }
            // A notice has no space between the final name token and the marker. It must not
            // shorten the common prefix of ordinary messages. This affects suggestions only;
            // Convert still preserves sentences that merely discuss a recalled message.
            if (rest.TrimEnd().EndsWith(RecallNotice, StringComparison.Ordinal)) continue;
            string[] tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!groups.TryGetValue(tokens[0], out List<string[]>? entries))
                groups[tokens[0]] = entries = [];
            entries.Add(tokens);
        }
        var suggestions = new List<string>(explicitNames);
        foreach (List<string[]> group in groups.Values)
        {
            // A single line cannot establish a multi-word name. Repeated lines may establish a prefix.
            int count = group.Count == 1 ? 1 : Math.Min(6, group.Min(tokens => tokens.Length - 1));
            for (int token = 0; token < count; token++)
            {
                if (group.Any(tokens => !string.Equals(tokens[token], group[0][token], StringComparison.Ordinal)))
                { count = token; break; }
            }
            string candidate = string.Join(' ', group[0].Take(count));
            if (candidate.Length > 0 && candidate.Length <= 150 && !suggestions.Contains(candidate, StringComparer.Ordinal))
                suggestions.Add(candidate);
        }
        return suggestions.AsReadOnly();
    }

    public static LineImportResult Convert(string text, string selfName, string otherName)
    {
        ValidateText(text);
        string self = ValidateName(selfName);
        string other = ValidateName(otherName);
        if (string.Equals(self, other, StringComparison.Ordinal))
            throw new JevException("「我」與「對方」必須選擇不同的完整顯示名稱。");
        string[] names = new[] { self, other }.OrderByDescending(name => name.Length).ToArray();
        var messages = new List<(string From, string Body)>();
        string? currentFrom = null;
        var currentBody = new List<string>();
        int headingCount = 0;
        int preservedDates = 0;
        int skippedRecalls = 0;
        bool joinedContinuation = false;

        void FinishMessage()
        {
            if (currentFrom is null) return;
            string body = string.Join(' ', currentBody.Where(part => part.Length > 0));
            if (string.IsNullOrWhiteSpace(body)) throw new JevException(InvalidImport);
            messages.Add((currentFrom, body));
            currentBody.Clear();
        }

        foreach (string line in Lines(text))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (IsDateHeading(line))
            {
                if (currentFrom is null) headingCount++;
                else
                {
                    // A date inside an active message may be its actual content. With plain
                    // clipboard text we cannot prove it is a separator, so retain it for review.
                    currentBody.Add(NormalizeBody(line));
                    preservedDates++;
                    joinedContinuation = true;
                }
                continue;
            }
            Match match = TimestampLine().Match(line);
            if (match.Success)
            {
                FinishMessage();
                // Recall notices are complete timestamped events, not a speaker's message.
                // Match the whole suffix only; quoted/discussed recall text must remain content.
                if (IsRecallNotice(match.Groups["rest"].Value, names))
                {
                    currentFrom = null;
                    skippedRecalls++;
                    continue;
                }
                string sender;
                string body;
                if (TryTabFields(match, out string tabName, out string tabBody))
                {
                    sender = tabName;
                    body = tabBody;
                    if (!names.Contains(sender, StringComparer.Ordinal)) throw new JevException(InvalidImport);
                }
                else
                {
                    string rest = match.Groups["rest"].Value.TrimStart();
                    sender = names.FirstOrDefault(name => rest.StartsWith(name, StringComparison.Ordinal)
                        && (rest.Length == name.Length || char.IsWhiteSpace(rest[name.Length]))) ?? "";
                    if (sender.Length == 0) throw new JevException(InvalidImport);
                    body = rest[sender.Length..].Trim();
                }
                currentFrom = sender == self ? "me" : "other";
                currentBody.Add(NormalizeBody(body));
                continue;
            }
            // Invalid or incomplete timestamps must not silently become somebody else's message body.
            if (TimestampLikeLine().IsMatch(line) || currentFrom is null) throw new JevException(InvalidImport);
            currentBody.Add(NormalizeBody(line));
            joinedContinuation = true;
        }
        FinishMessage();
        if (messages.Count == 0 || !messages.Any(message => message.From == "other"))
            throw new JevException((skippedRecalls > 0 ? $"已略過 {skippedRecalls} 筆「已收回訊息」系統通知。" : "")
                + "內容中找不到已確認的對方訊息，請檢查姓名與選取範圍。");
        int lastOther = messages.FindLastIndex(message => message.From == "other");
        bool repliedAfter = messages.Skip(lastOther + 1).Any(message => message.From == "me");
        var warnings = new List<string>();
        if (skippedRecalls > 0) warnings.Add($"已略過 {skippedRecalls} 筆「已收回訊息」系統通知，未納入對話分析。");
        if (headingCount > 0) warnings.Add("已略過日期標頭與訊息時間，請在分析前檢查轉換內容。");
        if (preservedDates > 0) warnings.Add($"有 {preservedDates} 行日期位於訊息內，已保留為正文；若是日期分隔線，可在套用後自行移除。");
        if (joinedContinuation) warnings.Add("同一則訊息的換行已合併為空格，文字內容保留。");
        if (repliedAfter) warnings.Add("對方最新訊息之後已有你的回覆；分析仍以對方最新訊息為目標，後續回覆保留為背景。");
        string transcript = string.Join(Environment.NewLine,
            messages.Select(message => $"{(message.From == "me" ? "我" : "對方")}：{message.Body}"));
        return new(transcript, messages.Count, repliedAfter, warnings.AsReadOnly(), skippedRecalls);
    }

    private static bool IsRecallNotice(string rest, IReadOnlyList<string> names)
    {
        string text = rest.Trim();
        if (text == RecallNotice) return true;
        return names.Any(name => text.StartsWith(name, StringComparison.Ordinal)
            && text[name.Length..].Trim() == RecallNotice);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new JevException("請先複製 LINE 對話文字。");
        if (text.Length > Transcript.MaximumCharacters)
            throw new JevException($"LINE 對話超過 {Transcript.MaximumCharacters:N0} 字元，請縮小選取範圍。");
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new JevException("LINE 複製內容包含無法處理的控制字元，請重新複製對話。");
    }

    private static string ValidateName(string name)
    {
        string value = (name ?? "").Trim();
        if (value.Length is < 1 or > 150 || value.Any(char.IsControl))
            throw new JevException("請填入「我」與「對方」的完整 LINE 顯示名稱。");
        return value;
    }

    private static bool TryTabFields(Match match, out string name, out string body)
    {
        string rest = match.Groups["rest"].Value;
        int tab = rest.IndexOf('\t');
        if (match.Groups["separator"].Value.Contains('\t') && tab >= 0)
        {
            name = rest[..tab].Trim();
            body = rest[(tab + 1)..].Trim();
            return true;
        }
        name = body = "";
        return false;
    }

    private static string NormalizeBody(string text) => text.Trim().Replace('\t', ' ');

    private static bool IsDateHeading(string line)
    {
        Match match = DateHeading().Match(line);
        if (!match.Success) return false;
        return DateTime.TryParseExact($"{match.Groups["year"].Value}-{match.Groups["month"].Value.PadLeft(2, '0')}-{match.Groups["day"].Value.PadLeft(2, '0')}",
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
