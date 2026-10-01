using System.Text.RegularExpressions;

namespace LineIntent.Core;

public sealed record TranscriptMessage(string From, string Text);

public sealed record ParsedTranscript(IReadOnlyList<TranscriptMessage> Messages, int LatestOtherIndex,
    bool HasUnknownSpeakers, bool HasSelfReplyAfterLatestOther = false);

public static partial class Transcript
{
    public const int MaximumCharacters = 24000;

    [GeneratedRegex(@"^\s*(我|自己|對方|对方|me|other)\s*[:：]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerPrefix();

    /// <summary>Unlabelled lines are context, never inferred to be the other person's words.</summary>
    public static ParsedTranscript Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new JevException("請先擷取或貼上對話，並標示「我：」與「對方：」。");
        if (text.Length > MaximumCharacters)
            throw new JevException($"對話超過 {MaximumCharacters:N0} 字元，請保留與最新訊息有關的內容。");

        var messages = new List<TranscriptMessage>();
        int latestOther = -1;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match match = SpeakerPrefix().Match(line);
            string who = "unknown";
            string body = line.Trim();
            if (match.Success)
            {
                string prefix = match.Groups[1].Value;
                who = prefix is "我" or "自己" || prefix.Equals("me", StringComparison.OrdinalIgnoreCase)
                    ? "me" : "other";
                body = match.Groups[2].Value.Trim();
                if (body.Length == 0) continue;
                if (who == "other") latestOther = messages.Count;
            }
            messages.Add(new(who, body));
        }
        if (latestOther < 0)
            throw new JevException("尚未標示對方訊息。請在確定由對方說出的每段文字前加上「對方：」。");
        return new(messages.AsReadOnly(), latestOther, messages.Any(m => m.From == "unknown"),
            messages.Skip(latestOther + 1).Any(m => m.From == "me"));
    }
}
