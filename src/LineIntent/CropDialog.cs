namespace LineIntent;

/// <summary>Crop only the captured LINE bitmap. No desktop-wide selection or persistent screenshot.</summary>
internal sealed class CropDialog : Form
{
    private readonly Bitmap _source;
    private readonly CropCanvas _canvas;
    private readonly Button _ok = new() { Text = "辨識框選區域", AutoSize = true, Enabled = false };
    public Bitmap? SelectedBitmap { get; private set; }

    public CropDialog(Bitmap source)
    {
        _source = source;
        Text = "框選 LINE 訊息區（保留左右氣泡與邊界）";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1080, 780);
        MinimumSize = new Size(560, 400);
        Font = new Font("Microsoft JhengHei UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        _canvas = new CropCanvas(source) { Dock = DockStyle.Fill };
        _canvas.SelectionChanged += (_, _) => _ok.Enabled = _canvas.SourceSelection.Width >= 12 && _canvas.SourceSelection.Height >= 12;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        _ok.Click += (_, _) =>
        {
            var crop = _canvas.SourceSelection;
            if (crop.Width < 12 || crop.Height < 12) return;
            SelectedBitmap = _source.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            DialogResult = DialogResult.OK;
            Close();
        };
        footer.Controls.Add(cancel);
        footer.Controls.Add(_ok);
        Controls.Add(_canvas);
        Controls.Add(footer);
        Controls.Add(new Label { Text = "框選整個訊息區，保留左右氣泡位置；避開聯絡人清單、標題與輸入框。接著可核對說話者。", Dock = DockStyle.Top, Height = 60, Padding = new Padding(12), AutoEllipsis = true });
        CancelButton = cancel;
        AcceptButton = _ok;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(560, area.Width), Math.Min(400, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    private sealed class CropCanvas : Control
    {
        private readonly Bitmap _image;
        private Point _start;
        private Point _end;
        private bool _dragging;
        public event EventHandler? SelectionChanged;
        public CropCanvas(Bitmap image)
        {
            _image = image;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 35, 43);
            Cursor = Cursors.Cross;
        }
        private Rectangle ImageBounds
        {
            get
            {
                var scale = Math.Min((double)ClientSize.Width / _image.Width, (double)ClientSize.Height / _image.Height);
                var width = (int)(_image.Width * scale);
                var height = (int)(_image.Height * scale);
                return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
            }
        }
        private Rectangle ScreenSelection => Rectangle.FromLTRB(Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y), Math.Max(_start.X, _end.X), Math.Max(_start.Y, _end.Y));
        public Rectangle SourceSelection
        {
            get
            {
                var bounds = ImageBounds;
                if (bounds.Width < 1 || bounds.Height < 1) return Rectangle.Empty;
                var selection = Rectangle.Intersect(bounds, ScreenSelection);
                if (selection.Width < 1 || selection.Height < 1) return Rectangle.Empty;
                return Rectangle.Intersect(new Rectangle(0, 0, _image.Width, _image.Height), Rectangle.FromLTRB(
                    (int)((selection.Left - bounds.Left) * (double)_image.Width / bounds.Width),
                    (int)((selection.Top - bounds.Top) * (double)_image.Height / bounds.Height),
                    (int)((selection.Right - bounds.Left) * (double)_image.Width / bounds.Width),
                    (int)((selection.Bottom - bounds.Top) * (double)_image.Height / bounds.Height)));
            }
        }
        private Point Clamp(Point p)
        {
            var b = ImageBounds;
            return new Point(Math.Clamp(p.X, b.Left, b.Right), Math.Clamp(p.Y, b.Top, b.Bottom));
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !ImageBounds.Contains(e.Location)) return;
            _start = _end = Clamp(e.Location);
            _dragging = true;
            Capture = true;
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            _end = Clamp(e.Location);
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _end = Clamp(e.Location);
            _dragging = false;
            Capture = false;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _start = _end = Point.Empty;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = ImageBounds;
            e.Graphics.DrawImage(_image, bounds);
            if (ScreenSelection.Width <= 0 || ScreenSelection.Height <= 0) return;
            using var outside = new Region(bounds);
            outside.Exclude(ScreenSelection);
            using var shade = new SolidBrush(Color.FromArgb(125, 0, 0, 0));
            e.Graphics.FillRegion(shade, outside);
            using var pen = new Pen(Color.FromArgb(0, 220, 135), 3);
            e.Graphics.DrawRectangle(pen, ScreenSelection);
        }
    }
}
