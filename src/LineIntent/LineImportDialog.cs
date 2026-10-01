using LineIntent.Core;

namespace LineIntent;

/// <summary>Explicit sender mapping for clipboard text; all input and preview remain in memory.</summary>
internal sealed class LineImportDialog : Form
{
    private readonly string _source;
    private readonly ComboBox _self = new() { Name = "SelfName", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _other = new() { Name = "OtherName", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _manualNames = new() { Name = "ManualNames", Text = "手動輸入姓名（候選不完整時使用）", AutoSize = true, Dock = DockStyle.Fill };
    private bool _changingNameMode;
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button _apply = new() { Text = "確認並套用", AutoSize = true, Enabled = false, Padding = new Padding(8) };
    public LineImportResult? Result { get; private set; }
    public string SelfName => _self.Text.Trim();

    public LineImportDialog(string source, string? preferredSelfName = null)
    {
        _source = source;
        Text = "解析 LINE 複製對話 · 確認發話者";
        Font = new Font("Microsoft JhengHei UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 720);
        MinimumSize = new Size(560, 480);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation = new Label
        {
            Text = "點姓名欄任意位置即可展開選單，請確認自己的完整 LINE 顯示名稱。\n候選不完整時可勾選「手動輸入姓名」；原文只在記憶體中處理。",
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 14)
        };
        layout.Controls.Add(explanation, 0, 0);
        layout.SetColumnSpan(explanation, 2);
        var selfLabel = new Label { Name = "SelfNameLabel", Text = "我的姓名", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Cursor = Cursors.Hand };
        selfLabel.Click += (_, _) => { _self.Focus(); _self.DroppedDown = true; };
        layout.Controls.Add(selfLabel, 0, 1);
        layout.Controls.Add(_self, 1, 1);
        var otherLabel = new Label { Name = "OtherNameLabel", Text = "對方姓名", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Cursor = Cursors.Hand };
        otherLabel.Click += (_, _) => { _other.Focus(); _other.DroppedDown = true; };
        layout.Controls.Add(otherLabel, 0, 2);
        layout.Controls.Add(_other, 1, 2);
        _self.Margin = _other.Margin = new Padding(3, 3, 3, 10);
        layout.Controls.Add(_manualNames, 1, 3);
        _manualNames.Margin = new Padding(3, 0, 3, 10);
        _preview.Font = new Font(Font.FontFamily, 11);
        layout.Controls.Add(_preview, 0, 4);
        layout.SetColumnSpan(_preview, 2);
        _status.Padding = new Padding(0, 10, 0, 8);
        layout.Controls.Add(_status, 0, 5);
        layout.SetColumnSpan(_status, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8) };
        footer.Controls.Add(_apply);
        footer.Controls.Add(cancel);
        layout.Controls.Add(footer, 0, 6);
        layout.SetColumnSpan(footer, 2);
        Controls.Add(layout);
        CancelButton = cancel;
        AcceptButton = _apply;
        var names = LineClipboardImporter.SuggestNames(source);
        _self.Items.AddRange(names.Cast<object>().ToArray());
        _other.Items.AddRange(names.Cast<object>().ToArray());
        _self.Text = !string.IsNullOrEmpty(preferredSelfName) && names.Contains(preferredSelfName) ? preferredSelfName : "";
        _other.Text = _self.Text.Length > 0 && names.Count == 2 ? names.FirstOrDefault(name => name != _self.Text) ?? "" : "";
        _self.TextChanged += (_, _) =>
        {
            if (_changingNameMode) return;
            if (names.Count == 2 && names.Contains(_self.Text))
                _other.Text = names.First(name => name != _self.Text);
            UpdatePreview();
        };
        _other.TextChanged += (_, _) => { if (!_changingNameMode) UpdatePreview(); };
        _manualNames.CheckedChanged += (_, _) => ChangeNameMode();
        _apply.Click += (_, _) => { if (Result is not null) { DialogResult = DialogResult.OK; Close(); } };
        UpdatePreview();
    }

    private void ChangeNameMode()
    {
        _changingNameMode = true;
        try
        {
            foreach (var field in new[] { _self, _other })
            {
                var value = field.Text.Trim();
                if (!_manualNames.Checked && value.Length > 0 && !field.Items.Contains(value))
                    field.Items.Add(value);
                field.DropDownStyle = _manualNames.Checked ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
                if (_manualNames.Checked) field.Text = value;
                else field.SelectedIndex = value.Length == 0 ? -1 : field.Items.IndexOf(value);
            }
        }
        finally { _changingNameMode = false; }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _apply.Enabled = false;
        Result = null;
        try
        {
            var result = LineClipboardImporter.Convert(_source, _self.Text.Trim(), _other.Text.Trim());
            _preview.Text = result.Transcript;
            _status.Text = $"已解析 {result.MessageCount} 則訊息。請核對發話者與內容後套用。";
            if (result.Warnings.Count > 0) _status.Text += "\n" + string.Join("\n", result.Warnings);
            Result = result;
            _apply.Enabled = true;
        }
        catch (JevException ex) { _preview.Clear(); _status.Text = ex.Message; }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(560, area.Width), Math.Min(480, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }
}
