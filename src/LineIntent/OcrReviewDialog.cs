using LineIntent.Core;

namespace LineIntent;

/// <summary>In-memory OCR review: image positions stay visible while the user verifies sender labels.</summary>
internal sealed class OcrReviewDialog : Form
{
    private readonly Bitmap _source;
    private readonly IReadOnlyList<ChatRecognizedMessage> _messages;
    private readonly string _captureNote;
    private readonly DataGridView _rows = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        Name = "ReviewRows", RowHeadersVisible = true, RowHeadersWidth = 70, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = Color.White, AutoGenerateColumns = false, EditMode = DataGridViewEditMode.EditOnEnter
    };
    private readonly Panel _image = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(235, 238, 237) };
    private readonly Label _validation = new() { Name = "ReviewStatus", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 8) };
    public string Transcript { get; private set; } = "";

    public OcrReviewDialog(Bitmap source, ChatRecognitionResult result)
    {
        _source = source;
        _messages = result.Messages;
        _captureNote = string.Join("\n", result.Warnings.Where(warning => warning.Contains("上緣") || warning.Contains("位置資料")));
        Text = "確認 OCR 對話 · 左邊是對方，右邊是我";
        Font = new Font("Microsoft JhengHei UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1180, 800);
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "點訊息文字即可補字；只改說話者不會補上空白文字。點選一列可對照下方放大圖。\n無關或不需要的項目請勾選右側「略過」；底部會列出尚未完成的列號。",
            Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 12)
        }, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        content.Controls.Add(_image, 0, 0);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.Controls.Add(_rows, 0, 0);
        content.Controls.Add(editor, 1, 0);
        _rows.Columns.Add(new DataGridViewComboBoxColumn { Name = "Speaker", HeaderText = "說話者", Width = 112, DataSource = new[] { "對方", "我", "未確認" }, SortMode = DataGridViewColumnSortMode.NotSortable });
        _rows.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "訊息（點此補字）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, NullValue = "" } });
        _rows.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Skip", HeaderText = "略過", Width = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var message in _messages)
        {
            var index = _rows.Rows.Add(message.Speaker == "me" ? "我" : message.Speaker == "other" ? "對方" : "未確認",
                message.Text, false);
            if (string.IsNullOrWhiteSpace(message.Text)) _rows.Rows[index].Cells["Message"].Value = null;
            _rows.Rows[index].HeaderCell.Value = (index + 1).ToString();
        }
        _rows.SelectionChanged += (_, _) => _image.Invalidate();
        _rows.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_rows.IsCurrentCellDirty && _rows.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _rows.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _rows.CellValueChanged += (_, _) => UpdateValidation();
        _rows.CellEndEdit += (_, _) => UpdateValidation();
        _rows.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || _rows.Columns["Message"] is not { } messageColumn || e.ColumnIndex != messageColumn.Index
                || !string.IsNullOrWhiteSpace(_rows.Rows[e.RowIndex].Cells["Message"].Value?.ToString())) return;
            e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            TextRenderer.DrawText(e.Graphics!, "（點此補字，或勾選略過）", e.CellStyle?.Font ?? Font,
                Rectangle.Inflate(e.CellBounds, -4, -2), Color.Gray, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
            e.Handled = true;
        };
        _rows.DataError += (_, e) => { e.ThrowException = false; e.Cancel = true; };
        _image.Paint += (_, e) => DrawSource(e.Graphics);
        _image.Resize += (_, _) => _image.Invalidate();
        var editActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var edit = new Button { Text = "補字／修改", AutoSize = true };
        edit.Click += (_, _) => { if (_rows.CurrentRow is { } row) { _rows.CurrentCell = row.Cells["Message"]; _rows.BeginEdit(true); } };
        var hmm = new Button { Name = "FillHmm", Text = "填入「嗯」", AutoSize = true };
        hmm.Click += (_, _) => FillSelectedText("嗯");
        var sigh = new Button { Name = "FillSigh", Text = "填入「唉」", AutoSize = true };
        sigh.Click += (_, _) => FillSelectedText("唉");
        var skip = new Button { Name = "SkipSelected", Text = "略過此列", AutoSize = true };
        skip.Click += (_, _) => { _rows.EndEdit(); if (_rows.CurrentRow is { } row) row.Cells["Skip"].Value = true; };
        editActions.Controls.AddRange([edit, hmm, sigh, skip]);
        editor.Controls.Add(editActions, 0, 1);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(_validation, 0, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "套用對話", AutoSize = true, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", AutoSize = true, Padding = new Padding(8), DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => TryApplyTranscript();
        footer.Controls.Add(apply);
        footer.Controls.Add(cancel);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        CancelButton = cancel;
        UpdateValidation();
    }

    private OcrReviewResult ValidateRows() => OcrReview.Validate(_rows.Rows.Cast<DataGridViewRow>()
        .Select(row => new OcrReviewEntry(row.Cells["Speaker"].Value?.ToString() switch { "我" => "me", "對方" => "other", _ => "unknown" },
            row.Cells["Message"].Value?.ToString() ?? "", row.Cells["Skip"].Value is true)).ToArray());

    private void UpdateValidation()
    {
        var result = ValidateRows();
        foreach (DataGridViewRow row in _rows.Rows)
        {
            row.ErrorText = string.Join(" ", result.Issues.Where(issue => issue.RowIndex == row.Index).Select(issue => issue.Message));
            row.DefaultCellStyle.ForeColor = row.Cells["Skip"].Value is true ? Color.Gray : Color.Black;
        }
        _validation.ForeColor = result.CanApply ? Color.DarkGreen : Color.FromArgb(145, 70, 20);
        var remainingRows = result.Issues.Where(issue => issue.RowIndex >= 0).Select(issue => issue.RowIndex).Distinct().Count();
        _validation.Text = result.CanApply ? $"核對完成，可套用 {_rows.Rows.Count - result.SkippedCount} 則訊息；已略過 {result.SkippedCount} 列。此步驟不傳送 API。"
            : (remainingRows > 0 ? $"尚有 {remainingRows} 列需要處理；已略過 {result.SkippedCount} 列。\n" : $"目前無法套用；已略過 {result.SkippedCount} 列。\n")
              + string.Join("\n", result.Issues.Take(3).Select(issue => issue.Message))
              + (result.Issues.Count > 3 ? "\n其餘待處理列請查看列號旁的標記。" : "");
        if (_captureNote.Length > 0) _validation.Text += "\n影像提醒：" + _captureNote;
    }

    internal void FillSelectedText(string text)
    {
        _rows.EndEdit();
        if (_rows.CurrentRow is not { } row) return;
        row.Cells["Message"].Value = text;
        row.Cells["Skip"].Value = false;
        UpdateValidation();
    }

    internal bool TryApplyTranscript()
    {
        _rows.EndEdit();
        var result = ValidateRows();
        UpdateValidation();
        if (!result.CanApply)
        {
            var first = result.Issues.First();
            if (first.RowIndex >= 0)
            {
                _rows.CurrentCell = _rows.Rows[first.RowIndex].Cells[first.Field == "speaker" ? "Speaker" : "Message"];
                _rows.Focus();
                _rows.BeginEdit(true);
            }
            return false;
        }
        Transcript = result.Transcript;
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }

    private void DrawSource(Graphics graphics)
    {
        if (_image.Width < 1 || _image.Height < 1) return;
        var overviewHeight = _image.Height * .62;
        var scale = Math.Min((double)_image.Width / _source.Width, overviewHeight / _source.Height);
        var width = (float)(_source.Width * scale);
        var height = (float)(_source.Height * scale);
        var originX = (_image.Width - width) / 2;
        var originY = (float)((overviewHeight - height) / 2);
        graphics.DrawImage(_source, originX, originY, width, height);
        if (_rows.CurrentRow is null || _rows.CurrentRow.Index >= _messages.Count) return;
        var box = _messages[_rows.CurrentRow.Index].Bounds;
        using var pen = new Pen(Color.FromArgb(235, 87, 32), 3);
        graphics.DrawRectangle(pen, originX + (float)(box.X * scale), originY + (float)(box.Y * scale),
            Math.Max(2, (float)(box.Width * scale)), Math.Max(2, (float)(box.Height * scale)));
        var detailTop = (float)overviewHeight + 26;
        graphics.DrawString("選取訊息（放大）", Font, Brushes.DimGray, 8, (float)overviewHeight + 3);
        var detailHeight = _image.Height - detailTop - 8;
        var sourceBox = RectangleF.Intersect(new RectangleF(0, 0, _source.Width, _source.Height),
            new RectangleF((float)box.X - 12, (float)box.Y - 12, (float)box.Width + 24, (float)box.Height + 24));
        if (detailHeight <= 0 || sourceBox.Width <= 0 || sourceBox.Height <= 0) return;
        var zoom = Math.Min(3, Math.Min((_image.Width - 16) / sourceBox.Width, detailHeight / sourceBox.Height));
        var destination = new RectangleF((_image.Width - sourceBox.Width * zoom) / 2,
            detailTop + (detailHeight - sourceBox.Height * zoom) / 2, sourceBox.Width * zoom, sourceBox.Height * zoom);
        graphics.DrawImage(_source, destination, sourceBox, GraphicsUnit.Pixel);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(720, area.Width), Math.Min(480, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }
}
