using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LineIntent.Core;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("Settings string never reveals API key", () =>
    {
        Assert(!Settings().ToString().Contains("test-only-key"), "settings leaked key");
        Assert(!JsonSerializer.Serialize(Settings()).Contains("test-only-key"), "settings JSON leaked key");
        return Task.CompletedTask;
    }),
    ("OpenRouter sends decisions protocol and seven typed questions", async () =>
    {
        using var http = Http(async request =>
        {
            Equal(request.Method, HttpMethod.Post);
            Equal(request.RequestUri!.AbsoluteUri, "https://openrouter.ai/api/alpha/decisions");
            Equal(request.Headers.Authorization!.Scheme, "Bearer");
            Equal(request.Headers.Authorization.Parameter, "test-only-key");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement root = body.RootElement;
            Equal(root.GetProperty("model").GetString(), "typesafe/jev-1.13");
            Equal(root.GetProperty("questions").EnumerateObject().Count(), 7);
            Equal(root.GetProperty("questions").GetProperty("danger_level").GetProperty("criteria").GetArrayLength(), 10);
            Assert(!root.TryGetProperty("messages", out _), "must not use Chat Completions protocol");
            JsonElement chat = root.GetProperty("state").GetProperty("chat");
            Equal(chat.GetProperty("latest_other_index").GetInt32(), 1);
            Equal(chat.GetProperty("messages")[1].GetProperty("text").GetString(), "明天方便嗎？");
            return Reply(Valid());
        });
        using var client = new JevClient(http);
        AnalysisResult result = await client.AnalyzeAsync("我：你好\n對方：明天方便嗎？", "朋友", Settings());
        Equal(result.Intent.Code, "request_action");
        Equal(result.Tension.Value, 3d);
        Equal(result.Need.Label, "具體行動或安排");
        Equal(result.Warnings.Count, 0);
    }),
    ("TypeSafe uses direct endpoint and alias", async () =>
    {
        using var http = Http(async request =>
        {
            Equal(request.RequestUri!.AbsoluteUri, "https://api.typesafe.ai/v1/systemone");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(json.RootElement.GetProperty("model").GetString(), "jev-latest");
            return Reply(Valid());
        });
        using var client = new JevClient(http);
        await client.AnalyzeAsync("對方：好嗎？", "", Settings() with { Provider = JevProvider.TypeSafe });
    }),
    ("Configured model overrides default", async () =>
    {
        using var http = Http(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(json.RootElement.GetProperty("model").GetString(), "typesafe/custom-model");
            return Reply(Valid());
        });
        using var client = new JevClient(http);
        await client.AnalyzeAsync("other: Hello", "", Settings() with { Model = "typesafe/custom-model" });
    }),
    ("Unknown speaker context is retained without attribution", async () =>
    {
        using var http = Http(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = json.RootElement.GetProperty("state").GetProperty("chat").GetProperty("messages");
            Equal(messages[0].GetProperty("from").GetString(), "unknown");
            Equal(messages[2].GetProperty("from").GetString(), "unknown");
            return Reply(Valid());
        });
        using var client = new JevClient(http);
        var result = await client.AnalyzeAsync("無法辨識的標題\n對方：好嗎？\n無法辨識的後文", "", Settings());
        Assert(result.Warnings.Any(w => w.Contains("未標示")), "unknown warning missing");
    }),
    ("Unlabelled transcript stops before network", () => RejectInput("hello", Settings())),
    ("Later self reply remains context and emits warning", async () =>
    {
        using var http = Http(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement chat = json.RootElement.GetProperty("state").GetProperty("chat");
            Equal(chat.GetProperty("latest_from").GetString(), "me");
            Equal(chat.GetProperty("latest_other_index").GetInt32(), 0);
            Equal(chat.GetProperty("has_self_reply_after_latest_other").GetBoolean(), true);
            Equal(chat.GetProperty("messages").GetArrayLength(), 2);
            return Reply(Valid());
        });
        using var client = new JevClient(http);
        var result = await client.AnalyzeAsync("對方：展覽在哪裡？\n我：我查一下", "朋友", Settings());
        Assert(result.Warnings.Any(w => w.Contains("之後已有你的回覆")), "self-reply warning missing");
    }),
    ("No meaningful other message stops before network", () => RejectInput("對方：\n標題", Settings())),
    ("Oversized input stops before network", () => RejectInput("對方：" + new string('a', Transcript.MaximumCharacters), Settings())),
    ("Missing key stops before network", () => RejectInput("對方：hello", Settings() with { ApiKey = "" })),
    ("Header injection key stops before network", () => RejectInput("對方：hello", Settings() with { ApiKey = "secret\r\nInjected: yes" })),
    ("Invalid provider stops before network", () => RejectInput("對方：hello", Settings() with { Provider = (JevProvider)42 })),
    ("Missing score does not silently become zero", () => RejectResponse(Mutate(j => j["answers"]!["danger_level"]!.AsObject().Remove("score")))),
    ("Missing answer does not silently become neutral", () => RejectResponse(Mutate(j => j["answers"]!.AsObject().Remove("literal_question")))),
    ("Invalid score range rejected", () => RejectResponse(Mutate(j => j["answers"]!["danger_level"]!["score"] = 12))),
    ("Wrong answer type rejected", () => RejectResponse(Mutate(j => j["answers"]!["true_intent"]!["type"] = "noul"))),
    ("Unknown option rejected", () => RejectResponse(Mutate(j => j["answers"]!["true_intent"]!["choice"] = "invented"))),
    ("Missing confidence rejected", () => RejectResponse(Mutate(j => j["answers"]!["best_action"]!.AsObject().Remove("confidence")))),
    ("Noul outside probability range rejected", () => RejectResponse(Mutate(j => j["answers"]!["literal_question"]!["noul"] = -0.1))),
    ("Incomplete probability distribution rejected", () => RejectResponse(Mutate(j => j["answers"]!["true_intent"]!["probabilities"]!.AsObject().Remove("uncertain")))),
    ("Nonnumeric probability rejected", () => RejectResponse(Mutate(j => j["answers"]!["true_intent"]!["probabilities"]!["uncertain"] = "no"))),
    ("Malformed JSON rejected", () => RejectResponse("{secret: invalid}")),
    ("Chat Completions response rejected", () => RejectResponse("{\"choices\":[{\"message\":{\"content\":\"secret\"}}]}")),
    ("Low confidence shown as uncertain", async () =>
    {
        string response = Mutate(j => j["answers"]!["true_intent"]!["confidence"] = .2);
        using var http = Http(_ => Task.FromResult(Reply(response)));
        using var client = new JevClient(http);
        var result = await client.AnalyzeAsync("對方：hello", "", Settings());
        Assert(result.Warnings.Any(w => w.Contains("信心偏低")), "low-confidence warning missing");
    }),
    ("HTTP errors never expose server body or key", async () =>
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError })
        {
            using var http = Http(_ => Task.FromResult(Reply("test-only-key private-chat", status)));
            using var client = new JevClient(http);
            await ThrowsSafe(() => client.AnalyzeAsync("對方：private-chat", "", Settings()));
        }
    }),
    ("Transport exception details are sanitized", async () =>
    {
        using var http = Http(_ => throw new HttpRequestException("test-only-key private-chat"));
        using var client = new JevClient(http);
        await ThrowsSafe(() => client.AnalyzeAsync("對方：private-chat", "", Settings()));
    }),
    ("Oversized service response rejected", () => RejectResponse(new string('x', 262145))),
    ("Caller cancellation is preserved", async () =>
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var http = Http(_ => throw new Exception("should not reach network"));
        using var client = new JevClient(http);
        try { await client.AnalyzeAsync("對方：hello", "", Settings(), cancel.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    })
};

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("Reply settings string and JSON never reveal key", () =>
    {
        Assert(!ReplyConfig().ToString().Contains("test-only-key"), "reply string leaked key");
        Assert(!JsonSerializer.Serialize(ReplyConfig()).Contains("test-only-key"), "reply JSON leaked key");
        return Task.CompletedTask;
    }),
    ("Reply request uses OpenRouter chat endpoint and Jev guidance", async () =>
    {
        int calls = 0;
        using var http = Http(async request =>
        {
            calls++;
            Equal(request.RequestUri!.AbsoluteUri, "https://openrouter.ai/api/v1/chat/completions");
            Equal(request.Method, HttpMethod.Post);
            Equal(request.Headers.Authorization!.Parameter, "test-only-key");
            string payload = await request.Content!.ReadAsStringAsync();
            Assert(!payload.Contains("test-only-key"), "key leaked to model payload");
            using JsonDocument json = JsonDocument.Parse(payload);
            JsonElement root = json.RootElement;
            Equal(root.GetProperty("model").GetString(), "test/provider-model");
            Equal(root.GetProperty("stream").GetBoolean(), false);
            Equal(root.GetProperty("reasoning").GetProperty("enabled").GetBoolean(), false);
            Equal(root.GetProperty("messages").GetArrayLength(), 2);
            Equal(root.GetProperty("messages")[0].GetProperty("role").GetString(), "system");
            Assert(!root.TryGetProperty("tools", out _) && !root.TryGetProperty("plugins", out _), "unexpected external capabilities");
            using JsonDocument context = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Equal(context.RootElement.GetProperty("jev").GetProperty("intent").GetProperty("code").GetString(), "request_action");
            Equal(context.RootElement.GetProperty("chat").GetProperty("messages")[0].GetProperty("from").GetString(), "unknown");
            return Reply(Completion("[\"可以，我先確認時間\",\"你方便什麼時候？\",\"好，我看看安排\"]"));
        });
        using var client = new ReplyClient(http);
        IReadOnlyList<string> result = await client.GenerateAsync("背景\n對方：明天方便嗎？", "朋友", ExampleAnalysis(), ReplyConfig());
        Equal(result.Count, 3);
        Equal(result[0], "可以，我先確認時間");
        Equal(calls, 1);
    }),
    ("Reply parser accepts a single fenced JSON array", async () =>
    {
        using var http = Http(_ => Task.FromResult(Reply(Completion("```json\n[\"好的\",\"我先確認\"]\n```"))));
        using var client = new ReplyClient(http);
        var replies = await client.GenerateAsync("對方：好嗎？", "", ExampleAnalysis(), ReplyConfig());
        Equal(replies.Count, 2);
    }),
    ("Reply parser retains valid single reply and removes duplicates", async () =>
    {
        using var http = Http(_ => Task.FromResult(Reply(Completion("[\"好的\",\"好的\"]"))));
        using var client = new ReplyClient(http);
        var replies = await client.GenerateAsync("對方：好嗎？", "", ExampleAnalysis(), ReplyConfig());
        Equal(replies.Count, 1);
    }),
    ("Reply model is required with no default", () => RejectReplyInput(ReplyConfig() with { Model = "" })),
    ("Reply key is required", () => RejectReplyInput(ReplyConfig() with { ApiKey = "" })),
    ("Reply key control injection stops before network", () => RejectReplyInput(ReplyConfig() with { ApiKey = "secret\r\nInjected: yes" })),
    ("Reply model controls stop before network", () => RejectReplyInput(ReplyConfig() with { Model = "provider/\0model" })),
    ("Reply long model stops before network", () => RejectReplyInput(ReplyConfig() with { Model = new string('x', 201) })),
    ("Reply unlabeled transcript stops before network", () => RejectReplyInput(ReplyConfig(), "unknown message")),
    ("Reply overlong transcript stops before network", () => RejectReplyInput(ReplyConfig(), "對方：" + new string('x', Transcript.MaximumCharacters))),
    ("Reply malformed envelope rejected", () => RejectReplyResponse("{private-chat not-json}")),
    ("Reply malformed candidate JSON rejected", () => RejectReplyResponse(Completion("['private-chat']"))),
    ("Reply object instead of array rejected", () => RejectReplyResponse(Completion("{\"reply\":\"private-chat\"}"))),
    ("Reply empty array rejected", () => RejectReplyResponse(Completion("[]"))),
    ("Reply four candidates rejected", () => RejectReplyResponse(Completion("[\"a\",\"b\",\"c\",\"d\"]"))),
    ("Reply empty candidate rejected", () => RejectReplyResponse(Completion("[\"   \"]"))),
    ("Reply nonstring candidate rejected", () => RejectReplyResponse(Completion("[3]"))),
    ("Reply candidate length cap enforced", () => RejectReplyResponse(Completion(JsonSerializer.Serialize(new[] { new string('x', ReplyClient.MaximumDraftCharacters + 1) })))),
    ("Reply candidate controls rejected", () => RejectReplyResponse(Completion(JsonSerializer.Serialize(new[] { "private-chat\nnext line" })))),
    ("Reply Unicode line separator rejected", () => RejectReplyResponse(Completion(JsonSerializer.Serialize(new[] { "private-chat\u2028next line" })))),
    ("Reply echoed key rejected without exposing it", () => RejectReplyResponse(Completion("[\"test-only-key\"]"))),
    ("Reply token-truncated generation rejected", () => RejectReplyResponse(Completion("[\"private-chat\"]", "length"))),
    ("Reply oversized HTTP body rejected", () => RejectReplyResponse(new string('x', 262145))),
    ("Reply HTTP status errors sanitized without retries", async () =>
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError })
        {
            int calls = 0;
            using var http = Http(_ => { calls++; return Task.FromResult(Reply("test-only-key private-chat", status)); });
            using var client = new ReplyClient(http);
            await ThrowsSafe(() => client.GenerateAsync("對方：private-chat", "", ExampleAnalysis(), ReplyConfig()));
            Equal(calls, 1);
        }
    }),
    ("Reply transport errors sanitized", async () =>
    {
        using var http = Http(_ => throw new HttpRequestException("test-only-key private-chat"));
        using var client = new ReplyClient(http);
        await ThrowsSafe(() => client.GenerateAsync("對方：private-chat", "", ExampleAnalysis(), ReplyConfig()));
    }),
    ("Reply caller cancellation preserved", async () =>
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var http = Http(_ => throw new Exception("should not reach network"));
        using var client = new ReplyClient(http);
        try { await client.GenerateAsync("對方：hello", "", ExampleAnalysis(), ReplyConfig(), cancel.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    })
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("LINE clipboard detection accepts timestamped lines", () =>
    {
        Assert(LineClipboardImporter.LooksLikeLineClipboard("17:31 Mira Stone 米拉 週六看展嗎？"), "timestamp not recognized");
        Assert(!LineClipboardImporter.LooksLikeLineClipboard("對方：週六看展嗎？"), "explicit transcript misdetected");
        Assert(!LineClipboardImporter.LooksLikeLineClipboard("2026.04.18 Saturday"), "date alone misdetected");
        return Task.CompletedTask;
    }),
    ("LINE suggestions infer repeated multiword name prefixes only", () =>
    {
        var names = LineClipboardImporter.SuggestNames("17:31 Mira Stone 米拉 週六看展嗎？\n17:32 杜映青 好啊\n17:33 Mira Stone 米拉 在市立館\n17:34 杜映青 幾點集合？");
        Equal(names.Count, 2);
        Assert(names.Contains("Mira Stone 米拉") && names.Contains("杜映青"), "suggested names differ");
        return Task.CompletedTask;
    }),
    ("LINE single-line suggestions do not invent full multiword names", () =>
    {
        var names = LineClipboardImporter.SuggestNames("17:31 Mira Stone 米拉 週六看展嗎？");
        Equal(names.Count, 1);
        Equal(names[0], "Mira");
        return Task.CompletedTask;
    }),
    ("LINE tab fields provide complete names without heuristics", () =>
    {
        var names = LineClipboardImporter.SuggestNames("17:31\tMira Stone 米拉\t週六看展嗎？\n17:32\t杜映青\t好啊");
        Assert(names.Contains("Mira Stone 米拉") && names.Contains("杜映青"), "tab names missing");
        var result = LineClipboardImporter.Convert("17:31\tMira Stone 米拉\t週六看展嗎？\n17:32\t杜映青\t好啊", "杜映青", "Mira Stone 米拉");
        Equal(result.MessageCount, 2);
        Equal(result.Transcript, $"對方：週六看展嗎？{Environment.NewLine}我：好啊");
        return Task.CompletedTask;
    }),
    ("LINE import maps confirmed full names and retains final self reply", () =>
    {
        string input = "17:31 Mira Stone 米拉 週六看展嗎？\n17:32 杜映青 好啊\n17:33 杜映青 哪個入口？\n17:34 Mira Stone 米拉 正門\n17:35 杜映青 我帶地圖";
        var result = LineClipboardImporter.Convert(input, "杜映青", "Mira Stone 米拉");
        Equal(result.MessageCount, 5);
        Assert(result.HasSelfReplyAfterLatestOther, "final reply not detected");
        Equal(result.Transcript, string.Join(Environment.NewLine, new[] { "對方：週六看展嗎？", "我：好啊", "我：哪個入口？", "對方：正門", "我：我帶地圖" }));
        var parsed = Transcript.Parse(result.Transcript);
        Equal(parsed.LatestOtherIndex, 3);
        Assert(parsed.HasSelfReplyAfterLatestOther, "parsed self context missing");
        return Task.CompletedTask;
    }),
    ("LINE latest other without later self has no already-replied flag", () =>
    {
        var result = LineClipboardImporter.Convert("17:31 杜映青 好啊\n17:32 Mira Stone 米拉 到時見", "杜映青", "Mira Stone 米拉");
        Assert(!result.HasSelfReplyAfterLatestOther, "incorrect already-replied flag");
        return Task.CompletedTask;
    }),
    ("LINE mapped names use longest whitespace-bounded match", () =>
    {
        var result = LineClipboardImporter.Convert("17:31 Mira Stone 入口在北邊\n17:32 Mira 好的", "Mira", "Mira Stone");
        Equal(result.Transcript, $"對方：入口在北邊{Environment.NewLine}我：好的");
        return Task.CompletedTask;
    }),
    ("LINE continuation text joins the same speaker without losing content", () =>
    {
        var result = LineClipboardImporter.Convert("17:31\tMira Stone 米拉\t展覽資訊：\r\n第一廳  北側\r\n第二廳\r\n17:32\t杜映青\t收到", "杜映青", "Mira Stone 米拉");
        Equal(result.Transcript, $"對方：展覽資訊： 第一廳  北側 第二廳{Environment.NewLine}我：收到");
        Equal(result.MessageCount, 2);
        Assert(result.Warnings.Any(w => w.Contains("換行")), "continuation warning missing");
        return Task.CompletedTask;
    }),
    ("LINE initial empty body may continue on the next line", () =>
    {
        var result = LineClipboardImporter.Convert("17:31\tMira Stone 米拉\t\n展覽資訊\n17:32\t杜映青\t收到", "杜映青", "Mira Stone 米拉");
        Equal(result.Transcript, $"對方：展覽資訊{Environment.NewLine}我：收到");
        return Task.CompletedTask;
    }),
    ("LINE recognized date headings are skipped with warning", () =>
    {
        var result = LineClipboardImporter.Convert("2026.04.18 Saturday\n17:31 Mira 週六見\n2026.04.19 星期日\n17:32 杜映青 收到", "杜映青", "Mira");
        Equal(result.MessageCount, 2);
        Assert(result.Warnings.Any(w => w.Contains("日期")), "date warning missing");
        return Task.CompletedTask;
    }),
    ("LINE timestamp with seconds is accepted", () =>
    {
        var result = LineClipboardImporter.Convert("7:31:02 Mira 到了\n07:32:58 杜映青 收到", "杜映青", "Mira");
        Equal(result.MessageCount, 2);
        return Task.CompletedTask;
    }),
    ("LINE unknown space-delimited sender fails whole import", () => ThrowsImport("17:31 Mira hello\n17:32 Unknown private-chat", "杜映青", "Mira")),
    ("LINE unknown tab-delimited sender fails whole import", () => ThrowsImport("17:31\tMira\thello\n17:32\tUnknown\tprivate-chat", "杜映青", "Mira")),
    ("LINE sender prefix without boundary is not a match", () => ThrowsImport("17:31 Mirabelle private-chat", "杜映青", "Mira")),
    ("LINE same display name for both sides is rejected", () => ThrowsImport("17:31 Mira private-chat", "Mira", "Mira")),
    ("LINE no opposing messages is rejected", () => ThrowsImport("17:31 Mira private-chat", "Mira", "杜映青")),
    ("LINE empty message without continuation is rejected", () => ThrowsImport("17:31\tMira\t\n17:32 杜映青 private-chat", "杜映青", "Mira")),
    ("LINE unknown preamble is not silently dropped", () => ThrowsImport("unknown private-chat\n17:31 Mira hello", "杜映青", "Mira")),
    ("LINE invalid timestamp cannot become continuation", () => ThrowsImport("17:31 Mira hello\n25:61 Unknown private-chat", "杜映青", "Mira")),
    ("LINE incomplete timestamp cannot become continuation", () => ThrowsImport("17:31 Mira hello\n17:32", "杜映青", "Mira")),
    ("LINE invalid calendar heading is rejected before messages", () => ThrowsImport("2026.02.30 Monday\n17:31 Mira private-chat", "杜映青", "Mira")),
    ("LINE input controls are rejected", () => ThrowsImport("17:31 Mira private-chat\0", "杜映青", "Mira")),
    ("LINE excessive input is rejected", () => ThrowsImport("17:31 Mira " + new string('x', Transcript.MaximumCharacters), "杜映青", "Mira")),
    ("LINE names cannot contain control characters", () => ThrowsImport("17:31 Mira private-chat", "杜\n映青", "Mira"))
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("Spatial OCR orders opposite sides by actual vertical position", () =>
    {
        var result = ChatLayout.Assemble(
            [new("週六見", new(30, 150, 90, 20)), new("想看展嗎？", new(320, 50, 100, 20))],
            [new(new(20, 140, 140, 45), "other"), new(new(300, 40, 150, 45), "me")], 500, 500);
        Equal(result.Transcript, $"我：想看展嗎？{Environment.NewLine}對方：週六見");
        Equal(result.Warnings.Count, 0);
        return Task.CompletedTask;
    }),
    ("Spatial OCR groups multi-line bubble in vertical order", () =>
    {
        var result = ChatLayout.Assemble(
            [new("下午再看特展", new(40, 100, 130, 20)), new("早上先看常設展", new(40, 50, 160, 20)), new("中午在館內吃飯", new(40, 75, 160, 20))],
            [new(new(25, 35, 220, 105), "other")], 500, 500);
        Equal(result.Messages.Count, 1);
        Equal(result.Transcript, "對方：早上先看常設展 中午在館內吃飯 下午再看特展");
        return Task.CompletedTask;
    }),
    ("Spatial OCR baseline fragments follow horizontal reading order", () =>
    {
        var result = ChatLayout.Assemble(
            [new("北門", new(90, 49, 40, 20)), new("入口在", new(35, 51, 50, 20)), new("請準時", new(35, 80, 80, 20))],
            [new(new(25, 35, 150, 80), "other")], 500, 500);
        Equal(result.Transcript, "對方：入口在 北門 請準時");
        return Task.CompletedTask;
    }),
    ("Spatial OCR does not infer speaker from a long bubble crossing center", () =>
    {
        var result = ChatLayout.Assemble([new("這是很長的一段展覽介紹", new(60, 50, 400, 20))],
            [new(new(30, 35, 450, 50), "other")], 500, 500);
        Equal(result.Messages[0].Speaker, "other");
        return Task.CompletedTask;
    }),
    ("Spatial OCR keeps short acknowledgments inside bubbles", () =>
    {
        var result = ChatLayout.Assemble([new("嗯", new(330, 50, 18, 20))],
            [new(new(315, 35, 50, 50), "me")], 500, 500);
        Equal(result.Transcript, "我：嗯");
        return Task.CompletedTask;
    }),
    ("Spatial OCR never strips timestamps or read text inside bubbles", () =>
    {
        var result = ChatLayout.Assemble([new("12:30", new(40, 50, 70, 20)), new("已讀", new(40, 80, 40, 20))],
            [new(new(25, 35, 130, 85), "other")], 500, 500);
        Equal(result.Transcript, "對方：12:30 已讀");
        Equal(result.IgnoredLineCount, 0);
        return Task.CompletedTask;
    }),
    ("Spatial OCR ignores exact outside UI metadata", () =>
    {
        var result = ChatLayout.Assemble(
            [new("12:30", new(200, 10, 50, 20)), new("已讀 12:31", new(200, 40, 90, 20)), new("2026.04.18 Saturday", new(160, 80, 170, 20)), new("入口在北邊", new(40, 150, 120, 20))],
            [new(new(25, 135, 170, 50), "other")], 500, 500);
        Equal(result.Transcript, "對方：入口在北邊");
        Equal(result.IgnoredLineCount, 3);
        return Task.CompletedTask;
    }),
    ("Spatial OCR preserves unassigned substantive text as unknown", () =>
    {
        var result = ChatLayout.Assemble(
            [new("約12:30見", new(300, 60, 120, 20)), new("展覽館管理員", new(40, 10, 150, 20)), new("好", new(40, 150, 20, 20))],
            [new(new(25, 135, 60, 50), "other")], 500, 500);
        Equal(result.Transcript, string.Join(Environment.NewLine, new[] { "未確認：展覽館管理員", "未確認：約12:30見", "對方：好" }));
        Assert(result.Warnings.Any(w => w.Contains("無法確認發話者")), "unassigned warning missing");
        return Task.CompletedTask;
    }),
    ("Spatial OCR does not confuse unassigned decimal or range with dates", () =>
    {
        var result = ChatLayout.Assemble([new("2.5", new(30, 40, 40, 20)), new("2-5", new(30, 80, 40, 20))], [], 500, 500);
        Equal(result.Transcript, $"未確認：2.5{Environment.NewLine}未確認：2-5");
        Equal(result.IgnoredLineCount, 0);
        return Task.CompletedTask;
    }),
    ("Spatial OCR does not attach distant text to nearest bubble", () =>
    {
        var result = ChatLayout.Assemble([new("另一段內容", new(350, 350, 100, 20))],
            [new(new(25, 35, 150, 50), "other")], 500, 500);
        Equal(result.Messages.Single(message => message.Text.Length > 0).Speaker, "unknown");
        Assert(result.Warnings.Any(w => w.Contains("沒有可辨識文字")), "empty bubble warning missing");
        return Task.CompletedTask;
    }),
    ("Spatial OCR overlapping bubbles remain ambiguous", () =>
    {
        var result = ChatLayout.Assemble([new("看完再討論", new(70, 50, 100, 20))],
            [new(new(25, 35, 180, 70), "other"), new(new(55, 35, 180, 70), "me")], 500, 500);
        Equal(result.Transcript, "未確認：看完再討論");
        Assert(result.Warnings.Any(w => w.Contains("多個氣泡")), "overlap warning missing");
        return Task.CompletedTask;
    }),
    ("Spatial OCR timestamp touching uncertain bubble geometry is retained", () =>
    {
        var result = ChatLayout.Assemble([new("12:30", new(0, 50, 300, 20))],
            [new(new(100, 35, 100, 50), "other")], 500, 500);
        Equal(result.Transcript, "未確認：12:30");
        Equal(result.IgnoredLineCount, 0);
        return Task.CompletedTask;
    }),
    ("Spatial OCR warns when upper or lower bubbles may be clipped", () =>
    {
        var result = ChatLayout.Assemble([new("開頭", new(40, 10, 50, 20)), new("末尾", new(340, 465, 50, 20))],
            [new(new(25, 0, 130, 45), "other"), new(new(325, 450, 130, 50), "me")], 500, 500);
        Assert(result.Warnings.Any(w => w.Contains("內容可能不完整")), "clipping warning missing");
        Equal(result.Messages.Count, 2);
        return Task.CompletedTask;
    }),
    ("Spatial OCR empty bubble warns even without text", () =>
    {
        var result = ChatLayout.Assemble([], [new(new(25, 35, 180, 60), "other")], 500, 500);
        Equal(result.Messages.Count, 1);
        Equal(result.Messages[0].Speaker, "other");
        Equal(result.Messages[0].Text, "");
        Equal(result.Messages[0].Bounds, new ChatRect(25, 35, 180, 60));
        Equal(result.Transcript, "");
        Assert(result.Warnings.Any(w => w.Contains("漏讀訊息")), "missing OCR warning absent");
        return Task.CompletedTask;
    }),
    ("Spatial OCR empty unknown bubble remains editable but excluded from transcript", () =>
    {
        var result = ChatLayout.Assemble([new("北門見", new(40, 150, 90, 20))],
            [new(new(25, 35, 180, 60), "unconfirmed"), new(new(25, 135, 130, 50), "other")], 500, 500);
        Equal(result.Messages.Count, 2);
        Equal(result.Messages[0].Speaker, "unknown");
        Equal(result.Messages[0].Text, "");
        Equal(result.Transcript, "對方：北門見");
        return Task.CompletedTask;
    }),
    ("Spatial OCR no bubble geometry preserves text for manual review", () =>
    {
        var result = ChatLayout.Assemble([new("可以看這一廳", new(40, 50, 130, 20)), new("12:30", new(200, 80, 50, 20))], [], 500, 500);
        Equal(result.Transcript, "未確認：可以看這一廳");
        Equal(result.IgnoredLineCount, 1);
        return Task.CompletedTask;
    }),
    ("Spatial OCR invalid bubble roles stay unknown", () =>
    {
        var result = ChatLayout.Assemble([new("這裡集合", new(40, 50, 90, 20))],
            [new(new(25, 35, 150, 50), "left")], 500, 500);
        Equal(result.Transcript, "未確認：這裡集合");
        Assert(result.Warnings.Count > 0, "unknown role warning missing");
        return Task.CompletedTask;
    }),
    ("Spatial OCR invalid line coordinates never discard content", () =>
    {
        var result = ChatLayout.Assemble([new("仍須保留", new(double.NaN, 50, 90, 20))], [], 500, 500);
        Equal(result.Transcript, "未確認：仍須保留");
        Assert(result.Warnings.Any(w => w.Contains("位置資料無效")), "invalid position warning missing");
        return Task.CompletedTask;
    }),
    ("Spatial OCR invalid image geometry cannot assign speakers", () =>
    {
        var result = ChatLayout.Assemble([new("仍須保留", new(40, 50, 90, 20))],
            [new(new(25, 35, 150, 50), "other")], 0, 0);
        Equal(result.Transcript, "未確認：仍須保留");
        return Task.CompletedTask;
    }),
    ("Spatial OCR equal-height bubbles use X as stable tie breaker", () =>
    {
        var result = ChatLayout.Assemble([new("右側", new(330, 50, 50, 20)), new("左側", new(40, 50, 50, 20))],
            [new(new(315, 35, 120, 50), "me"), new(new(25, 35, 120, 50), "other")], 500, 500);
        Equal(result.Transcript, $"對方：左側{Environment.NewLine}我：右側");
        return Task.CompletedTask;
    }),
    ("Spatial OCR output remains compatible with transcript parser", () =>
    {
        var result = ChatLayout.Assemble([new("幾點集合？", new(40, 50, 110, 20)), new("我查一下", new(330, 150, 100, 20)), new("截圖外的文字", new(40, 250, 150, 20))],
            [new(new(25, 35, 170, 50), "other"), new(new(315, 135, 150, 50), "me")], 500, 500);
        var parsed = Transcript.Parse(result.Transcript);
        Equal(parsed.LatestOtherIndex, 0);
        Assert(parsed.HasSelfReplyAfterLatestOther && parsed.HasUnknownSpeakers, "transcript context flags missing");
        return Task.CompletedTask;
    })
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("OCR geometry zero angle preserves coordinates", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, 0), new(10, 20, 30, 10));
        return Task.CompletedTask;
    }),
    ("OCR geometry null angle preserves coordinates", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, null), new(10, 20, 30, 10));
        return Task.CompletedTask;
    }),
    ("OCR geometry positive ninety degrees rotates clockwise", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, 90), new(70, 10, 10, 30));
        return Task.CompletedTask;
    }),
    ("OCR geometry negative ninety degrees rotates counterclockwise", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, -90), new(20, 60, 10, 30));
        return Task.CompletedTask;
    }),
    ("OCR geometry arbitrary angle encloses all four corners", () =>
    {
        double extent = 10 * Math.Sqrt(2);
        NearRect(OcrGeometry.ToImageCoordinates(new(40, 40, 20, 20), 100, 100, 100, 100, 45),
            new(50 - extent, 50 - extent, extent * 2, extent * 2));
        return Task.CompletedTask;
    }),
    ("OCR geometry scales original dimensions independently", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 200, 300, 0), new(20, 60, 60, 30));
        return Task.CompletedTask;
    }),
    ("OCR geometry rotates before nonuniform resize", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 200, 300, 90), new(140, 30, 20, 90));
        return Task.CompletedTask;
    }),
    ("OCR geometry uses OCR image center and keeps outside coordinates", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 200, 100, 200, 100, 90), new(120, -40, 10, 30));
        return Task.CompletedTask;
    }),
    ("OCR geometry full turns normalize consistently", () =>
    {
        NearRect(OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, 450), new(70, 10, 10, 30));
        return Task.CompletedTask;
    }),
    ("OCR geometry invalid dimensions are rejected", () =>
    {
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), invalid, 100, 100, 100, 0));
            ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, invalid, 100, 100, 0));
            ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, invalid, 100, 0));
            ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, invalid, 0));
        }
        return Task.CompletedTask;
    }),
    ("OCR geometry invalid rectangles and angles are rejected", () =>
    {
        foreach (ChatRect box in new[] { new ChatRect(0, 0, 0, 10), new ChatRect(double.NaN, 0, 10, 10), new ChatRect(0, 0, 10, -1) })
            ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(box, 100, 100, 100, 100, 0));
        ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(null!, 100, 100, 100, 100, 0));
        ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, double.NaN));
        ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), 100, 100, 100, 100, double.NegativeInfinity));
        return Task.CompletedTask;
    }),
    ("OCR geometry numeric overflow is rejected", () =>
    {
        ExpectGeometryError(() => OcrGeometry.ToImageCoordinates(new(10, 20, 30, 10), double.Epsilon, 100, double.MaxValue, 100, 0));
        return Task.CompletedTask;
    }),
    ("Spatial OCR ignores spaced Chinese timestamps outside bubbles", () =>
    {
        var result = ChatLayout.Assemble(
            [new("下午 1 1 : 28", new(250, 10, 150, 20)), new("上午 7 : 44", new(250, 40, 150, 20)), new("R e a d", new(250, 70, 100, 20)), new("北門見", new(40, 150, 90, 20))],
            [new(new(25, 135, 140, 50), "other")], 500, 500);
        Equal(result.Transcript, "對方：北門見");
        Equal(result.IgnoredLineCount, 3);
        return Task.CompletedTask;
    }),
    ("Spatial OCR preserves spaced time and read text inside bubbles", () =>
    {
        var result = ChatLayout.Assemble(
            [new("下午 1 1 : 28", new(40, 50, 150, 20)), new("R e a d", new(40, 80, 90, 20))],
            [new(new(25, 35, 190, 80), "other")], 500, 500);
        Equal(result.Transcript, "對方：下午 1 1 : 28 R e a d");
        Equal(result.IgnoredLineCount, 0);
        return Task.CompletedTask;
    })
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("LINE recall notice without sender delimiter is skipped and counted", () =>
    {
        var result = LineClipboardImporter.Convert("08:24 Alex Chen 陳小明 準備出門\n08:25 王小華已收回訊息  \n08:26 王小華 北門見", "Alex Chen 陳小明", "王小華");
        Equal(result.SkippedRecallCount, 1);
        Equal(result.MessageCount, 2);
        Equal(result.Transcript, "我：準備出門" + Environment.NewLine + "對方：北門見");
        Assert(result.Warnings.Any(w => w.Contains("1 筆") && w.Contains("未納入")), "recall count warning missing");
        return Task.CompletedTask;
    }),
    ("LINE recall supports spaces tabs and trailing fullwidth whitespace", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 王小華 北門見\n08:21 Alex Chen 陳小明已收回訊息\n08:22 王小華 已收回訊息　\n08:23\t王小華\t已收回訊息\t \n08:24 Alex Chen 陳小明\t已收回訊息", "Alex Chen 陳小明", "王小華");
        Equal(result.SkippedRecallCount, 4);
        Equal(result.MessageCount, 1);
        Equal(result.Transcript, "對方：北門見");
        return Task.CompletedTask;
    }),
    ("LINE timestamped anonymous recall notice is skipped", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 已收回訊息  \n08:21 王小華 北門見", "Alex", "王小華");
        Equal(result.SkippedRecallCount, 1);
        Equal(result.Transcript, "對方：北門見");
        return Task.CompletedTask;
    }),
    ("LINE recall separates messages without duplicating multiline content", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 Alex 第一段\n第二段\n08:21 王小華已收回訊息\n08:22 王小華 北門見", "Alex", "王小華");
        Equal(result.Transcript, "我：第一段 第二段" + Environment.NewLine + "對方：北門見");
        Equal(result.MessageCount, 2);
        return Task.CompletedTask;
    }),
    ("LINE recall does not become latest other message", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 王小華 北門見\n08:21 Alex 收到\n08:22 王小華已收回訊息", "Alex", "王小華");
        Assert(result.HasSelfReplyAfterLatestOther, "trailing recall changed latest target");
        var parsed = Transcript.Parse(result.Transcript);
        Equal(parsed.LatestOtherIndex, 0);
        Equal(parsed.Messages.Count, 2);
        return Task.CompletedTask;
    }),
    ("LINE conversations mentioning recall are preserved", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 王小華 我看到已收回訊息\n08:21 Alex 「已收回訊息」是系統通知\n08:22 王小華 已收回訊息，但我有看到", "Alex", "王小華");
        Equal(result.SkippedRecallCount, 0);
        Equal(result.MessageCount, 3);
        Assert(result.Transcript.Contains("已收回訊息，但我有看到"), "ordinary sentence was deleted");
        return Task.CompletedTask;
    }),
    ("LINE untimestamped quoted continuation is not treated as an event", () =>
    {
        var result = LineClipboardImporter.Convert("08:20 王小華 畫面上写著\n已收回訊息", "Alex", "王小華");
        Equal(result.SkippedRecallCount, 0);
        Assert(result.Transcript.Contains("已收回訊息"), "continuation was deleted");
        return Task.CompletedTask;
    }),
    ("LINE unknown recall sender still fails safely", () => ThrowsImport("08:20 王小華 北門見\n08:21 Unknown已收回訊息", "Alex", "王小華")),
    ("LINE incomplete earlier message is not hidden by recall", () => ThrowsImport("08:20 Alex\n08:21 王小華已收回訊息\n08:22 王小華 北門見", "Alex", "王小華")),
    ("LINE recall-only input reports skipped count but cannot analyze", () =>
    {
        try { LineClipboardImporter.Convert("08:20 王小華已收回訊息\n08:21 Alex已收回訊息", "Alex", "王小華"); }
        catch (JevException error)
        {
            Assert(error.Message.Contains("2 筆") && error.Message.Contains("找不到"), "recall-only explanation missing");
            return Task.CompletedTask;
        }
        throw new Exception("Recall-only transcript was accepted");
    })
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("OCR review skips blank unknown rows without errors", () =>
    {
        var result = OcrReview.Validate([new("unknown", "", true), new("other", "北門見")]);
        Assert(result.CanApply, "skipped blank row incorrectly blocked apply");
        Equal(result.SkippedCount, 1);
        Equal(result.Transcript, "對方：北門見");
        return Task.CompletedTask;
    }),
    ("OCR review role selection alone cannot fill missing text", () =>
    {
        var result = OcrReview.Validate([new("other", ""), new("other", "北門見")]);
        Equal(result.Issues.Single().RowIndex, 0);
        Equal(result.Issues.Single().Field, "text");
        Equal(result.Transcript, "");
        return Task.CompletedTask;
    }),
    ("OCR review identifies remaining row after another is skipped", () =>
    {
        var result = OcrReview.Validate([new("unknown", "工具列"), new("other", "", true), new("other", "北門見")]);
        Equal(result.Issues.Single().RowIndex, 0);
        Assert(result.Issues.Single().Message.Contains("第 1 列"), "missing explicit row number");
        return Task.CompletedTask;
    }),
    ("OCR review manual single characters are preserved", () =>
    {
        var result = OcrReview.Validate([new("other", "嗯"), new("me", "知道了"), new("other", "唉")]);
        Assert(result.CanApply, "corrected text was rejected");
        Equal(Transcript.Parse(result.Transcript).Messages[2].Text, "唉");
        return Task.CompletedTask;
    }),
    ("OCR review all skipped needs an actual other message", () =>
    {
        var result = OcrReview.Validate([new("other", "", true), new("me", "收到")]);
        Assert(!result.CanApply && result.Issues.Single().Field == "conversation", "missing other message accepted");
        return Task.CompletedTask;
    }),
    ("OCR review unknown speaker remains explicit even with text", () =>
    {
        var result = OcrReview.Validate([new("unknown", "嗯"), new("other", "你好")]);
        Equal(result.Issues.Single().Field, "speaker");
        return Task.CompletedTask;
    }),
    ("OCR review joins edited lines and omits skipped text", () =>
    {
        var result = OcrReview.Validate([new("other", "北門\r\n集合"), new("unknown", "工具列", true)]);
        Equal(result.Transcript, "對方：北門 集合");
        return Task.CompletedTask;
    }),
    ("OCR review refuses oversized edited transcript", () =>
    {
        var result = OcrReview.Validate([new("other", new string('a', Transcript.MaximumCharacters))]);
        Assert(!result.CanApply && result.Issues.Any(issue => issue.Field == "conversation"), "long review text accepted");
        return Task.CompletedTask;
    })
});

