using System.Globalization;
using LineIntent.Capture;
using LineIntent.Core;

namespace LineIntent.Settings;

public sealed class SettingsDialog : Form
{
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _openRouterKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly TextBox _typeSafeKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _enableReplySuggestions = new() { Text = "啟用 LLM 回覆草稿（選用）", AutoSize = true };
    private readonly TextBox _llmModel = new() { Dock = DockStyle.Fill, PlaceholderText = "OpenRouter 模型 ID；啟用時必填" };
    private readonly TextBox _llmApiKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "留空沿用上方 OpenRouter API Key" };
    private readonly TextBox _relationship = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly ComboBox _ocrLanguage = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _modelHint = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };

    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Result = settings;
        Text = "LINE 意圖助手 · 設定";
        Font = new Font("Microsoft JhengHei UI", 10f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 640);
        MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 18, 22, 8) };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(fields);
        Controls.Add(body);

        AddWideRow(fields, new Label
        {
            Text = "連接你的 JEV 服務",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 9)
        });
        AddWideRow(fields, Note("JEV 用於意圖判斷；每次分析只使用所選服務。回覆草稿可另行啟用 LLM。"));

        _provider.Items.AddRange([new ProviderChoice(JevProvider.OpenRouter, "OpenRouter"),
            new ProviderChoice(JevProvider.TypeSafe, "TypeSafe / JEV")]);
        _provider.SelectedIndex = settings.Provider == JevProvider.TypeSafe ? 1 : 0;
        _openRouterKey.Text = settings.OpenRouterKey;
        _typeSafeKey.Text = settings.TypeSafeKey;
        _model.Text = settings.Model;
        _enableReplySuggestions.Checked = settings.EnableReplySuggestions;
        _llmModel.Text = settings.LlmModel;
        _llmApiKey.Text = settings.LlmApiKey;
        _relationship.Text = settings.Relationship;

        AddRow(fields, "JEV 分析服務", _provider);
        AddRow(fields, "OpenRouter API Key", _openRouterKey);
        AddRow(fields, "TypeSafe API Key", _typeSafeKey);
        var showKeys = new CheckBox { Text = "顯示 API Key", AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        showKeys.CheckedChanged += (_, _) =>
        {
            _openRouterKey.UseSystemPasswordChar = !showKeys.Checked;
            _typeSafeKey.UseSystemPasswordChar = !showKeys.Checked;
            _llmApiKey.UseSystemPasswordChar = !showKeys.Checked;
        };
        AddRow(fields, "", showKeys);
        AddRow(fields, "JEV 模型", _model);
        AddRow(fields, "", _modelHint);

        AddWideRow(fields, new Label
        {
            Text = "LLM 回覆草稿 · OpenRouter",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 8)
        });
        AddWideRow(fields, _enableReplySuggestions);
        AddRow(fields, "LLM 模型", _llmModel);
        AddRow(fields, "LLM API Key", _llmApiKey);
        AddRow(fields, "", Note("LLM Key 留空會沿用上方 OpenRouter Key。請自行填入可使用的 OpenRouter 模型 ID；未啟用時不呼叫 LLM。"));
        _enableReplySuggestions.CheckedChanged += (_, _) => UpdateLlmFields();
        UpdateLlmFields();

        AddRow(fields, "與對方的關係", _relationship);

        var languages = new List<LanguageChoice> { new("", "自動選擇（優先中文）") };
        var languageHint = "辨識在這台電腦完成。若缺少中文，請在 Windows 語言設定安裝 OCR 語言套件。";
        try
        {
            languages.AddRange(WindowsOcr.AvailableLanguages.Select(tag => new LanguageChoice(tag, LanguageName(tag))));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            languageHint = "目前無法讀取 Windows OCR 語言。仍可貼上對話文字並分析。";
        }
        if (!string.IsNullOrEmpty(settings.OcrLanguage) && languages.All(item => item.Tag != settings.OcrLanguage))
            languages.Add(new LanguageChoice(settings.OcrLanguage, $"{settings.OcrLanguage}（目前未安裝）"));
        _ocrLanguage.Items.AddRange(languages.Cast<object>().ToArray());
        _ocrLanguage.SelectedItem = languages.First(item => item.Tag == settings.OcrLanguage);
        AddRow(fields, "OCR 辨識語言", _ocrLanguage);
        AddRow(fields, "", Note(languageHint));

        AddWideRow(fields, new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "隱私與儲存",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 18, 0, 6)
        });
        AddWideRow(fields, Note("API Key 使用 Windows DPAPI 加密，僅目前 Windows 使用者可解密。" +
            "密鑰只在呼叫所選 API 時用於驗證，不會放入聊天內容、日誌或原始碼。"));
        AddWideRow(fields, Note("JEV 分析會將對話與關係背景傳送至所選服務。啟用 LLM 後，產生草稿會另外將對話、關係背景與 JEV 結果傳送至 OpenRouter（openrouter.ai），即使 JEV 選用 TypeSafe 也一樣。"));
        AddWideRow(fields, Note("設定檔位置：\r\n" + SettingsStore.SettingsPath));

        var save = new Button { Text = "儲存設定", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 8, 18, 12)
        };
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) => SaveResult();
        _provider.SelectedIndexChanged += (_, _) =>
        {
            _model.Clear();
            UpdateModelHint();
        };
        UpdateModelHint();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        var availableWidth = Math.Max(1, area.Width - 24);
        var availableHeight = Math.Max(1, area.Height - 24);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, availableWidth), Math.Min(MinimumSize.Height, availableHeight));
        Size = new Size(Math.Min(Width, availableWidth), Math.Min(Height, availableHeight));
    }

    private void SaveResult()
    {
        var result = new AppSettings
        {
            Provider = ((ProviderChoice)_provider.SelectedItem!).Value,
            OpenRouterKey = _openRouterKey.Text.Trim(),
            TypeSafeKey = _typeSafeKey.Text.Trim(),
            Model = _model.Text.Trim(),
            EnableReplySuggestions = _enableReplySuggestions.Checked,
            LlmModel = _llmModel.Text.Trim(),
            LlmApiKey = _llmApiKey.Text.Trim(),
            Relationship = string.IsNullOrWhiteSpace(_relationship.Text) ? "一般朋友" : _relationship.Text.Trim(),
            OcrLanguage = ((LanguageChoice)_ocrLanguage.SelectedItem!).Tag
        };
        if (result.EnableReplySuggestions && string.IsNullOrWhiteSpace(result.LlmModel))
        {
            MessageBox.Show(this, "啟用回覆草稿時，請填入可使用的 OpenRouter LLM 模型 ID。", "請填入 LLM 模型", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _llmModel.Focus();
            return;
        }
        if (result.EnableReplySuggestions && string.IsNullOrWhiteSpace(result.EffectiveLlmApiKey))
        {
            MessageBox.Show(this, "請填入 LLM API Key，或在上方填入可沿用的 OpenRouter API Key。TypeSafe Key 無法用於 OpenRouter 草稿。", "請填入 OpenRouter Key", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _llmApiKey.Focus();
            return;
        }
        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateModelHint()
    {
        var openRouter = ((ProviderChoice)_provider.SelectedItem!).Value == JevProvider.OpenRouter;
        _model.PlaceholderText = openRouter ? "留空使用 typesafe/jev-1.13" : "留空使用 jev-latest";
        _modelHint.Text = "切換服務會清除自訂模型名稱，改用該服務的預設模型。";
    }

    private void UpdateLlmFields()
    {
        _llmModel.Enabled = _enableReplySuggestions.Checked;
        _llmApiKey.Enabled = _enableReplySuggestions.Checked;
    }

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 2, 0, 10)
    };

    private static void AddRow(TableLayoutPanel table, string label, Control field)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 3, 8, 7)
        }, 0, row);
        if (field.Margin == new Padding(3))
            field.Margin = new Padding(0, 3, 0, 7);
        table.Controls.Add(field, 1, row);
    }

    private static void AddWideRow(TableLayoutPanel table, Control field)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(field, 0, row);
        table.SetColumnSpan(field, 2);
    }

    private static string LanguageName(string tag)
    {
        try { return $"{CultureInfo.GetCultureInfo(tag).NativeName}（{tag}）"; }
        catch (CultureNotFoundException) { return tag; }
    }

    private sealed record ProviderChoice(JevProvider Value, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record LanguageChoice(string Tag, string Name)
    {
        public override string ToString() => Name;
    }
}
