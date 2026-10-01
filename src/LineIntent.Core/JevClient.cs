using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LineIntent.Core;

public sealed class JevClient : IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private const string InvalidResponse = "判斷服務回覆格式不完整，未產生分析結果。請重試或檢查模型設定。";

    public JevClient(HttpClient? client = null)
    {
        ownsClient = client is null;
        // A redirect must never forward the conversation or credential to another host.
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AnalysisResult> AnalyzeAsync(string transcript, string relationship,
        JevSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(settings);
        ParsedTranscript parsed = Transcript.Parse(transcript);
        string key = (settings.ApiKey ?? "").Trim();
        if (key.Length == 0) throw new JevException("請先在設定填入所選服務商的 API Key。");
        if (key.Length > 4096 || key.Any(char.IsControl)) throw new JevException("API Key 格式不正確，請重新填入。");
        if (!Enum.IsDefined(settings.Provider)) throw new JevException("請選擇 OpenRouter 或 TypeSafe。");
        bool openRouter = settings.Provider == JevProvider.OpenRouter;
        string endpoint = openRouter ? "https://openrouter.ai/api/alpha/decisions" : "https://api.typesafe.ai/v1/systemone";
        string model = string.IsNullOrWhiteSpace(settings.Model)
            ? (openRouter ? "typesafe/jev-1.13" : "jev-latest") : settings.Model.Trim();
        if (model.Length > 200 || model.Any(char.IsWhiteSpace) || model.Any(char.IsControl))
            throw new JevException("模型 ID 格式不正確，請使用服務商提供的模型名稱。");
        string relation = (relationship ?? "").Trim();
        if (relation.Length > 1000) throw new JevException("關係背景過長，請縮短至 1,000 字元以內。");

        var payload = new
        {
            model,
            state = new
            {
                chat = new
                {
                    relationship = relation.Length > 0 ? relation : "Relationship unspecified; do not assume romance, hierarchy, or gender.",
                    messages = parsed.Messages.Select(m => new { from = m.From, text = m.Text }),
                    latest_from = parsed.Messages.LastOrDefault(message => message.From != "unknown")?.From ?? "unknown",
                    latest_other_index = parsed.LatestOtherIndex,
                    has_self_reply_after_latest_other = parsed.HasSelfReplyAfterLatestOther,
                    capture_note = "User-reviewed transcript. Speaker labels are authoritative. Unlabelled lines have unknown authors. OCR may be imperfect."
                }
            },
            questions = JevQuestions.Create()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            // No automatic retries: a manual retry is visible and prevents duplicate billable calls.
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new JevException(InvalidResponse);
            await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int size;
            while ((size = await body.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + size > MaximumResponseBytes) throw new JevException(InvalidResponse);
                buffer.Write(chunk, 0, size);
            }
            return ParseResponse(buffer.ToArray(), parsed.HasUnknownSpeakers, parsed.HasSelfReplyAfterLatestOther);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new JevException("判斷服務等待超過 35 秒。請檢查連線後重試。"); }
        catch (HttpRequestException)
        { throw new JevException("無法連線至判斷服務。請檢查網路與服務商狀態。"); }
        catch (IOException)
        { throw new JevException("讀取判斷服務回覆失敗。請檢查連線後重試。"); }
    }

    private static JevException StatusError(HttpStatusCode status) => new((int)status switch
    {
        401 => "API Key 被拒絕（HTTP 401）。請確認金鑰與服務商相符。",
        402 => "服務商額度不足（HTTP 402）。請檢查帳戶額度。",
        403 => "此帳戶無權使用判斷服務（HTTP 403）。請檢查服務商權限。",
        404 => "找不到模型或判斷端點（HTTP 404）。請檢查模型 ID。",
        422 => "判斷請求遭服務商拒絕（HTTP 422）。請確認使用 Jev 判斷模型。",
        429 => "判斷服務要求降低頻率（HTTP 429）。請稍後手動重試。",
        529 => "判斷服務目前過載（HTTP 529）。請稍後手動重試。",
        >= 300 and < 400 => "服務商要求重新導向，已停止傳送。請確認服務商端點狀態。",
        >= 500 => "判斷服務暫時無法使用。請稍後手動重試。",
        _ => "判斷服務未接受請求。請檢查服務商與模型設定。"
    });

    private static AnalysisResult ParseResponse(byte[] bytes, bool unknownSpeakers, bool selfRepliedAfter)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement answers = document.RootElement.GetProperty("answers");
            if (answers.ValueKind != JsonValueKind.Object) throw new JevException(InvalidResponse);
            ChoiceResult intent = ReadChoice(answers, "true_intent", JevQuestions.IntentLabels);
            ChoiceResult need = ReadChoice(answers, "she_needs", JevQuestions.NeedLabels);
            ChoiceResult action = ReadChoice(answers, "best_action", JevQuestions.ActionLabels);
            JsonElement tension = ReadAnswer(answers, "danger_level", "score");
            double score = Number(tension, "score", 9);
            double scoreConfidence = Number(tension, "confidence");
            ReadProbabilities(tension, Enumerable.Range(0, 10).Select(n => n.ToString()).ToArray());
            var warnings = new List<string>();
            if (unknownSpeakers) warnings.Add("未標示發話者的文字只作背景參考；無法確認是誰說的。");
            if (selfRepliedAfter) warnings.Add("對方最新訊息之後已有你的回覆；這份分析以對方最新訊息為目標，後續回覆保留為背景，並非建議再發一次訊息。");
            if (intent.Confidence < .6 || need.Confidence < .6 || action.Confidence < .6 || scoreConfidence < .6)
                warnings.Add("部分判斷的模型信心偏低，建議先確認對方意思。");
            if (intent.Code == "uncertain" || need.Code == "uncertain")
                warnings.Add("目前內容不足以支持明確結論，補充相關前文會更有幫助。");
            return new(intent, need, action, new(score, scoreConfidence),
                Number(ReadAnswer(answers, "literal_question", "noul"), "noul"),
                Number(ReadAnswer(answers, "should_reply_now", "noul"), "noul"),
                Number(ReadAnswer(answers, "tension_resolved", "noul"), "noul"), warnings.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new JevException(InvalidResponse); }
    }

    private static JsonElement ReadAnswer(JsonElement answers, string id, string type)
    {
        JsonElement answer = answers.GetProperty(id);
        if (answer.GetProperty("type").GetString() != type) throw new JevException(InvalidResponse);
        return answer;
    }

    private static double Number(JsonElement node, string name, double maximum = 1)
    {
        JsonElement value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number)
            || !double.IsFinite(number) || number < 0 || number > maximum)
            throw new JevException(InvalidResponse);
        return number;
    }

    private static ChoiceResult ReadChoice(JsonElement answers, string id, IReadOnlyDictionary<string, string> labels)
    {
        JsonElement answer = ReadAnswer(answers, id, "choice");
        string? code = answer.GetProperty("choice").GetString();
        if (code is null || !labels.TryGetValue(code, out string? label)) throw new JevException(InvalidResponse);
        JsonElement probabilities = ReadProbabilities(answer, labels.Keys);
        return new(code, label, Number(answer, "confidence"), Number(probabilities, code));
    }

    private static JsonElement ReadProbabilities(JsonElement answer, IEnumerable<string> expectedKeys)
    {
        JsonElement probabilities = answer.GetProperty("probabilities");
        if (probabilities.ValueKind != JsonValueKind.Object) throw new JevException(InvalidResponse);
        string[] expected = expectedKeys.ToArray();
        if (probabilities.EnumerateObject().Count() != expected.Length) throw new JevException(InvalidResponse);
        double total = expected.Sum(key => Number(probabilities, key));
        // Permit serialization rounding while rejecting materially broken distributions.
        if (Math.Abs(total - 1) > .025) throw new JevException(InvalidResponse);
        return probabilities;
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
