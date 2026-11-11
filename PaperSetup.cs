namespace CoffeeViewer;

/// <summary>편집 용지: 판형(폭 · 길이) · 방향 · 여백(mm). Fit이면 용지 없이 창 폭에 맞춰 보여준다(기본).</summary>
public sealed class PaperSetup
{
    public bool Fit { get; set; }
    public string Name { get; set; } = "A4 (국배판)";
    public double Width { get; set; } = 210;
    public double Height { get; set; } = 297;
    public bool Landscape { get; set; }
    public double Top { get; set; } = 15;
    public double Bottom { get; set; } = 6;
    public double Left { get; set; } = 15;
    public double Right { get; set; } = 10;
    public double Header { get; set; } = 8;
    public double Footer { get; set; }
    public double Gutter { get; set; }

    /// <summary>글이 놓이는 칸의 폭(mm) = 용지 폭 − 왼쪽 · 오른쪽 · 제본 여백.</summary>
    public double ContentWidth => Math.Max(20, Width - Left - Right - Gutter);

    public PaperSetup Clone() => (PaperSetup)MemberwiseClone();
}

/// <summary>편집 용지 설정 창(한글의 "편집 용지"를 본뜬 것): 용지 종류 · 방향 · 여백 · 미리보기.</summary>
internal sealed class PaperForm : Form
{
    private static readonly (string Name, double W, double H)[] Presets =
    {
        ("A4 (국배판)", 210, 297), ("A5 (국판)", 148, 210), ("A3", 297, 420), ("B4", 257, 364), ("B5", 182, 257),
        ("신국판", 152, 225), ("46판", 127, 188), ("크라운판", 176, 248), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6),
    };
    private const string FitName = "화면에 맞춤 (용지 없음)";
    private const string CustomName = "사용자 지정";

    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown width = Mm(50, 1000), height = Mm(50, 1500);
    private readonly NumericUpDown top = Mm(0, 200), bottom = Mm(0, 200), left = Mm(0, 200), right = Mm(0, 200),
        header = Mm(0, 100), footer = Mm(0, 100), gutter = Mm(0, 100);
    private readonly RadioButton portrait = new() { Text = "▯ 세로", Appearance = Appearance.Button, AutoSize = false, Size = new Size(84, 34), TextAlign = ContentAlignment.MiddleCenter };
    private readonly RadioButton landscape = new() { Text = "▭ 가로", Appearance = Appearance.Button, AutoSize = false, Size = new Size(84, 34), TextAlign = ContentAlignment.MiddleCenter };
    private readonly CheckBox asDefault = new() { Text = "현재 설정 값을 새 문서에도 적용", AutoSize = true };
    private readonly Panel preview = new() { Size = new Size(300, 210), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(235, 236, 240) };
    private bool loading;
    private FlowLayoutPanel? bottomPanel;
    private TableLayoutPanel? rootPanel;

    /// <summary>화면 배율이 높아도 버튼이 잘리지 않게: 내용이 필요로 하는 높이에 맞춰 창 높이를 정한다(화면보다 크면 화면에 맞추고 안쪽을 스크롤).</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (rootPanel == null || bottomPanel == null) return;
        int need = rootPanel.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + bottomPanel.Height + 8;
        int maxH = Screen.FromControl(this).WorkingArea.Height - 80;
        int h = Math.Min(Math.Max(need, ClientSize.Height), maxH);
        if (h != ClientSize.Height) { ClientSize = new Size(ClientSize.Width, h); CenterToParent(); }
    }

    public PaperSetup Result { get; private set; } = new() { Fit = true };
    public bool AsDefault => asDefault.Checked;

    private static NumericUpDown Mm(int min, int max) => new() { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = 1, Width = 80 };

    public PaperForm(PaperSetup? current)
    {
        Text = "편집 용지";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 9f);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(14) };
        Controls.Add(root);

        Label Heading(string t) => new() { Text = t, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(50, 80, 160), Margin = new Padding(0, 10, 0, 4) };
        FlowLayoutPanel Row(params Control[] c)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            f.Controls.AddRange(c);
            return f;
        }
        Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 4, 0) };

        kind.Items.Add(FitName);
        foreach (var p in Presets) kind.Items.Add($"{p.Name}  [{p.W:0.#} x {p.H:0.#} mm]");
        kind.Items.Add(CustomName);

        root.Controls.Add(Heading("용지 종류"));
        root.Controls.Add(Row(L("종류"), kind));
        root.Controls.Add(Row(L("폭"), width, L("mm"), L("길이"), height, L("mm")));

        root.Controls.Add(Heading("용지 방향"));
        root.Controls.Add(Row(portrait, landscape));

        root.Controls.Add(Heading("용지 여백"));
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0) };
        void Add(string label, NumericUpDown n, int col, int row)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(8, 6, 4, 0) }, col, row);
            grid.Controls.Add(n, col + 1, row);
        }
        Add("위쪽", top, 0, 0); Add("아래쪽", bottom, 2, 0);
        Add("왼쪽", left, 0, 1); Add("오른쪽", right, 2, 1);
        Add("머리말", header, 0, 2); Add("꼬리말", footer, 2, 2);
        Add("제본", gutter, 0, 3);
        root.Controls.Add(grid);
        root.Controls.Add(new Label { Text = "머리말 · 꼬리말 · 제본 여백은 인쇄 · 내보내기에 쓰입니다(화면 보기에는 제본 여백만 반영).", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0), Margin = new Padding(0, 4, 0, 0) });

        preview.Margin = new Padding(8, 6, 0, 0);
        preview.Paint += PaintPreview;
        root.Controls.Add(preview);
        asDefault.Margin = new Padding(0, 10, 0, 0);
        root.Controls.Add(asDefault);

        var ok = new Button { Text = "설정(&D)", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 32), Padding = new Padding(10, 4, 10, 4) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 32), Padding = new Padding(10, 4, 10, 4) };
        // 버튼은 창 아래에 따로 붙박이로 둔다(내용이 길어도 잘리지 않게). 내용은 넘치면 스크롤된다.
        // 아래 단추 줄: 단추 크기에 맞춰 스스로 자라는 줄(배율이 높아도 잘리지 않는다).
        var bottom2 = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(16, 12, 16, 12) };
        bottom2.Controls.Add(cancel);
        bottom2.Controls.Add(ok);
        Controls.Add(bottom2);
        bottomPanel = bottom2;
        rootPanel = root;
        AcceptButton = ok;
        CancelButton = cancel;
        ClientSize = new Size(480, 720);
        MinimumSize = Size;

        // 지금 값으로 채운다
        var cur = current ?? new PaperSetup { Fit = true };
        loading = true;
        if (cur.Fit) kind.SelectedIndex = 0;
        else
        {
            int idx = Array.FindIndex(Presets, p => p.Name == cur.Name);
            kind.SelectedIndex = idx >= 0 ? idx + 1 : kind.Items.Count - 1;
        }
        width.Value = (decimal)cur.Width; height.Value = (decimal)cur.Height;
        (cur.Landscape ? landscape : portrait).Checked = true;
        top.Value = (decimal)cur.Top; bottom.Value = (decimal)cur.Bottom; left.Value = (decimal)cur.Left; right.Value = (decimal)cur.Right;
        header.Value = (decimal)cur.Header; footer.Value = (decimal)cur.Footer; gutter.Value = (decimal)cur.Gutter;
        loading = false;
        UpdateEnabled();

        kind.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            int i = kind.SelectedIndex;
            if (i >= 1 && i <= Presets.Length)
            {
                var (_, w, h) = Presets[i - 1];
                loading = true;
                bool land = landscape.Checked;
                width.Value = (decimal)(land ? h : w);
                height.Value = (decimal)(land ? w : h);
                loading = false;
            }
            UpdateEnabled();
        };
        void SizeEdited(object? s, EventArgs e)
        {
            if (loading) return;
            loading = true;
            (width.Value > height.Value ? landscape : portrait).Checked = true;
            if (kind.SelectedIndex != 0) kind.SelectedIndex = kind.Items.Count - 1; // 손으로 고치면 사용자 지정
            loading = false;
            preview.Invalidate();
        }
        width.ValueChanged += SizeEdited;
        height.ValueChanged += SizeEdited;
        EventHandler orient = (_, _) =>
        {
            if (loading) return;
            bool wantLand = landscape.Checked;
            if ((width.Value > height.Value) != wantLand)
            {
                loading = true;
                (width.Value, height.Value) = (height.Value, width.Value);
                loading = false;
            }
            preview.Invalidate();
        };
        portrait.CheckedChanged += orient;
        landscape.CheckedChanged += orient;
        foreach (var n in new[] { top, bottom, left, right, header, footer, gutter }) n.ValueChanged += (_, _) => preview.Invalidate();

        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (kind.SelectedIndex == 0) { Result = new PaperSetup { Fit = true }; return; }
            double w = (double)width.Value, h = (double)height.Value;
            var p = new PaperSetup
            {
                Name = kind.SelectedIndex <= Presets.Length ? Presets[kind.SelectedIndex - 1].Name : CustomName,
                Width = w, Height = h, Landscape = w > h,
                Top = (double)top.Value, Bottom = (double)bottom.Value, Left = (double)left.Value, Right = (double)right.Value,
                Header = (double)header.Value, Footer = (double)footer.Value, Gutter = (double)gutter.Value,
            };
            if (p.Left + p.Right + p.Gutter >= p.Width - 20 || p.Top + p.Bottom >= p.Height - 20)
            {
                MessageBox.Show(this, "여백이 너무 커서 글이 놓일 자리가 없어요.", "편집 용지", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            Result = p;
        };
    }

    private void UpdateEnabled()
    {
        bool paper = kind.SelectedIndex != 0;
        foreach (Control c in new Control[] { width, height, portrait, landscape, top, bottom, left, right, header, footer, gutter }) c.Enabled = paper;
        preview.Invalidate();
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (kind.SelectedIndex == 0)
        {
            using var f = new Font(Font.FontFamily, 9f);
            TextRenderer.DrawText(g, "창 폭에 맞춰 보여줍니다", f, preview.ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        double w = (double)width.Value, h = (double)height.Value;
        var area = Rectangle.Inflate(preview.ClientRectangle, -12, -12);
        double s = Math.Min(area.Width / w, area.Height / h);
        var page = new RectangleF((float)(area.X + (area.Width - w * s) / 2), (float)(area.Y + (area.Height - h * s) / 2), (float)(w * s), (float)(h * s));
        g.FillRectangle(Brushes.White, page);
        g.DrawRectangle(Pens.Gray, page.X, page.Y, page.Width, page.Height);
        var body = RectangleF.FromLTRB(
            page.Left + (float)(((double)left.Value + (double)gutter.Value) * s), page.Top + (float)((double)top.Value * s),
            page.Right - (float)((double)right.Value * s), page.Bottom - (float)((double)bottom.Value * s));
        if (body.Width <= 0 || body.Height <= 0) return;
        using var dash = new Pen(Color.FromArgb(120, 140, 200)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawRectangle(dash, body.X, body.Y, body.Width, body.Height);
        using var line = new Pen(Color.FromArgb(70, 70, 70));
        for (float y = body.Top + 3; y < body.Bottom - 2; y += 4)
            g.DrawLine(line, body.Left + 1, y, y % 20 < 4 ? body.Right - 12 : body.Right - 1, y);
    }
}
