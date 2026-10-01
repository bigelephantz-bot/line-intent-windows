using System.Text;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Net;
using System.Text.Json;
using LineIntent.Capture;
using LineIntent.Core;
using LineIntent.Settings;

namespace LineIntent;

/// <summary>Explicit local smoke test with synthetic text only. Never reads real chat or calls an API.</summary>
internal static class Diagnostics
{
    public static int CaptureReport(string[] args)
    {
        var index = Array.IndexOf(args, "--capture-diagnostics");
        if (index < 0 || index + 1 >= args.Length) return 2;
        var reportPath = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        // Metadata only. No title, screenshot, OCR, clipboard, configuration, or network access.
        File.WriteAllLines(reportPath, LineCapture.GetDiagnostics(), new UTF8Encoding(false));
        return 0;
    }

    public static int Run(string[] args)
    {
        var report = new List<string>();
        var destination = args.Length > 1 && args[1] != "--self-test" ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var failures = 0;
        var checkCount = 0;
        void Prepare(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
        }
        void Check(string name, Action test)
        {
            checkCount++;
            try { test(); report.Add("PASS " + name); }
            catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.GetType().Name); }
        }
        Check("WinForms main layout and render", () =>
        {
            using var form = new MainForm(new AppSettings(), enumerateWindowsOnShown: false);
            Prepare(form);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(destination, "main-window.png"));
        });
        Check("Settings dialog layout and render", () =>
        {
            using var form = new SettingsDialog(new AppSettings());
            Prepare(form);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(destination, "settings-window.png"));
        });
        Check("Intent and drafts display independently and clear together", () =>
        {
            using var form = new MainForm(new AppSettings { EnableReplySuggestions = true }, enumerateWindowsOnShown: false);
            Prepare(form);
            var analysis = new AnalysisResult(new("request_action", "希望確認明天的安排", .8, .9),
                new("action", "確認時間與地點", .8, .9), new("make_plan", "一起確認可行時間", .8, .9),
                new(1, .8), .9, .8, .9, Array.Empty<string>());
            form.Render(analysis);
            var intent = (RichTextBox)form.Controls.Find("IntentResult", true).Single();
            var draft = (RichTextBox)form.Controls.Find("DraftResult", true).Single();
            var before = intent.Text;
            form.ShowDrafts(new[] { "可以，明天下午兩點方便嗎？", "我先確認時間，晚點跟你說", "好，我們再確認地點" });
            if (intent.Text != before || !draft.Text.Contains("明天下午兩點") || draft.Text.Contains("模型信心"))
                throw new InvalidDataException();
            if (intent.RectangleToScreen(intent.ClientRectangle).IntersectsWith(draft.RectangleToScreen(draft.ClientRectangle)))
                throw new InvalidDataException();
            using var preview = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
            preview.Save(Path.Combine(destination, "separate-results.png"));
            form.ClearResult();
            if (intent.Text.Contains("希望確認明天的安排") || draft.Text.Contains("明天下午兩點")) throw new InvalidDataException();
        });
        Check("LINE import requires identity and preserves latest self reply", () =>
        {
            const string sample = "07:42 Alex Chen 陳小明 今天改去美術館\r\n07:44 王小華 我先查開放時間\r\n07:45 Alex Chen 陳小明 好，我查交通";
            using var form = new LineImportDialog(sample);
            Prepare(form);
            if (form.Result is not null) throw new InvalidDataException("Initial identity must not be guessed");
            var self = (ComboBox)form.Controls.Find("SelfName", true).Single();
            self.Text = "Alex Chen 陳小明";
            if (form.Result?.MessageCount != 3 || !form.Result.HasSelfReplyAfterLatestOther) throw new InvalidDataException();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(destination, "line-import.png"));
        });
        Check("Name text area opens dropdown and manual edits survive mode change", () =>
        {
            const string source = "08:00 Alex Chen 陳小明 北門見\n08:01 王小華 好\n08:02 Alex Chen 陳小明 下午見";
            using var form = new LineImportDialog(source);
            Prepare(form);
            foreach (var name in new[] { "SelfName", "OtherName" })
            {
                var field = (ComboBox)form.Controls.Find(name, true).Single();
                if (field.DropDownStyle != ComboBoxStyle.DropDownList) throw new InvalidDataException();
                var point = new nint((field.Height / 2 << 16) | 16); // Text area, far from the arrow.
                var opened = false;
                EventHandler handler = (_, _) => opened = true;
                field.DropDown += handler;
                PostMessage(field.Handle, 0x0201, new nint(1), point);
                PostMessage(field.Handle, 0x0202, 0, point);
                Application.DoEvents();
                field.DroppedDown = false;
                field.DropDown -= handler;
                if (!opened) throw new InvalidDataException("Text-area click did not open the native list");
                opened = false;
                field.DropDown += handler;
                var label = form.Controls.Find(name + "Label", true).Single();
                var labelPoint = new nint((label.Height / 2 << 16) | 16);
                PostMessage(label.Handle, 0x0201, new nint(1), labelPoint);
                PostMessage(label.Handle, 0x0202, 0, labelPoint);
                Application.DoEvents();
                field.DroppedDown = false;
                field.DropDown -= handler;
                if (!opened) throw new InvalidDataException("Row-label click did not open the native list");
            }
            var manual = (CheckBox)form.Controls.Find("ManualNames", true).Single();
            var self = (ComboBox)form.Controls.Find("SelfName", true).Single();
            manual.Checked = true;
            if (self.DropDownStyle != ComboBoxStyle.DropDown) throw new InvalidDataException();
            self.Text = "自訂完整姓名";
            manual.Checked = false;
            if (self.Text != "自訂完整姓名" || self.DropDownStyle != ComboBoxStyle.DropDownList) throw new InvalidDataException();
            self.SelectedItem = "Alex Chen 陳小明";
            if (form.Result?.MessageCount != 3) throw new InvalidDataException();
        });
        Check("DPAPI encrypted settings round trip", () =>
        {
            var path = Path.Combine(destination, "test-settings.json");
            var settings = new AppSettings { OpenRouterKey = "synthetic-router-key", TypeSafeKey = "synthetic-typesafe-key", LlmApiKey = "synthetic-llm-key", LlmModel = "synthetic/model", Relationship = "測試朋友" };
            SettingsStore.Save(settings, path);
            var stored = File.ReadAllText(path);
            if (stored.Contains("synthetic-router-key") || stored.Contains("synthetic-typesafe-key") || stored.Contains("synthetic-llm-key")) throw new InvalidDataException();
            var plain = System.Text.Json.JsonSerializer.Serialize(settings);
            if (plain.Contains("synthetic-router-key") || plain.Contains("synthetic-typesafe-key") || plain.Contains("synthetic-llm-key")) throw new InvalidDataException();
            var read = SettingsStore.Load(path);
            if (read != settings) throw new InvalidDataException();
        });
        Check("Recall notice imports with visible skip count", () =>
        {
            const string source = "08:24 Alex Chen 陳小明 準備出門\n08:25 王小華已收回訊息  \n08:26 王小華 北門見";
            using var form = new LineImportDialog(source, "Alex Chen 陳小明");
            Prepare(form);
            // One ordinary line cannot establish a multi-word display name; explicitly confirm it,
            // exactly as the dialog permits the user to do when a suggestion is incomplete.
            ((CheckBox)form.Controls.Find("ManualNames", true).Single()).Checked = true;
            ((ComboBox)form.Controls.Find("SelfName", true).Single()).Text = "Alex Chen 陳小明";
            ((ComboBox)form.Controls.Find("OtherName", true).Single()).Text = "王小華";
            if (form.Result?.SkippedRecallCount != 1 || form.Result.MessageCount != 2
                || form.Result.Transcript.Contains("已收回訊息")) throw new InvalidDataException();
            static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
                .SelectMany(child => new[] { child }.Concat(Descendants(child)));
            if (!Descendants(form).OfType<Label>().Any(label => label.Text.Contains("1 筆") && label.Text.Contains("已收回訊息")))
                throw new InvalidDataException();
            using var preview = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
            preview.Save(Path.Combine(destination, "recall-import.png"));
        });
        Check("LINE window enumeration", () =>
        {
            var count = LineCapture.ListWindows().Count;
            report.Add($"INFO LINE windows: {count} (no real chat captured)");
        });
        Check("Reject invalid capture target", () =>
        {
            try
            {
                LineCapture.CaptureAsync(new LineWindow(0, 0, "synthetic")).GetAwaiter().GetResult();
                throw new Exception("Accepted invalid target");
            }
            catch (InvalidOperationException) { }
        });
        Check("Traditional Chinese Windows OCR", () =>
        {
            if (WindowsOcr.NormalizeSpacing("今 天 很 累 ，明 天 12 30 再 說\r\nHello  world 42  7 。\n我 想") != "今天很累，明天 12 30 再說\r\nHello  world 42  7。\n我想") throw new InvalidDataException();
            using var bitmap = new Bitmap(1000, 220);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Microsoft JhengHei", 30, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("今天真的很累，明天再說好嗎", font, Brushes.Black, new PointF(25, 40));
                graphics.DrawString("我想先休息一下", font, Brushes.Black, new PointF(25, 110));
            }
            var text = Task.Run(() => WindowsOcr.RecognizeAsync(bitmap)).GetAwaiter().GetResult();
            report.Add("INFO OCR languages: " + string.Join(", ", WindowsOcr.AvailableLanguages));
            report.Add("INFO synthetic OCR text: " + text.ReplaceLineEndings(" / "));
            // OCR is approximate: compare edit accuracy instead of requiring error-free spelling.
            static string ChineseOnly(string value) => new(value.Where(c => c is >= '\u3400' and <= '\u9fff').ToArray());
            var expected = ChineseOnly("今天真的很累明天再說好嗎我想先休息一下");
            var actual = ChineseOnly(text);
            var distance = new int[expected.Length + 1, actual.Length + 1];
            for (var i = 0; i <= expected.Length; i++) distance[i, 0] = i;
            for (var j = 0; j <= actual.Length; j++) distance[0, j] = j;
            for (var i = 1; i <= expected.Length; i++)
            for (var j = 1; j <= actual.Length; j++)
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            var accuracy = 1 - (double)distance[expected.Length, actual.Length] / Math.Max(expected.Length, actual.Length);
            report.Add($"INFO synthetic OCR character similarity: {accuracy:P0}; threshold 75%; manual review required");
            if (accuracy < .75) throw new InvalidDataException();
        });
        Check("Bubble geometry identical at 96 and 144 DPI", () =>
        {
            using var first = SyntheticChat(96);
            using var second = SyntheticChat(144);
            var a = LineBubbleDetector.Detect(first);
            var b = LineBubbleDetector.Detect(second);
            if (a.Count != 3 || a.Count(item => item.Speaker == "other") != 2 || a.Count(item => item.Speaker == "me") != 1
                || !a.SequenceEqual(b)) throw new InvalidDataException();
        });
        Check("Spatial OCR and editable speaker review", () =>
        {
            using var source = SyntheticChat(144);
            var result = Task.Run(() => WindowsOcr.RecognizeChatAsync(source)).GetAwaiter().GetResult();
            var recognized = result.Messages.Where(message => message.Speaker != "unknown").ToArray();
            if (recognized.Length != 3 || recognized.Any(message => string.IsNullOrWhiteSpace(message.Text))
                || !recognized.Select(message => message.Speaker).SequenceEqual(new[] { "other", "me", "other" }))
                throw new InvalidDataException();
            using var form = new OcrReviewDialog(source, result);
            Prepare(form);
            using var preview = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
            preview.Save(Path.Combine(destination, "ocr-review.png"));
        });
        Check("Skipping blank rows identifies the actual remaining row", () =>
        {
            using var image = SyntheticChat(96);
            var result = new ChatRecognitionResult("", new ChatRecognizedMessage[]
            {
                new("unknown", "合成工具列", new(5, 5, 150, 20)),
                new("other", "", new(60, 40, 100, 66)),
                new("other", "北門見", new(60, 325, 230, 66))
            }, Array.Empty<string>(), 0);
            using var form = new OcrReviewDialog(image, result);
            Prepare(form);
            var rows = (DataGridView)form.Controls.Find("ReviewRows", true).Single();
            var skipColumn = rows.Columns["Skip"] ?? throw new InvalidDataException();
            var checkbox = rows.GetCellDisplayRectangle(skipColumn.Index, 1, true);
            var point = new nint(((checkbox.Top + checkbox.Height / 2) << 16) | (checkbox.Left + checkbox.Width / 2));
            PostMessage(rows.Handle, 0x0201, new nint(1), point);
            PostMessage(rows.Handle, 0x0202, 0, point);
            Application.DoEvents();
            if (rows.Rows[1].Cells["Skip"].Value is not true) throw new InvalidDataException("Checkbox was not committed");
            if (form.TryApplyTranscript() || rows.CurrentRow?.Index != 0) throw new InvalidDataException();
            var status = (Label)form.Controls.Find("ReviewStatus", true).Single();
            if (!status.Text.Contains("第 1 列") || status.Text.Contains("第 2 列：")) throw new InvalidDataException();
            ((Button)form.Controls.Find("SkipSelected", true).Single()).PerformClick();
            if (!form.TryApplyTranscript() || form.Transcript != "對方：北門見") throw new InvalidDataException();
        });
        Check("Blank text stays blank until explicitly corrected", () =>
        {
            using var image = SyntheticChat(96);
            var result = new ChatRecognitionResult("", new ChatRecognizedMessage[]
            { new("other", "", new(60, 40, 100, 66)), new("other", "", new(60, 325, 100, 66)) }, Array.Empty<string>(), 0);
            using var form = new OcrReviewDialog(image, result);
            Prepare(form);
            if (form.TryApplyTranscript() || form.TryApplyTranscript()) throw new InvalidDataException("Placeholder became message text");
            using var preview = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
            preview.Save(Path.Combine(destination, "empty-review.png"));
            ((Button)form.Controls.Find("FillHmm", true).Single()).PerformClick();
            if (form.TryApplyTranscript()) throw new InvalidDataException("Second blank row accepted");
            ((Button)form.Controls.Find("FillSigh", true).Single()).PerformClick();
            if (!form.TryApplyTranscript() || form.Transcript != "對方：嗯" + Environment.NewLine + "對方：唉") throw new InvalidDataException();
        });
        Check("Single-character OCR retry excludes generated context", () =>
        {
            foreach (var text in new[] { "嗯", "唉", "好", "" })
            {
                using var sample = new Bitmap(110, 90);
                using (var g = Graphics.FromImage(sample))
                using (var font = new Font("Microsoft JhengHei", 32, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    g.Clear(Color.FromArgb(239, 239, 239));
                    g.DrawString(text, font, Brushes.Black, 30, 24);
                }
                var read = Task.Run(() => WindowsOcr.RecognizeShortBubbleAsync(sample, new Rectangle(Point.Empty, sample.Size))).GetAwaiter().GetResult();
                if (read != text || read.Contains("文字")) throw new InvalidDataException("Short OCR mismatch");
            }
        });
        Check("Direct bitmap copies preserve pixels at odd widths and high DPI", () =>
        {
            using var bitmap = new Bitmap(17, 11, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            bitmap.SetResolution(144, 144);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, Color.FromArgb(x * 13, y * 21, (x + y) * 7));
            using var software = BitmapPixels.ToSoftwareBitmap(bitmap);
            using var returned = BitmapPixels.ToBitmap(software);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != returned.GetPixel(x, y).ToArgb()) throw new InvalidDataException();
        });
        Check("Pooled masks do not retain earlier images", () =>
        {
            using var source = SyntheticChat(96);
            var expected = LineBubbleDetector.Detect(source);
            using var blank = new Bitmap(1100, 630);
            using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
            if (LineBubbleDetector.Detect(blank).Count != 0 || !LineBubbleDetector.Detect(source).SequenceEqual(expected))
                throw new InvalidDataException();
        });
        Check("Draft-only retry reuses analysis and invalidates on context changes", () =>
        {
            var judgments = 0;
            var replies = 0;
            var failReply = false;
            TaskCompletionSource<HttpResponseMessage>? pendingReply = null;
            using var judgmentHttp = new HttpClient(new DiagnosticHandler(async request =>
            {
                judgments++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var answers = new Dictionary<string, object>();
                foreach (var question in body.RootElement.GetProperty("questions").EnumerateObject())
                {
                    var type = question.Value.GetProperty("type").GetString();
                    if (type == "choice")
                    {
                        var keys = question.Value.GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToArray();
                        answers[question.Name] = new { type, choice = keys[0], confidence = .8, probabilities = keys.ToDictionary(k => k, k => k == keys[0] ? 1d : 0d) };
                    }
                    else if (type == "score") answers[question.Name] = new { type, score = 1, confidence = .8,
                        probabilities = Enumerable.Range(0, 10).ToDictionary(i => i.ToString(), i => i == 1 ? 1d : 0d) };
                    else answers[question.Name] = new { type = "noul", noul = .5 };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { answers })) };
            }));
            using var replyHttp = new HttpClient(new DiagnosticHandler(_ =>
            {
                replies++;
                if (pendingReply is not null) return pendingReply.Task;
                return Task.FromResult(new HttpResponseMessage(failReply ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop",
                        message = new { content = "[\"收到，我確認一下\",\"好，北門見\",\"謝謝提醒\"]" } } } }))
                });
            }));
            var settings = new AppSettings { OpenRouterKey = "test-only-key", EnableReplySuggestions = true, LlmModel = "test/model" };
            using var form = new MainForm(settings, false, new JevClient(judgmentHttp), new ReplyClient(replyHttp));
            Prepare(form);
            var input = (TextBox)form.Controls.Find("TranscriptInput", true).Single();
            var intent = (RichTextBox)form.Controls.Find("IntentResult", true).Single();
            var retry = (Button)form.Controls.Find("RegenerateDraft", true).Single();
            input.Text = "對方：明天北門見";
            CompleteUiTask(form.AnalyzeAsync());
            if (judgments != 1 || replies != 1 || !retry.Enabled) throw new InvalidDataException();
            var originalIntent = intent.Text;
            CompleteUiTask(form.RegenerateDraftsAsync());
            if (judgments != 1 || replies != 2 || intent.Text != originalIntent) throw new InvalidDataException();
            failReply = true;
            CompleteUiTask(form.RegenerateDraftsAsync());
            if (judgments != 1 || replies != 3 || !retry.Enabled || intent.Text != originalIntent) throw new InvalidDataException();
            failReply = false;
            input.Text = "對方：改成南門見";
            CompleteUiTask(form.RegenerateDraftsAsync());
            if (retry.Enabled || judgments != 1 || replies != 3) throw new InvalidDataException();
            CompleteUiTask(form.AnalyzeAsync());
            form.ApplySettings(settings with { Relationship = "同事" });
            CompleteUiTask(form.RegenerateDraftsAsync());
            if (retry.Enabled || judgments != 2 || replies != 4) throw new InvalidDataException();
            form.ApplySettings(settings);
            CompleteUiTask(form.AnalyzeAsync());
            pendingReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = form.RegenerateDraftsAsync();
            input.Text = "對方：時間改成下午";
            pendingReply.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[\\\"舊草稿不得套用\\\"]\"}}]}") });
            CompleteUiTask(inFlight);
            var draft = (RichTextBox)form.Controls.Find("DraftResult", true).Single();
            if (retry.Enabled || judgments != 3 || replies != 6 || draft.Text.Contains("舊草稿不得套用")) throw new InvalidDataException();
        });
        report.Add($"RESULT {checkCount - failures}/{checkCount} smoke checks passed. No live Jev call or real LINE capture performed.");
        File.WriteAllLines(Path.Combine(destination, "results.txt"), report, new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    private static Bitmap SyntheticChat(float dpi)
    {
        var image = new Bitmap(1000, 580);
        image.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(image);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.Clear(Color.White);
        using var gray = new SolidBrush(Color.FromArgb(224, 226, 225));
        using var green = new SolidBrush(Color.FromArgb(169, 211, 121));
        using var tinyGray = new SolidBrush(Color.FromArgb(245, 245, 245));
        using var font = new Font("Microsoft JhengHei", 27, FontStyle.Regular, GraphicsUnit.Pixel);
        using var small = new Font("Microsoft JhengHei", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        static void Bubble(Graphics canvas, Brush brush, Rectangle box)
        {
            const int diameter = 40;
            using var path = new GraphicsPath();
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            canvas.FillPath(brush, path);
        }
        Bubble(graphics, gray, new Rectangle(60, 40, 320, 66));
        Bubble(graphics, green, new Rectangle(610, 165, 360, 104));
        Bubble(graphics, gray, new Rectangle(60, 325, 230, 66));
        graphics.FillEllipse(gray, 8, 48, 40, 40);
        Bubble(graphics, tinyGray, new Rectangle(460, 440, 80, 40));
        graphics.DrawString("今天去美術館", font, Brushes.Black, 80, 58);
        graphics.DrawString("我先查開放時間", font, Brushes.Black, 630, 180);
        graphics.DrawString("下午再出發", font, Brushes.Black, 630, 220);
        graphics.DrawString("好的，謝謝", font, Brushes.Black, 80, 344);
        graphics.DrawString("上午 9:30", small, Brushes.Gray, 395, 88);
        graphics.DrawString("今天", small, Brushes.Gray, 481, 451);
        return image;
    }

    private static void CompleteUiTask(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
        { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Offline UI test did not complete.");
        task.GetAwaiter().GetResult();
    }

    private sealed class DiagnosticHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
}
