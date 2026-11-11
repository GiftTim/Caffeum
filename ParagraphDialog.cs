namespace CoffeeViewer;

/// <summary>문단 서식 값(파일마다 기억). 정렬 · 줄 간격(%)은 서식창이 쓰는 LookAlign · LookLine과 같은 값을 쓰고, 나머지는 여기에 둔다.</summary>
public sealed class ParaFormat
{
    /// <summary>정렬: justify(양쪽) · left · center · right · distribute(배분) · divide(나눔).</summary>
    public string Align { get; set; } = "justify";
    public double Left { get; set; }
    public double Right { get; set; }
    /// <summary>첫 줄: 0 보통 · 1 들여쓰기 · 2 내어쓰기.</summary>
    public int FirstMode { get; set; }
    public double FirstPt { get; set; }
    /// <summary>줄 간격 방식: 0 글자에 따라(%) · 1 고정 값(pt).</summary>
    public int LineMode { get; set; }
    public int LinePercent { get; set; } = 160;
    public double LinePt { get; set; } = 16;
    public double Before { get; set; }
    public double After { get; set; }
    public bool UseGrid { get; set; } = true;
    /// <summary>한글 줄 나눔 단위: char(글자) · word(어절).</summary>
    public string KoUnit { get; set; } = "char";
    /// <summary>영어 줄 나눔 단위: word(단어) · char(글자).</summary>
    public string EnUnit { get; set; } = "word";
    public int MinSpace { get; set; } = 100;

    public ParaFormat Clone() => (ParaFormat)MemberwiseClone();
}

/// <summary>서식창의 [문단 서식]: 정렬 방식 · 여백 · 첫 줄 · 간격 · 줄 나눔 기준과 미리보기(한글의 같은 창 모양).</summary>
internal sealed class ParagraphDialog : Form
{
    private static readonly Color Accent = Color.FromArgb(0x3B, 0x6F, 0xD8);
    private readonly ParaFormat v;
    private readonly AlignToggle[] aligns;
    private readonly UnitUpDown left, right, firstPt, linePct, before, after, minSpace;
    private readonly RadioButton firstNormal, firstIndent, firstOutdent;
    private readonly ComboBox lineMode, koUnit, enUnit;
    private readonly CheckBox grid;
    private readonly PreviewBox preview;

    public ParaFormat Result => v;

