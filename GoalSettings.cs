using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>글자 수 세기 위젯이 쓰는 분량 계산: 글자 수 · 단어 수 · 문장 수 · 용량(바이트), 공백 · 문장부호 · 특수문자 제외.</summary>
internal static class GoalCounter
{
    public static readonly string[] UnitNames = { "글자 수", "단어 수", "문장 수", "용량" };
    private static readonly Regex Words = new(@"\S+", RegexOptions.Compiled);
    private static readonly Regex Sentences = new(@"[^.!?…。！？\r\n]*[\p{L}\p{N}][^.!?…。！？\r\n]*[.!?…。！？]*", RegexOptions.Compiled);

    public static string UnitLabel(AppSettings s) => s.GoalUnit switch { 1 => "단어", 2 => "문장", 3 => "바이트", _ => "자" };

    public static long Count(string text, AppSettings s)
    {
        switch (s.GoalUnit)
        {
            case 1: return Words.Matches(text).Count;
            case 2: return Sentences.Matches(text).Count;
            case 3: return Encoding.UTF8.GetByteCount(text.Replace("\r", ""));
        }
        long n = 0;
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (s.GoalExcludeSpaces && char.IsWhiteSpace(ch)) continue;
            if (s.GoalExcludePunct && char.IsPunctuation(ch)) continue;
            if (s.GoalExcludeSpecial && (char.IsSymbol(ch) || char.IsControl(ch))) continue;
            n++;
        }
        return n;
    }
}

/// <summary>동그란 선택 표시가 있는 카드 한 장(분량 계산 단위 · 목록 글자 수 방식 고르기).</summary>
internal sealed class ChoiceCard : Control
{
    private readonly string glyphText;
    private readonly bool glyphIsIcon;
    private bool selected;
    public event Action? Chosen;

