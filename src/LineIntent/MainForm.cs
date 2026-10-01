using LineIntent.Capture;
using LineIntent.Core;
using LineIntent.Settings;

namespace LineIntent;

internal sealed class MainForm : Form
{
    private AppSettings _settings;
    private readonly ComboBox _windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox _transcript = new() { Name = "TranscriptInput", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Dock = DockStyle.Fill, MaxLength = 0 };
    private readonly RichTextBox _result = new() { Name = "IntentResult", ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _draftResult = new() { Name = "DraftResult", ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _destination = new() { AutoSize = true, MaximumSize = new Size(920, 0) };
    private readonly Button _capture = Button("擷取 LINE");
    private readonly Button _refresh = Button("重新整理");
    private readonly Button _configure = Button("API 與設定");
    private readonly Button _analyze = Button("分析對方意圖");
    private readonly Button _cancel = Button("取消");
    private readonly Button _regenerate = Button("只重新產生草稿");
    private readonly FlowLayoutPanel _editActions = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private readonly JevClient _jev;
    private readonly ReplyClient _replyClient;
    private AnalysisResult? _cachedAnalysis;
    private string? _cachedText;
    private AppSettings? _cachedSettings;
    private readonly FlowLayoutPanel _replyActions = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private IReadOnlyList<string> _drafts = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _hasResult;
    private string? _selfDisplayName;

    public MainForm(AppSettings settings, bool enumerateWindowsOnShown = true, JevClient? jevClient = null, ReplyClient? replyClient = null)
    {
        _settings = settings;
        _jev = jevClient ?? new();
        _replyClient = replyClient ?? new();
        _regenerate.Name = "RegenerateDraft";
        _regenerate.Enabled = false;
        Text = "LINE 意圖助手 · JEV";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft JhengHei UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1420, 900);
        MinimumSize = new Size(1040, 650);
        BackColor = Color.FromArgb(245, 248, 247);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "看懂對話，多一個判斷角度", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
        heading.Controls.Add(new Label { Text = "LINE 視窗 → 框選文字 → 本機辨識 → JEV 意圖分析", AutoSize = true, Location = new Point(2, 49), ForeColor = Color.FromArgb(80, 98, 92) });
        layout.Controls.Add(heading, 0, 0);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 12) };
        toolbar.Controls.AddRange([_windows, _refresh, _capture, _configure]);
        var pin = new CheckBox { Text = "視窗置頂", AutoSize = true, Margin = new Padding(10, 9, 0, 0) };
        pin.CheckedChanged += (_, _) => TopMost = pin.Checked;
        toolbar.Controls.Add(pin);
        layout.Controls.Add(toolbar, 0, 1);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 0, 16, 0) };
        input.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        input.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        input.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        input.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        input.Controls.Add(new Label { Text = "1   確認對話文字與說話者", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        input.Controls.Add(new Label { Text = "可貼上 LINE 對話並確認姓名。\n分析最近一則對方訊息。", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 12) }, 0, 1);
        _transcript.Font = new Font("Microsoft JhengHei UI", 11);
        _transcript.PlaceholderText = "我：今天有空聊一下嗎？\r\n對方：今天真的很累，明天再說好嗎";
        input.Controls.Add(_transcript, 0, 2);
        var paste = Button("貼上文字");
        paste.Click += (_, _) =>
        {
            TryClipboard(() => { if (Clipboard.ContainsText()) _transcript.SelectedText = Clipboard.GetText(); });
            if (LineClipboardImporter.LooksLikeLineClipboard(_transcript.Text)) ImportLineText();
        };
        var import = Button("解析 LINE 格式");
        import.Click += (_, _) => ImportLineText();
        var mark = Button("選取段落標為對方");
        mark.Click += (_, _) => MarkOther();
        var sample = Button("載入範例");
        sample.Click += (_, _) => _transcript.Text = "我：今天有空聊一下嗎？\r\n對方：今天真的很累，明天再說好嗎";
        var clear = Button("清除");
        clear.Click += (_, _) => { _transcript.Clear(); ClearResult(); _status.Text = "對話與分析結果已清除。"; };
        _editActions.Controls.AddRange([paste, import, mark, sample, clear]);
        input.Controls.Add(_editActions, 0, 3);
        columns.Controls.Add(input, 0, 0);
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 0, 14, 0) };
        output.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        output.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        output.Controls.Add(new Label { Text = "2   理解可能意圖", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        output.Controls.Add(new Label { Text = "這是根據文字的推測，並非讀心或事實認定。\n模型信心不代表實際正確率。", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 12), ForeColor = Color.FromArgb(90, 101, 98) }, 0, 1);
        output.Controls.Add(_result, 0, 2);
        columns.Controls.Add(output, 1, 0);
        var drafts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8, 0, 0, 0) };
        drafts.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        drafts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        drafts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        drafts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        drafts.Controls.Add(new Label { Text = "3   回覆草稿", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        drafts.Controls.Add(new Label { Text = "依意圖分析提供候選回覆。\n請核對後複製，訊息由你自行傳送。", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 12), ForeColor = Color.FromArgb(90, 101, 98) }, 0, 1);
        drafts.Controls.Add(_draftResult, 0, 2);
        drafts.Controls.Add(_replyActions, 0, 3);
        columns.Controls.Add(drafts, 2, 0);
        layout.Controls.Add(columns, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 12, 0, 6) };
        _analyze.BackColor = Color.FromArgb(5, 135, 76);
        _analyze.ForeColor = Color.White;
        _analyze.FlatStyle = FlatStyle.Flat;
        _analyze.Font = new Font(Font, FontStyle.Bold);
        _cancel.Enabled = false;
        actions.Controls.AddRange([_analyze, _regenerate, _cancel]);
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(_destination, 0, 4);
        layout.Controls.Add(_status, 0, 5);
        Controls.Add(layout);
        ClearResult();
        UpdateDestination();
        _refresh.Click += (_, _) => RefreshWindows();
        _capture.Click += async (_, _) => await CaptureAsync();
        _configure.Click += (_, _) => Configure();
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _regenerate.Click += async (_, _) => await RegenerateDraftsAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _transcript.TextChanged += (_, _) => { if (_hasResult) { ClearResult(); _status.Text = "文字已變更，請重新分析。"; } };
        if (enumerateWindowsOnShown) Shown += (_, _) => RefreshWindows();
        FormClosing += (_, _) => _operation?.Cancel();
        FormClosed += (_, _) => { _jev.Dispose(); _replyClient.Dispose(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(1040, area.Width), Math.Min(650, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    private static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 34), Margin = new Padding(0, 0, 8, 6), Padding = new Padding(6, 2, 6, 2) };
    private void RefreshWindows()
    {
        var selected = (_windows.SelectedItem as LineWindow)?.Handle;
        _windows.Items.Clear();
        try
        {
            foreach (var window in LineCapture.ListWindows()) _windows.Items.Add(window);
            if (_windows.Items.Count > 0)
            {
                _windows.SelectedIndex = 0;
                foreach (LineWindow window in _windows.Items) if (window.Handle == selected) _windows.SelectedItem = window;
                _status.Text = $"找到 {_windows.Items.Count} 個 LINE 視窗。開啟想分析的聊天後，按「擷取 LINE」。";
            }
            else _status.Text = "尚未找到 LINE 視窗。請開啟 LINE 再重新整理，也可直接貼上對話。";
        }
        catch { _status.Text = "無法列出 LINE 視窗，請重新整理或直接貼上對話。"; }
        _capture.Enabled = !_busy && _windows.Items.Count > 0;
    }
    private void Configure()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SettingsStore.Save(dialog.Result);
            ApplySettings(dialog.Result);
            _status.Text = "設定已儲存，API Key 已使用 Windows 使用者加密保護。";
        }
        catch { MessageBox.Show(this, "設定無法儲存。請確認本機使用者設定資料夾可寫入，再重試。", "設定未儲存"); }
    }
    internal void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        ClearResult();
        UpdateDestination();
    }
    private void UpdateDestination()
    {
        var host = _settings.Provider == JevProvider.OpenRouter ? "openrouter.ai" : "api.typesafe.ai";
        var draftInfo = _settings.EnableReplySuggestions ? "；LLM 草稿另傳送至 openrouter.ai" : "";
        _destination.Text = $"分析傳送文字至 {host}{draftInfo}；只重生草稿僅傳送至 OpenRouter。圖片不外傳。";
    }
    private void MarkOther()
    {
        if (_transcript.SelectionLength == 0)
        {
            _status.Text = "先在文字框選取對方的完整段落，再按「選取段落標為對方」。";
            _transcript.Focus();
            return;
        }
        var start = _transcript.SelectionStart;
        if (start > 0 && _transcript.Text[start - 1] != '\n')
        {
            _status.Text = "請從段落開頭選取，避免把說話者標籤插入句子中。";
            return;
        }
        var selection = _transcript.SelectedText;
        var trailingBreak = selection.EndsWith('\n') ? "\r\n" : "";
        _transcript.SelectedText = "對方：" + string.Join(" ", selection.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0)) + trailingBreak;
        _status.Text = "已將選取內容合併為一則對方訊息。請確認沒有包含自己的發言，再按分析。";
    }
    private bool ImportLineText()
    {
        if (_transcript.TextLength > Transcript.MaximumCharacters)
        {
            _status.Text = $"對話超過 {Transcript.MaximumCharacters:N0} 字元，請縮小範圍；原文尚未變更。";
            return false;
        }
        if (!LineClipboardImporter.LooksLikeLineClipboard(_transcript.Text))
        {
            _status.Text = "未偵測到 LINE 的「時間 姓名 訊息」格式；一般文字可使用「我：」「對方：」標示。";
            return false;
        }
        try
        {
            using var dialog = new LineImportDialog(_transcript.Text, _selfDisplayName);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return false;
            _selfDisplayName = dialog.SelfName;
            _transcript.Text = dialog.Result.Transcript;
            ClearResult();
            _status.Text = $"LINE 格式已解析，共 {dialog.Result.MessageCount} 則訊息。"
                + (dialog.Result.SkippedRecallCount > 0 ? $"已略過 {dialog.Result.SkippedRecallCount} 筆收回通知。" : "")
                + "分析目標為最近一則對方訊息。";
            return true;
        }
        catch (JevException ex) { _status.Text = ex.Message; return false; }
    }
    private async Task CaptureAsync()
    {
        if (_windows.SelectedItem is not LineWindow window) return;
        BeginOperation("正在擷取選取的 LINE 視窗…");
        var token = _operation!.Token;
        var stage = "擷取 LINE 視窗";
        try
        {
            Bitmap shot;
            Hide();
            try { shot = await LineCapture.CaptureAsync(window, token); }
            finally { if (!IsDisposed && !Disposing) { Show(); Activate(); } }
            using (shot)
            using (var dialog = new CropDialog(shot))
            {
                token.ThrowIfCancellationRequested();
                if (dialog.ShowDialog(this) != DialogResult.OK) { _status.Text = "已取消框選，對話未變更。"; return; }
                using var crop = dialog.SelectedBitmap!;
                stage = "本機 OCR 辨識";
                _status.Text = "正在本機辨識文字，圖片不會上傳…";
                var recognized = await WindowsOcr.RecognizeChatAsync(crop, _settings.OcrLanguage, token);
                token.ThrowIfCancellationRequested();
                if (recognized.Messages.Count == 0) { _status.Text = "沒有辨識到聊天文字。請框選包含左右氣泡的訊息區，或直接貼上。"; return; }
                using var review = new OcrReviewDialog(crop, recognized);
                if (review.ShowDialog(this) != DialogResult.OK) { _status.Text = "已取消 OCR 確認，原對話未變更。"; return; }
                _transcript.Text = review.Transcript;
                ClearResult();
                _status.Text = "OCR 對話已套用，已依氣泡位置標示我／對方。確認文字後可直接分析。";
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "擷取或辨識已取消。"; }
        catch (InvalidOperationException ex) { ShowCaptureError(stage + "失敗：" + ex.Message); }
        catch (Exception ex) { ShowCaptureError($"{stage}失敗（代碼 0x{ex.HResult:X8}）。請重試，或先貼上 LINE 對話文字。"); }
        finally { EndOperation(); }
    }
    private void ShowCaptureError(string message)
    {
        if (IsDisposed || Disposing) return;
        _status.Text = message;
        MessageBox.Show(this, message, "LINE 擷取未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private bool OutboundTextIsSafe()
    {
        var keys = new[] { _settings.OpenRouterKey, _settings.TypeSafeKey, _settings.LlmApiKey }.Where(key => !string.IsNullOrWhiteSpace(key));
        var fields = new[] { _transcript.Text, _settings.Relationship, _settings.Model, _settings.LlmModel };
        if (!keys.Any(key => fields.Any(field => field.Contains(key, StringComparison.Ordinal)))) return true;
        _status.Text = "對話或其他設定欄位含有已儲存的 API Key，已停止分析。請移除密鑰再試。";
        return false;
    }

    private bool CanRegenerate => _cachedAnalysis is not null && _cachedText == _transcript.Text && _cachedSettings == _settings
        && _settings.EnableReplySuggestions && !string.IsNullOrWhiteSpace(_settings.EffectiveLlmApiKey) && !string.IsNullOrWhiteSpace(_settings.LlmModel);

    internal async Task RegenerateDraftsAsync()
    {
        if (_busy) return;
        if (!CanRegenerate) { _status.Text = "請先完成目前對話的意圖分析，並啟用 LLM 草稿。"; return; }
        if (!OutboundTextIsSafe()) return;
        var analysis = _cachedAnalysis!;
        var input = _cachedText!;
        var context = _settings;
        BeginOperation("僅重新產生草稿，沿用本次意圖分析…");
        ClearDrafts("正在重新產生草稿…\n\n意圖分析保持不變。");
        try
        {
            var drafts = await _replyClient.GenerateAsync(input, context.Relationship, analysis,
                new ReplySettings { ApiKey = context.EffectiveLlmApiKey, Model = context.LlmModel }, _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (input != _transcript.Text || context != _settings || _cachedAnalysis is null)
            { _status.Text = "輸入已變更，舊草稿結果未套用。"; return; }
            ShowDrafts(drafts);
            _status.Text = "草稿已更新；本次沒有重新呼叫 JEV。";
        }
        catch (OperationCanceledException) { if (!IsDisposed) { _draftResult.Text = "草稿重生已取消，可再重試。"; _status.Text = "意圖分析保留，已取消草稿重生。"; } }
        catch (JevException ex) { if (!IsDisposed) { _draftResult.Text = ex.Message; _status.Text = "意圖分析保留，草稿可重試。"; } }
        catch { if (!IsDisposed) { _draftResult.Text = "草稿產生失敗，請重試。"; _status.Text = "意圖分析保留。"; } }
        finally { EndOperation(); }
    }

    internal async Task AnalyzeAsync()
    {
        if (_busy) return;
        if (LineClipboardImporter.LooksLikeLineClipboard(_transcript.Text) && !ImportLineText()) return;
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            Configure();
            if (string.IsNullOrWhiteSpace(_settings.ApiKey)) { _status.Text = "請先在「API 與設定」填入所選服務的 API Key。"; return; }
        }
        if (!OutboundTextIsSafe()) return;
        var input = _transcript.Text;
        var context = _settings;
        BeginOperation("正在請 JEV 分析，通常需要數秒…");
        ClearResult();
        _draftResult.Text = _settings.EnableReplySuggestions ? "等待意圖分析完成後產生回覆草稿…" : "尚未啟用回覆草稿。\n\n可至「API 與設定」啟用 LLM 並填入模型。";
        try
        {
            var settings = new JevSettings { Provider = _settings.Provider, ApiKey = _settings.ApiKey, Model = _settings.Model };
            var result = await _jev.AnalyzeAsync(input, context.Relationship, settings, _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (input != _transcript.Text || context != _settings) { _status.Text = "輸入已變更，請重新分析。"; return; }
            _cachedAnalysis = result;
            _cachedText = input;
            _cachedSettings = context;
            Render(result);
            if (_settings.EnableReplySuggestions)
            {
                _status.Text = "JEV 判斷完成，正在透過 OpenRouter 產生 LLM 回覆草稿…";
                _draftResult.Text = "正在產生回覆草稿…\n\n左側意圖分析已可閱讀。";
                try
                {
                    var drafts = await _replyClient.GenerateAsync(_transcript.Text, _settings.Relationship, result,
                        new ReplySettings { ApiKey = _settings.EffectiveLlmApiKey, Model = _settings.LlmModel }, _operation.Token);
                    _operation.Token.ThrowIfCancellationRequested();
                    if (input != _transcript.Text || context != _settings)
                    { _status.Text = "輸入已變更，舊草稿結果未套用。"; return; }
                    ShowDrafts(drafts);
                    _status.Text = "JEV 與 LLM 完成。草稿可複製；請確認內容後自行回覆。";
                }
                catch (JevException ex)
                {
                    _draftResult.Text = "草稿未產生：" + ex.Message;
                    _status.Text = "JEV 判斷已完成；" + ex.Message;
                }
            }
            else _status.Text = "分析完成。可根據建議動作自行回覆 LINE。";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _draftResult.Text = "已取消，未產生回覆草稿。";
                _status.Text = "分析已取消。已送出的請求仍可能由服務商計費。";
            }
        }
        catch (JevException ex)
        {
            if (!IsDisposed) { _draftResult.Text = "未產生草稿，請先完成意圖分析。"; _status.Text = ex.Message; }
        }
        catch
        {
            if (!IsDisposed) { _draftResult.Text = "未產生回覆草稿，請重試。"; _status.Text = "分析失敗。請檢查網路、服務商與模型設定後重試。"; }
        }
        finally { EndOperation(); }
    }
    internal void Render(AnalysisResult result)
    {
        _result.Clear();
        Append("可能意圖\n", true);
        Append(result.Intent.Label + "\n", false);
        Append($"模型信心 {result.Intent.Confidence:P0}\n\n", false);
        Append("對方可能需要\n", true);
        Append(result.Need.Label + "\n\n", false);
        Append("建議下一步\n", true);
        Append(result.Action.Label + "\n\n", false);
        Append($"互動緊張度   {result.Tension.Value:0.0} / 9\n\n", true);
        Append($"偏向字面意思：{result.LiteralProbability:P0}\n有足夠資訊可具體回應：{result.ReplySubstanceProbability:P0}\n沒有未解決的緊張情緒：{result.ResolvedProbability:P0}\n", false);
        if (result.Warnings.Count > 0) Append("\n" + string.Join("\n", result.Warnings), false);
        _result.SelectionStart = 0;
        _result.ScrollToCaret();
        _hasResult = true;
    }
    private void Append(string text, bool bold)
        => AppendTo(_result, text, bold);

    private void AppendTo(RichTextBox target, string text, bool bold)
    {
        target.SelectionStart = target.TextLength;
        using var font = new Font(Font.FontFamily, bold ? 12 : 11, bold ? FontStyle.Bold : FontStyle.Regular);
        target.SelectionFont = font;
        target.SelectionColor = bold ? Color.FromArgb(3, 111, 65) : Color.FromArgb(36, 48, 42);
        target.AppendText(text);
    }
    internal void ClearResult()
    {
        _hasResult = false;
        _cachedAnalysis = null;
        _cachedText = null;
        _cachedSettings = null;
        _regenerate.Enabled = false;
        _result.Text = "完成左側文字確認後，按「分析對方意圖」。\n\n會顯示可能意圖、需求、建議動作與互動緊張度。\n\n未分析時不會呼叫 API。";
        ClearDrafts(_settings.EnableReplySuggestions
            ? "完成意圖分析後，回覆草稿會顯示在這裡。\n\n每則草稿可個別複製。"
            : "尚未啟用回覆草稿。\n\n可至「API 與設定」啟用 LLM 並填入模型。");
    }
    private void ClearDrafts(string message)
    {
        _drafts = [];
        foreach (Control control in _replyActions.Controls.Cast<Control>().ToArray()) control.Dispose();
        _draftResult.Text = message;
    }
    internal void ShowDrafts(IReadOnlyList<string> drafts)
    {
        _drafts = drafts;
        _draftResult.Clear();
        foreach (Control control in _replyActions.Controls.Cast<Control>().ToArray()) control.Dispose();
        for (var i = 0; i < drafts.Count; i++)
        {
            AppendTo(_draftResult, $"草稿 {i + 1}\n", true);
            AppendTo(_draftResult, drafts[i] + "\n\n", false);
            var index = i;
            var copy = Button($"複製草稿 {i + 1}");
            copy.Click += (_, _) => TryClipboard(() =>
            {
                Clipboard.SetText(_drafts[index]);
                _status.Text = "草稿已複製。請在 LINE 貼上後確認內容再自行傳送。";
            });
            _replyActions.Controls.Add(copy);
        }
        _draftResult.SelectionStart = 0;
        _draftResult.ScrollToCaret();
    }
    private void BeginOperation(string status)
    {
        _operation = new CancellationTokenSource();
        _busy = true;
        _configure.Enabled = _refresh.Enabled = _capture.Enabled = _analyze.Enabled = _windows.Enabled = _editActions.Enabled = _replyActions.Enabled = false;
        _transcript.ReadOnly = true;
        _cancel.Enabled = true;
        _regenerate.Enabled = false;
        _status.Text = status;
    }
    private void EndOperation()
    {
        _operation?.Dispose();
        _operation = null;
        _busy = false;
        if (IsDisposed || Disposing) return;
        _configure.Enabled = _refresh.Enabled = _analyze.Enabled = _windows.Enabled = _editActions.Enabled = _replyActions.Enabled = true;
        _capture.Enabled = _windows.Items.Count > 0;
        _transcript.ReadOnly = false;
        _cancel.Enabled = false;
        _regenerate.Enabled = CanRegenerate;
    }
    private void TryClipboard(Action action)
    {
        try { action(); }
        catch { _status.Text = "剪貼簿目前忙碌，請稍後再試或使用 Ctrl+V。"; }
    }
}