    public ParagraphDialog(ParaFormat current)
    {
        v = current.Clone();
        Text = "문단 서식";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        ClientSize = new Size(716, 790);

        int Y = 14;
        Controls.Add(new Label { Text = "정렬 방식", AutoSize = true, Location = new Point(20, Y) });
        string[] keys = { "justify", "left", "center", "right", "distribute", "divide" };
        string[] tips = { "양쪽 정렬", "왼쪽 정렬", "가운데 정렬", "오른쪽 정렬", "배분 정렬", "나눔 정렬" };
        aligns = new AlignToggle[6];
        for (int i = 0; i < 6; i++)
        {
            string key = keys[i];
            aligns[i] = new AlignToggle(i) { Location = new Point(20 + i * 54, Y + 30), Size = new Size(46, 44), Checked = v.Align == key };
            new ToolTip().SetToolTip(aligns[i], tips[i]);
            aligns[i].CheckedChanged += (_, _) => { if (aligns[Array.IndexOf(keys, key)].Checked) { v.Align = key; preview?.Invalidate(); } };
            Controls.Add(aligns[i]);
        }

        Y = 100;
        Section("여백", 20, Y, 290);
        Section("첫 줄", 330, Y, 366);
        left = Num(110, Y + 36, v.Left, 0, 500, 1, "pt", 20, "왼쪽", x => v.Left = x);
        right = Num(110, Y + 78, v.Right, 0, 500, 1, "pt", 20, "오른쪽", x => v.Right = x);
        firstNormal = Radio("보통", 350, Y + 34, v.FirstMode == 0, () => v.FirstMode = 0);
        firstIndent = Radio("들여쓰기", 350, Y + 70, v.FirstMode == 1, () => v.FirstMode = 1);
        firstOutdent = Radio("내어쓰기", 350, Y + 106, v.FirstMode == 2, () => v.FirstMode = 2);
        firstPt = new UnitUpDown { Unit = "pt", Location = new Point(480, Y + 90), Width = 130, DecimalPlaces = 1, Minimum = 0, Maximum = 500, Increment = 1, Value = (decimal)v.FirstPt };
        firstPt.ValueChanged += (_, _) => { v.FirstPt = (double)firstPt.Value; preview.Invalidate(); };
        Controls.Add(firstPt);
        void SyncFirst() { firstPt.Enabled = v.FirstMode != 0; }

        Y = 258;
        Section("간격", 20, Y, 676);
        Controls.Add(new Label { Text = "줄 간격", AutoSize = true, Location = new Point(34, Y + 44) });
        lineMode = new ComboBox { Location = new Point(110, Y + 40), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        lineMode.Items.AddRange(new object[] { "글자에 따라", "고정 값" });
        lineMode.SelectedIndex = v.LineMode;
        Controls.Add(lineMode);
        linePct = new UnitUpDown { Unit = "%", Location = new Point(110, Y + 82), Width = 130, Minimum = 100, Maximum = 300, Increment = 5, Value = Math.Clamp(v.LinePercent, 100, 300) };
        Controls.Add(linePct);
        lineMode.SelectedIndexChanged += (_, _) =>
        {
            v.LineMode = lineMode.SelectedIndex;
            if (v.LineMode == 0) { linePct.Minimum = 100; linePct.Maximum = 300; linePct.Increment = 5; linePct.DecimalPlaces = 0; linePct.Value = Math.Clamp(v.LinePercent, 100, 300); ((UnitUpDown)linePct).Unit = "%"; ((UnitUpDown)linePct).RefreshText(); }
            else { linePct.Minimum = 6; linePct.Maximum = 200; linePct.Increment = 1; linePct.DecimalPlaces = 1; linePct.Value = (decimal)Math.Clamp(v.LinePt, 6, 200); ((UnitUpDown)linePct).Unit = "pt"; ((UnitUpDown)linePct).RefreshText(); }
            preview.Invalidate();
        };
        linePct.ValueChanged += (_, _) => { if (v.LineMode == 0) v.LinePercent = (int)linePct.Value; else v.LinePt = (double)linePct.Value; preview.Invalidate(); };
        before = Num(520, Y + 40, v.Before, 0, 500, 1, "pt", 370, "문단 위", x => v.Before = x);
        after = Num(520, Y + 82, v.After, 0, 500, 1, "pt", 370, "문단 아래", x => v.After = x);
        grid = new CheckBox { Text = "편집 용지의 줄 격자 사용", AutoSize = true, Location = new Point(34, Y + 128), Checked = v.UseGrid };
        grid.CheckedChanged += (_, _) => v.UseGrid = grid.Checked;
        Controls.Add(grid);

        Y = 420;
        Section("줄 나눔 기준", 20, Y, 676);
        Controls.Add(new Label { Text = "한글 단위", AutoSize = true, Location = new Point(34, Y + 44) });
        koUnit = new ComboBox { Location = new Point(124, Y + 40), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        koUnit.Items.AddRange(new object[] { "글자", "어절" });
        koUnit.SelectedIndex = v.KoUnit == "word" ? 1 : 0;
        koUnit.SelectedIndexChanged += (_, _) => { v.KoUnit = koUnit.SelectedIndex == 1 ? "word" : "char"; preview.Invalidate(); };
        Controls.Add(koUnit);
        Controls.Add(new Label { Text = "영어 단위", AutoSize = true, Location = new Point(34, Y + 86) });
        enUnit = new ComboBox { Location = new Point(124, Y + 82), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        enUnit.Items.AddRange(new object[] { "단어", "글자" });
        enUnit.SelectedIndex = v.EnUnit == "char" ? 1 : 0;
        enUnit.SelectedIndexChanged += (_, _) => { v.EnUnit = enUnit.SelectedIndex == 1 ? "char" : "word"; preview.Invalidate(); };
        Controls.Add(enUnit);
        minSpace = Num(520, Y + 40, v.MinSpace, 50, 200, 1, "%", 370, "최소 공백", x => v.MinSpace = (int)x, decimals: 0);

        preview = new PreviewBox(v) { Location = new Point(20, 548), Size = new Size(676, 178) };
        Controls.Add(preview);

        var ok = new Button { Text = "설정", Location = new Point(496, 740), Size = new Size(96, 36), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", Location = new Point(600, 740), Size = new Size(96, 36), DialogResult = DialogResult.Cancel };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        foreach (var r in new[] { firstNormal, firstIndent, firstOutdent }) r.CheckedChanged += (_, _) => SyncFirst();
        SyncFirst();
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }

    private void Section(string text, int x, int y, int width)
    {
        var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = Accent };
        Controls.Add(l);
        int tw = TextRenderer.MeasureText(text, Font).Width;
        Controls.Add(new Panel { Location = new Point(x + tw + 14, y + 11), Size = new Size(width - tw - 14, 1), BackColor = Color.FromArgb(200, 200, 200) });
    }

    private UnitUpDown Num(int x, int y, double value, double min, double max, double inc, string unit, int labelX, string label, Action<double> set, int decimals = 1)
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(labelX + 14, y + 4) });
        var n = new UnitUpDown { Unit = unit, Location = new Point(x, y), Width = 130, DecimalPlaces = decimals, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)inc, Value = (decimal)Math.Clamp(value, min, max) };
        n.ValueChanged += (_, _) => { set((double)n.Value); preview?.Invalidate(); };
        Controls.Add(n);
        return n;
    }

    private RadioButton Radio(string text, int x, int y, bool on, Action chosen)
    {
        var r = new RadioButton { Text = text, AutoSize = true, Location = new Point(x, y), Checked = on };
        r.CheckedChanged += (_, _) => { if (r.Checked) { chosen(); preview?.Invalidate(); } };
        Controls.Add(r);
        return r;
    }

    /// <summary>정렬 아이콘 버튼(줄 여러 개로 정렬 모양을 그린다). 하나만 눌려 있다.</summary>
    private sealed class AlignToggle : RadioButton
    {
        private readonly int kind; // 0 양쪽 · 1 왼쪽 · 2 가운데 · 3 오른쪽 · 4 배분 · 5 나눔
        public AlignToggle(int kind)
        {
            this.kind = kind;
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            TextAlign = ContentAlignment.MiddleCenter;
            SetStyle(ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Checked ? Color.FromArgb(0xE3, 0xEC, 0xFB) : SystemColors.Control);
            using (var pen = new Pen(Checked ? Accent : Color.FromArgb(170, 170, 170), Checked ? 2f : 1f)) g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            using var line = new Pen(Color.FromArgb(70, 70, 70), 1.4f);
            int pad = 10, w = Width - pad * 2;
            for (int i = 0; i < 6; i++)
            {
                int y = 9 + i * 5;
                bool shortLine = i % 3 == 2 && kind != 0 && kind != 4 && kind != 5;
                int lw = shortLine ? w * 2 / 3 : w;
                int x = kind switch { 2 => pad + (w - lw) / 2, 3 => pad + (w - lw), _ => pad };
                if (kind == 4 && i % 2 == 1) { g.DrawLine(line, pad, y, pad + w / 3, y); g.DrawLine(line, pad + w * 2 / 3, y, pad + w, y); continue; }
                if (kind == 5 && i == 5) { for (int d = 0; d < w; d += 4) g.DrawLine(line, pad + d, y, pad + d + 2, y); continue; }
                g.DrawLine(line, x, y, x + lw, y);
            }
        }
    }

    /// <summary>미리보기: 이전 문단 · 현재 문단 · 이후 문단을 지금 값대로 흉내 내서 그린다.</summary>
    private sealed class PreviewBox : Control
    {
        private readonly ParaFormat v;
        public PreviewBox(ParaFormat v)
        {
            this.v = v;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            using (var pen = new Pen(Color.FromArgb(170, 170, 170))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var font = new Font(NovelStore.UiFontFamily, 8.5f);
            float ptPx = DeviceDpi / 72f;
            int fh = font.Height;
            float lineH = v.LineMode == 0 ? fh * v.LinePercent / 100f * 0.78f : (float)v.LinePt * ptPx * 0.78f;
            lineH = Math.Max(lineH, fh * 0.9f);
            int pad = 8;
            float y = pad;
            string prev = string.Concat(Enumerable.Repeat("이전 문단 ", 24));
            string cur = string.Concat(Enumerable.Repeat("가나다라AaBbCc12345 ", 12));
            string next = string.Concat(Enumerable.Repeat("이후 문단 ", 24));
            y = Para(g, font, prev, Color.FromArgb(140, 150, 175), pad, y, lineH, ptPx, indentFirst: 0, left: 0, right: 0, align: "left");
            y += (float)v.Before * ptPx * 0.78f;
            float first = v.FirstMode == 1 ? (float)v.FirstPt : v.FirstMode == 2 ? -(float)v.FirstPt : 0;
            y = Para(g, font, cur, Color.FromArgb(40, 40, 40), pad, y, lineH, ptPx, indentFirst: first, left: (float)v.Left, right: (float)v.Right, align: v.Align);
            y += (float)v.After * ptPx * 0.78f;
            Para(g, font, next, Color.FromArgb(140, 150, 175), pad, y, lineH, ptPx, indentFirst: 0, left: 0, right: 0, align: "left");
        }

        private float Para(Graphics g, Font font, string text, Color color, int pad, float y, float lineH, float ptPx, float indentFirst, float left, float right, string align)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            float areaL = pad + left * ptPx * 0.78f, areaR = Width - pad - right * ptPx * 0.78f;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            int idx = 0;
            bool firstLine = true;
            float space = TextRenderer.MeasureText("가 가", font, Size.Empty, flags).Width - TextRenderer.MeasureText("가가", font, Size.Empty, flags).Width;
            while (idx < words.Count && y + lineH <= Height)
            {
                float x0 = areaL + (firstLine ? indentFirst * ptPx * 0.78f : (indentFirst < 0 ? -indentFirst * ptPx * 0.78f * 0 : 0));
                if (indentFirst < 0 && !firstLine) x0 = areaL + (-indentFirst) * ptPx * 0.78f;
                if (indentFirst < 0 && firstLine) x0 = areaL;
                float avail = areaR - x0;
                var line = new List<string>();
                float used = 0;
                while (idx < words.Count)
                {
                    float w = TextRenderer.MeasureText(words[idx], font, Size.Empty, flags).Width;
                    float add = line.Count == 0 ? w : space + w;
                    if (line.Count > 0 && used + add > avail) break;
                    line.Add(words[idx]);
                    used += add;
                    idx++;
                }
                bool last = idx >= words.Count;
                float lx = x0;
                float gap = space;
                if ((align is "justify" or "divide" or "distribute") && !last && line.Count > 1)
                    gap = space + (avail - used) / (line.Count - 1);
                else if (align == "center") lx = x0 + (avail - used) / 2;
                else if (align == "right") lx = x0 + (avail - used);
                float x = lx;
                foreach (var w in line)
                {
                    TextRenderer.DrawText(g, w, font, new Point((int)x, (int)y), color, flags);
                    x += TextRenderer.MeasureText(w, font, Size.Empty, flags).Width + gap;
                }
                y += lineH;
                firstLine = false;
            }
            return y;
        }
    }
}