    public ChoiceCard(string label, string glyph, bool iconFont = false)
    {
        Text = label;
        glyphText = glyph;
        glyphIsIcon = iconFont;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Chosen?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
        using (var path = Rounded(r, 10))
        {
            g.FillPath(Brushes.White, path);
            using var pen = new Pen(selected ? Color.FromArgb(24, 24, 24) : Color.FromArgb(226, 226, 226), selected ? 2.4f : 1f);
            g.DrawPath(pen, path);
        }
        // 오른쪽 위 라디오
        float k = DeviceDpi / 96f, d = 22 * k, rx = Width - 14 * k - d, ry = 12 * k;
        using (var pen = new Pen(selected ? Color.FromArgb(24, 24, 24) : Color.FromArgb(205, 205, 205), 1.4f))
        {
            if (selected) { using var fb = new SolidBrush(Color.FromArgb(24, 24, 24)); g.FillEllipse(fb, rx, ry, d, d); g.FillEllipse(Brushes.White, rx + 7 * k, ry + 7 * k, d - 14 * k, d - 14 * k); }
            else g.DrawEllipse(pen, rx, ry, d, d);
        }
        Color ink = Color.FromArgb(70, 70, 70);
        if (glyphIsIcon)
        {
            using var f = new Font(MainForm.IconFontName(), 18f);
            TextRenderer.DrawText(g, glyphText, f, new Rectangle((int)(16 * k), (int)(14 * k), (int)(40 * k), (int)(34 * k)), ink, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
        else
        {
            using var f = new Font(NovelStore.UiFontFamily, 15f, FontStyle.Bold);
            TextRenderer.DrawText(g, glyphText, f, new Rectangle((int)(16 * k), (int)(14 * k), (int)(70 * k), (int)(34 * k)), ink, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(16 * k), Height - (int)(38 * k), Width - (int)(24 * k), (int)(26 * k)), Color.FromArgb(30, 30, 30), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float dd = rad * 2;
        p.AddArc(r.X, r.Y, dd, dd, 180, 90); p.AddArc(r.Right - dd, r.Y, dd, dd, 270, 90);
        p.AddArc(r.Right - dd, r.Bottom - dd, dd, dd, 0, 90); p.AddArc(r.X, r.Bottom - dd, dd, dd, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>켜고 끄는 스위치(알약 + 동그라미).</summary>
internal sealed class ToggleSwitch : Control
{
    private bool on;
    public event Action<bool>? Toggled;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(54, 30); // 놓는 쪽에서 화면 배율에 맞춰 다시 정한다
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool On { get => on; set { if (on == value) return; on = value; Invalidate(); } }
    protected override void OnClick(EventArgs e) { base.OnClick(e); on = !on; Invalidate(); Toggled?.Invoke(on); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = new GraphicsPath())
        {
            float d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 90, 180); path.AddArc(r.Right - d, r.Y, d, d, 270, 180); path.CloseFigure();
            using var fb = new SolidBrush(on ? Color.FromArgb(24, 24, 24) : Color.FromArgb(226, 226, 226));
            g.FillPath(fb, path);
        }
        float kd = r.Height - 8, kx = on ? r.Right - kd - 4 : r.X + 4;
        g.FillEllipse(Brushes.White, kx, r.Y + 4, kd, kd);
    }
}


/// <summary>
/// 글자 수 세기 설정(위젯의 톱니바퀴): 글자 수 계산 기준(분량 계산 단위 · 공백 / 문장부호 / 특수문자 제외) · 목표 설정(편당 분량 목표) ·
/// 목록 글자 수 기준(소설 메인 · 파일 트리 목록에 보이는 글자 수에 공백을 넣을지). 바꾸는 즉시 저장된다.
/// 화면 배율에 맞춰 모든 좌표 · 글자를 직접 곱해서(S) 자리를 잡는다.
/// </summary>
internal sealed class GoalSettingsDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(244, 244, 244), Violet = Color.FromArgb(120, 90, 220), Dim = Color.FromArgb(100, 100, 100);
    private readonly AppSettings s;
    private readonly Panel body;
    private int y;
    private readonly int left, contentW;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private Font F(float pt, FontStyle st = FontStyle.Regular) => new(NovelStore.UiFontFamily, pt, st);

    public GoalSettingsDialog(AppSettings settings)
    {
        s = settings;
        Text = "글자 수 세기";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None; // 좌표는 아래에서 직접 곱한다(자동 배율과 겹치면 칸이 어긋난다)
        Font = F(10f);
        BackColor = Bg;
        ClientSize = new Size(S(760), S(600));

        body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Bg };
        Controls.Add(body);
        left = S(32);
        contentW = ClientSize.Width - left * 2 - SystemInformation.VerticalScrollBarWidth; // 스크롤바에 가려지지 않게
        y = S(24);

        // ── 글자 수 계산 기준 ──
        Section("글자 수 계산 기준");
        Head("", "분량 계산 단위", "분량 계산의 단위를 설정합니다. 연재 플랫폼 기준에 맞춰 조정할 수 있습니다.");
        var cards = new[]
        {
            new ChoiceCard("글자 수", "A", false), new ChoiceCard("단어 수", "abc", false),
            new ChoiceCard("문장 수", "¶", false), new ChoiceCard("용량", "", true),
        };
        int gap = S(12), cardW = (contentW - gap * 3) / 4, cardH = S(112);
        for (int i = 0; i < cards.Length; i++)
        {
            int idx = i;
            var c = cards[i];
            c.Bounds = new Rectangle(left + i * (cardW + gap), y, cardW, cardH);
            c.Selected = s.GoalUnit == i;
            c.Chosen += () => { s.GoalUnit = idx; foreach (var o in cards) o.Selected = o == c; Save(); };
            body.Controls.Add(c);
        }
        y += cardH + S(26);
        Toggle("", "공백 제외하기", "분량 계산 시 공백을 제외합니다.", s.GoalExcludeSpaces, v => s.GoalExcludeSpaces = v);
        Toggle("", "문장부호 제외하기", "분량 계산 시 문장부호를 제외합니다.", s.GoalExcludePunct, v => s.GoalExcludePunct = v);
        Toggle("", "특수문자 제외하기", "분량 계산 시 특수문자를 제외합니다.", s.GoalExcludeSpecial, v => s.GoalExcludeSpecial = v);

        // ── 목표 설정 ──
        Rule();
        Section("목표 설정");
        int goalTop = y;
        Head("", "편당 분량 목표", "한 편당 작성 목표 분량을 설정합니다.");
        var goal = new NumericUpDown
        {
            Minimum = 1, Maximum = 10_000_000, Value = Math.Clamp(s.WriterGoalChars, 1, 10_000_000), Width = S(150),
            Location = new Point(left + contentW - S(150), goalTop + S(2)), Font = F(11f), BorderStyle = BorderStyle.FixedSingle, ThousandsSeparator = false,
        };
        goal.ValueChanged += (_, _) => { s.WriterGoalChars = (int)goal.Value; Save(); };
        body.Controls.Add(goal);
        y += S(6);

        // ── 목록 글자 수 기준 ──
        Rule();
        Section("목록 글자 수 기준");
        Head("", "목록 글자 수 계산 방식", "목록에서 표시되는 글자 수는 별도로 계산되어 위 설정을 따라가지 않습니다. 공백 포함 또는 공백 제외 중 선택할 수 있습니다.");
        var excl = new ChoiceCard("공백 제외", "", true);
        var incl = new ChoiceCard("공백 포함", "", true);
        int half = (contentW - gap) / 2, listH = S(100);
        excl.Bounds = new Rectangle(left, y, half, listH);
        incl.Bounds = new Rectangle(left + half + gap, y, half, listH);
        excl.Selected = !s.ListCountWithSpaces; incl.Selected = s.ListCountWithSpaces;
        excl.Chosen += () => { s.ListCountWithSpaces = false; excl.Selected = true; incl.Selected = false; Save(); };
        incl.Chosen += () => { s.ListCountWithSpaces = true; excl.Selected = false; incl.Selected = true; Save(); };
        body.Controls.Add(excl); body.Controls.Add(incl);
        y += listH + S(32);
        body.AutoScrollMinSize = new Size(0, y);
    }

    private void Save() { s.Save(); NovelStore.ListCountWithSpaces = s.ListCountWithSpaces; }

    private Label Add(string text, Font font, Color color, int x, int top, int width = 0)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Bg, UseMnemonic = false, Location = new Point(x, top) };
        if (width > 0)
        {
            // 폭에 맞춰 줄바꿈한 높이
            var sz = TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            l.AutoSize = false;
            l.Size = new Size(width, sz.Height + S(4));
        }
        else l.AutoSize = true;
        body.Controls.Add(l);
        return l;
    }