checks.AddRange(new (string Name, Func<Task> Run)[]
{
    ("P2 recall does not shorten suggested multiword names", () =>
    {
        const string input = "08:00 Alex Chen 陳小明 北門見\n08:01 王小華 好\n08:02 Alex Chen 陳小明已收回訊息\n08:03 Alex Chen 陳小明 下午見";
        var names = LineClipboardImporter.SuggestNames(input);
        Assert(names.Contains("Alex Chen 陳小明") && !names.Contains("Alex Chen"), "full name was shortened");
        var result = LineClipboardImporter.Convert(input, names[0], names[1]);
        Equal(result.SkippedRecallCount, 1);
        Assert(!result.Transcript.Contains("已收回訊息"), "notice entered conversation");
        return Task.CompletedTask;
    }),
    ("P2 multiple recall notices do not change name suggestion", () =>
    {
        var names = LineClipboardImporter.SuggestNames("08:00 Alex Chen 陳小明 北門見\n08:01 Alex Chen 陳小明已收回訊息\n08:02 Alex Chen 陳小明 已收回訊息  \n08:03 Alex Chen 陳小明 下午見");
        Equal(names.Single(), "Alex Chen 陳小明");
        return Task.CompletedTask;
    }),
    ("P2 date in message body survives import", () =>
    {
        var result = LineClipboardImporter.Convert("08:00 王小華 下次活動日期\n2026/10/01\n08:01 Alex 好", "Alex", "王小華");
        Assert(result.Transcript.Contains("下次活動日期 2026/10/01"), "body date lost");
        Assert(result.Warnings.Any(w => w.Contains("保留為正文")), "ambiguous date warning absent");
        return Task.CompletedTask;
    }),
    ("P2 date with weekday and subsequent text stays in same message", () =>
    {
        var result = LineClipboardImporter.Convert("08:00 王小華 日期如下\n2026/10/01 星期四\n請先保留時間\n08:01 Alex 好", "Alex", "王小華");
        Equal(result.MessageCount, 2);
        Assert(result.Transcript.Contains("2026/10/01 星期四 請先保留時間"), "date continuation lost");
        return Task.CompletedTask;
    }),
    ("P2 leading date header is still skipped", () =>
    {
        var result = LineClipboardImporter.Convert("2026/10/01 星期四\n08:00 王小華 北門見", "Alex", "王小華");
        Equal(result.Transcript, "對方：北門見");
        Assert(result.Warnings.Any(w => w.Contains("略過日期")), "leading date warning absent");
        return Task.CompletedTask;
    }),
    ("P2 repeated body dates remain intact", () =>
    {
        var result = LineClipboardImporter.Convert("08:00 王小華 可選的日期\n2026-10-01\n2026-10-02\n08:01 Alex 好", "Alex", "王小華");
        Assert(result.Transcript.Contains("2026-10-01 2026-10-02"), "multiple dates lost");
        return Task.CompletedTask;
    })
});

