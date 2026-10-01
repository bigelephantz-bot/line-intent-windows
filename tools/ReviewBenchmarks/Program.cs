using System.Diagnostics;
using System.Text.Json;
using LineIntent.Capture;
using LineIntent.Core;

// Reproducible synthetic-only review probes. No settings, clipboard, real images or API calls.
var recallSample = "08:00 Alex Chen 陳小明 北門見\n08:01 王小華 好\n08:02 Alex Chen 陳小明已收回訊息\n08:03 Alex Chen 陳小明 下午見";
var dateSample = "08:00 王小華 下次活動日期\n2026/10/01\n08:01 Alex Chen 陳小明 好";
var importedDate = LineClipboardImporter.Convert(dateSample, "Alex Chen 陳小明", "王小華");
var suggestedImport = LineClipboardImporter.Convert(recallSample,
    LineClipboardImporter.SuggestNames(recallSample)[0], "王小華");
var findings = new
{
    recallNameCandidates = LineClipboardImporter.SuggestNames(recallSample),
    fullSelfNameSuggested = LineClipboardImporter.SuggestNames(recallSample).Contains("Alex Chen 陳小明"),
    skippedRecallWhenUsingSuggestedName = suggestedImport.SkippedRecallCount,
    recallRemainsWhenUsingSuggestedName = suggestedImport.Transcript.Contains("已收回訊息"),
    dateInBodyPreserved = importedDate.Transcript.Contains("2026/10/01"),
    dateWarningPresent = importedDate.Warnings.Any(w => w.Contains("日期"))
};
var samples = new List<object>();
foreach (var count in new[] { 30, 500 })
{
    var text = string.Join('\n', Enumerable.Range(0, count).Select(i =>
        $"08:{i % 60:00} {(i % 2 == 0 ? "Alex Chen 陳小明" : "王小華")} 訊息內容第{i}則"));
    samples.Add(Measure($"import-{count}-messages-{text.Length}-chars", () =>
        LineClipboardImporter.Convert(text, "Alex Chen 陳小明", "王小華")));
}
foreach (var width in new[] { 1920, 3840 })
{
    using var image = MakeImage(width, width * 9 / 16);
    samples.Add(Measure($"bubble-detection-{image.Width}x{image.Height}", () => LineBubbleDetector.Detect(image)));
}
Console.WriteLine(JsonSerializer.Serialize(new { environment = new { runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount }, findings, samples }, new JsonSerializerOptions { WriteIndented = true }));

static object Measure(string label, Action action)
{
    action();
    var elapsed = new List<double>();
    var allocations = new List<long>();
    for (var i = 0; i < 5; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        elapsed.Add(watch.Elapsed.TotalMilliseconds);
        allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
    }
    return new { label, repetitions = 5, medianMilliseconds = Math.Round(elapsed.Order().ElementAt(2), 2),
        maximumMilliseconds = Math.Round(elapsed.Max(), 2), medianManagedAllocatedMiB = Math.Round(allocations.Order().ElementAt(2) / 1048576d, 2) };
}

static Bitmap MakeImage(int width, int height)
{
    var bitmap = new Bitmap(width, height);
    var scale = width / 1920f;
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.White);
    using var gray = new SolidBrush(Color.FromArgb(225, 225, 225));
    using var green = new SolidBrush(Color.FromArgb(170, 215, 130));
    using var font = new Font("Microsoft JhengHei", 24 * scale, GraphicsUnit.Pixel);
    for (var i = 0; i < 10; i++)
    {
        var x = i % 2 == 0 ? 90 * scale : width - 650 * scale;
        var y = (45 + i * 95) * scale;
        graphics.FillRectangle(i % 2 == 0 ? gray : green, x, y, 550 * scale, 64 * scale);
        graphics.DrawString("合成測試訊息，明天北門集合", font, Brushes.Black, x + 16 * scale, y + 12 * scale);
    }
    return bitmap;
}