    private void Section(string text)
    {
        Add(text, F(12.5f, FontStyle.Bold), Color.Black, left, y);
        y += S(46);
    }

    private void Rule()
    {
        y += S(6);
        body.Controls.Add(new Panel { Bounds = new Rectangle(left, y, contentW, 1), BackColor = Color.FromArgb(226, 226, 226) });
        y += S(30);
    }

    /// <summary>아이콘 + 제목 + 설명(폭에 맞춰 줄바꿈). 쓴 만큼 y가 내려간다.</summary>
    private void Head(string glyph, string title, string desc)
    {
        Add(glyph, new Font(MainForm.IconFontName(), 14f), Violet, left, y);
        Add(title, F(11.5f), Color.Black, left + S(34), y + S(1));
        y += S(34);
        var d = Add(desc, F(10f), Dim, left, y, contentW);
        y += d.Height + S(14);
    }

    private void Toggle(string glyph, string title, string desc, bool on, Action<bool> set)
    {
        Add(glyph, new Font(MainForm.IconFontName(), 14f), Violet, left, y);
        Add(title, F(11.5f), Color.Black, left + S(34), y + S(1));
        var sw = new ToggleSwitch { On = on, Size = new Size(S(54), S(30)), Location = new Point(left + contentW - S(54), y) };
        sw.Toggled += v => { set(v); Save(); };
        body.Controls.Add(sw);
        y += S(36);
        var d = Add(desc, F(10f), Dim, left, y, contentW - S(70));
        y += d.Height + S(22);
    }
}