int passed = 0;
foreach (var check in checks)
{
    try { await check.Run(); Console.WriteLine($"PASS {check.Name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {check.Name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine($"{passed}/{checks.Count} passed");
return passed == checks.Count ? 0 : 1;

static JevSettings Settings() => new() { ApiKey = "test-only-key" };
static void NearRect(ChatRect actual, ChatRect expected)
{
    Assert(Math.Abs(actual.X - expected.X) < 1e-8 && Math.Abs(actual.Y - expected.Y) < 1e-8
        && Math.Abs(actual.Width - expected.Width) < 1e-8 && Math.Abs(actual.Height - expected.Height) < 1e-8,
        "geometry differs from analytical expectation");
}
static void ExpectGeometryError(Action action)
{
    try { action(); }
    catch (ArgumentException error)
    {
        Assert(error.InnerException is null, "geometry error should have no inner details");
        return;
    }
    throw new Exception("Expected invalid OCR geometry to be rejected");
}
static Task ThrowsImport(string input, string self, string other) => ThrowsSafe(() =>
{
    LineClipboardImporter.Convert(input, self, other);
    return Task.CompletedTask;
});
static ReplySettings ReplyConfig() => new() { ApiKey = "test-only-key", Model = "test/provider-model" };
static AnalysisResult ExampleAnalysis() => new(
    new("request_action", "希望你採取具體行動", .8, .9),
    new("action", "具體行動或安排", .8, .9),
    new("make_plan", "商量時間、地點或安排", .8, .9),
    new(2, .8), .9, .8, .8, Array.Empty<string>());
static string Completion(string content, string reason = "stop") => JsonSerializer.Serialize(new
{ choices = new[] { new { finish_reason = reason, message = new { role = "assistant", content } } } });
static async Task RejectReplyInput(ReplySettings settings, string transcript = "對方：hello")
{
    using var http = Http(_ => throw new Exception("invalid reply input reached network"));
    using var client = new ReplyClient(http);
    await ThrowsSafe(() => client.GenerateAsync(transcript, "", ExampleAnalysis(), settings));
}
static async Task RejectReplyResponse(string response)
{
    using var http = Http(_ => Task.FromResult(Reply(response)));
    using var client = new ReplyClient(http);
    await ThrowsSafe(() => client.GenerateAsync("對方：hello", "", ExampleAnalysis(), ReplyConfig()));
}
static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
static void Equal<T>(T actual, T expected) => Assert(EqualityComparer<T>.Default.Equals(actual, expected), "values differ");
static HttpClient Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => new(new FakeHandler(action));
static HttpResponseMessage Reply(string value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
{ Content = new StringContent(value, Encoding.UTF8, "application/json") };
static async Task ThrowsSafe(Func<Task> action)
{
    try { await action(); }
    catch (JevException error)
    {
        Assert(!error.ToString().Contains("test-only-key") && !error.ToString().Contains("private-chat"), "sensitive data leaked");
        Assert(error.InnerException is null, "exception must not retain sensitive inner error");
        return;
    }
    throw new Exception("Expected JevException");
}
static async Task RejectInput(string text, JevSettings settings)
{
    using var http = Http(_ => throw new Exception("invalid input reached network"));
    using var client = new JevClient(http);
    await ThrowsSafe(() => client.AnalyzeAsync(text, "", settings));
}
static async Task RejectResponse(string value)
{
    using var http = Http(_ => Task.FromResult(Reply(value)));
    using var client = new JevClient(http);
    await ThrowsSafe(() => client.AnalyzeAsync("對方：hello", "", Settings()));
}
static string Mutate(Action<JsonNode> change)
{
    JsonNode node = JsonNode.Parse(Valid())!;
    change(node);
    return node.ToJsonString();
}
static string Valid()
{
    object Choice(string choice, string[] options) => new
    { type = "choice", choice, confidence = .8, probabilities = options.ToDictionary(k => k, k => k == choice ? 1d : 0d) };
    return JsonSerializer.Serialize(new
    {
        model = "jev-1.13.0",
        answers = new Dictionary<string, object>
        {
            ["true_intent"] = Choice("request_action", ["confirm_you_care", "vent_anger", "request_action", "seek_explanation", "casual_chat", "close_topic", "uncertain"]),
            ["she_needs"] = Choice("action", ["apology", "action", "explanation", "care", "nothing", "uncertain"]),
            ["best_action"] = Choice("make_plan", ["check_history", "apologize", "give_commitment", "explain", "acknowledge", "say_less", "make_plan", "clarify"]),
            ["danger_level"] = new { type = "score", score = 3d, confidence = .8, probabilities = Enumerable.Range(0, 10).ToDictionary(k => k.ToString(), k => k == 3 ? 1d : 0d) },
            ["literal_question"] = new { type = "noul", noul = .9 },
            ["should_reply_now"] = new { type = "noul", noul = .8 },
            ["tension_resolved"] = new { type = "noul", noul = .5 }
        },
        usage = new { input_tokens = 10, output_tokens = 20 }
    });
}

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => action(request);
}
