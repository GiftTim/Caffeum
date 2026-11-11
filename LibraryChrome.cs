using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// 서재 화면 전용 색 묶음. 읽기 화면 테마(ReaderTheme) 하나에서 바탕 · 옆 막대 · 머리 막대 · 카드 · 강조색을 뽑아서,
/// 라이트 · 세피아 · 초록 · 다크 · 블랙 어느 테마든 같은 짜임새(머리 막대가 가장 진하고, 옆 막대, 본문 순)로 보이게 한다.
/// </summary>
internal sealed class LibraryPalette
{
    public required Color Main { get; init; }       // 책 목록 바탕
    public required Color Side { get; init; }       // 왼쪽 옆 막대
    public required Color Header { get; init; }     // 맨 위 머리 막대
    public required Color Fore { get; init; }
    public required Color Dim { get; init; }        // 작은 안내 글 · 머리글
    public required Color Line { get; init; }       // 구분선 · 테두리
    public required Color Card { get; init; }       // 버튼 · 입력칸 바탕
    public required Color CardHover { get; init; }
    public required Color Accent { get; init; }     // 강조(주 버튼 · 진행 막대)
    public required Color AccentFore { get; init; } // 강조색 위 글자
    public required Color AccentSoft { get; init; } // 선택된 줄 · 고른 정렬 버튼
    public bool Dark { get; init; }

    public static LibraryPalette From(ReaderTheme t)
    {
        Color main = t.Back, fore = t.Fore;
        Color accent = t.Link;
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        return new LibraryPalette
        {
            Main = main,
            Side = Themes.Blend(main, fore, t.Dark ? 0.045f : 0.035f),
            Header = Themes.Blend(main, fore, t.Dark ? 0.075f : 0.06f),
            Fore = fore,
            Dim = Themes.Blend(main, fore, 0.55f),
            Line = Themes.Blend(main, fore, 0.13f),
            Card = Themes.Blend(main, fore, 0.08f),
            CardHover = Themes.Blend(main, fore, 0.15f),
            Accent = accent,
            AccentFore = lum > 0.6 ? Color.Black : Color.White,
            AccentSoft = Themes.Blend(main, accent, t.Dark ? 0.28f : 0.18f),
            Dark = t.Dark,
        };
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>모서리가 둥근 납작한 버튼. Primary는 강조색으로 채우고, Selected는 정렬처럼 여럿 중 고른 것을 표시한다.</summary>
internal sealed class PillButton : Control
{
    private LibraryPalette? palette;
    private bool hover, down;
    private bool primary, selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Primary { get => primary; set { primary = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    /// <summary>둥근 모서리 바깥을 칠할 색. 비우면 부모 바탕색 — 검색칸처럼 부모와 다른 색 위에 얹을 때만 정한다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Surface { get; set; }
    /// <summary>작은 버튼(검색칸 안에 넣는 것). 안쪽 여백을 줄인다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Compact { get; set; }

    /// <summary>글자색을 따로 정할 때(소설 삭제하기의 붉은 글자 등).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? TextColor { get; set; }
    /// <summary>글자 앞에 붙는 작은 아이콘(아이콘 글꼴의 글자 하나).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string? Glyph { get => glyph; set { glyph = value; if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); } }
    private string? glyph;
    /// <summary>아이콘 글꼴 대신 그려 둔 그림을 아이콘 자리에 쓴다(보라색 책 모양 등).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Image? GlyphImage { get; set; }
    /// <summary>글자색에 맞춰 아이콘 그림을 그려 주는 함수(크기, 색). 위키 · 메모 아이콘과 같은 색이 되게 한다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Func<int, Color, Bitmap>? GlyphFactory { get; set; }
    private Bitmap? factoryBmp;
    private Color factoryColor;
    private bool muted;
    /// <summary>주 버튼을 옅게 보여 "아직 누를 수 없다"를 나타낸다(눌림 자체는 막지 않으니 쓰는 쪽이 처리한다).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Muted { get => muted; set { muted = value; Invalidate(); } }

    public PillButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AutoSize = true;
        Margin = new Padding(0, 0, 6, 0);
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (glyph != null) t.Width += S(26);
        return Compact ? new Size(t.Width + S(12) * 2, t.Height + S(7) * 2) : new Size(t.Width + S(14) * 2, t.Height + S(8) * 2);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    // 이 버튼 위에서 누른 키만 뗄 때 클릭으로 친다(창에서 Enter로 확인하고 나면 포커스가 옮겨 온 버튼에 '뗌'만 전달돼 저절로 눌리던 문제)
    private bool keyDownHere;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) keyDownHere = true;
    }
    protected override void OnLostFocus(EventArgs e) { keyDownHere = false; Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && keyDownHere) { keyDownHere = false; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Surface ?? Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 검색칸 안(Surface가 정해진 경우)은 바탕이 이미 카드색이라, 안 고른 버튼을 카드색으로 칠하면 칸에 묻혀 안 보인다.
        // 그래서 안 고른 버튼은 본문 바탕색 + 진한 테두리로 칸과 구분되게 한다.
        bool inField = Surface != null;
        Color fill = !Enabled ? p.Card
            : primary && muted ? Themes.Blend(p.Accent, p.Main, 0.55f)
            : primary ? (down ? Themes.Blend(p.Accent, Color.Black, 0.15f) : hover ? Themes.Blend(p.Accent, Color.White, 0.12f) : p.Accent)
            : selected ? p.AccentSoft
            : down || hover ? p.CardHover
            : inField ? p.Main : p.Card;
        Color border = primary && Enabled ? fill
            : selected ? Themes.Blend(p.AccentSoft, p.Accent, 0.5f)
            : inField ? Themes.Blend(p.Line, p.Dim, 0.45f) : p.Line;
        Color text = !Enabled ? p.Dim : primary ? p.AccentFore : TextColor ?? p.Fore;

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = LibraryPalette.Rounded(r, S(8)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = new Pen(Focused && ShowFocusCues ? p.Accent : border)) g.DrawPath(pen, path);
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (glyph == null)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        int tw = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        using var icon = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.95f);
        // 아이콘의 실제 폭을 재서 [아이콘 + 간격 + 글자] 덩어리를 버튼 가운데에 놓는다(가는 아이콘이 왼쪽으로 치우쳐 보이지 않게)
        if (GlyphFactory != null)
        {
            Color ic = primary ? text : TextColor ?? p.Dim;
            if (factoryBmp == null || factoryColor != ic) { factoryBmp?.Dispose(); factoryBmp = GlyphFactory(S(18) * 3, ic); factoryColor = ic; }
        }
        var gi = GlyphFactory != null ? factoryBmp : GlyphImage;
        int isz = gi != null ? S(18) : TextRenderer.MeasureText(glyph, icon, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        int gap = S(8);
        int x = (Width - (isz + gap + tw)) / 2;
        if (gi != null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(gi, new Rectangle(x, (Height - isz) / 2, isz, isz));
        }
        else
            TextRenderer.DrawText(g, glyph, icon, new Rectangle(x, 0, isz, Height), primary ? text : TextColor ?? p.Dim, flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + isz + gap, 0, tw + S(4), Height), text, flags);
    }
}

/// <summary>둥근 테두리 안에 [앞쪽 버튼들(AddLeading)] [테두리 없는 입력칸] [돋보기]를 차례로 넣은 검색칸.</summary>
internal sealed class SearchField : Panel
{
    public TextBox Box { get; }
    private LibraryPalette? palette;
    private readonly Font iconFont;
    private readonly List<PillButton> leading = new();

    /// <summary>돋보기와 입력칸 사이에 작은 버튼을 넣는다(모두 포함 · 하나라도 같은 검색 방식).</summary>
    public void AddLeading(PillButton button)
    {
        button.Compact = true;
        button.Margin = Padding.Empty;
        leading.Add(button);
        Controls.Add(button);
        if (palette != null) { button.Surface = palette.Card; button.SetPalette(palette); }
        PerformLayout();
    }

    public SearchField(string placeholder)
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        iconFont = new Font("Segoe MDL2 Assets", 10f);
        Box = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = placeholder };
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => Box.Focus();
        Disposed += (_, _) => iconFont.Dispose();
    }

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        BackColor = Parent?.BackColor ?? p.Main;
        Box.BackColor = p.Card;
        Box.ForeColor = p.Fore;
        foreach (var b in leading) { b.Surface = p.Card; b.SetPalette(p); }
        Invalidate();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int x = S(10), right = S(40); // 돋보기는 오른쪽 끝(OnPaint) — 그 자리만큼 입력칸 오른쪽을 비운다
        foreach (var b in leading)
        {
            b.Location = new Point(x, (Height - b.Height) / 2);
            x += b.Width + S(6);
        }
        if (leading.Count > 0) x += S(10);
        Box.SetBounds(x, (Height - Box.Height) / 2, Math.Max(10, Width - x - right), Box.Height);
    }

    /// <summary>안에 든 버튼까지 다 보이는 데 필요한 최소 폭(입력칸 자리 제외).</summary>
    public int LeadingWidth => S(10) + leading.Sum(b => b.Width + S(6)) + (leading.Count > 0 ? S(10) : 0) + S(40);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(S(320), Math.Max(Box.PreferredHeight + S(18), leading.Count > 0 ? leading.Max(b => b.Height) + S(10) : 0));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = palette;
        if (p == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(10)))
        {
            using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);
            using (var pen = new Pen(Box.Focused ? p.Accent : p.Line, Box.Focused ? 1.5f : 1f)) g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, "", iconFont, new Rectangle(Width - S(34), 0, S(20), Height), p.Dim, // 돋보기: 오른쪽 끝
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>"작은 영문 머리말 + 큰 제목" 두 줄 묶음(MY LIBRARY / 내 서재 같은). 글자 크기는 지금 글꼴 기준으로 정한다.</summary>
internal sealed class TitleBlock : Control
{
    private LibraryPalette? palette;
    private string caption;

    private string subtitle = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Caption { get => caption; set { caption = value; Invalidate(); } }

    /// <summary>큰 제목 밑의 작은 한 줄(지금 보는 폴더 · 고른 파일). 길면 …로 줄인다 — 폭은 바꾸지 않아서, 파일을 고를 때마다 옆 검색칸이 들썩이지 않는다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle { get => subtitle; set { if (subtitle == value) return; subtitle = value; Invalidate(); } }

    /// <summary>작은 줄을 쓸지. 켜 두면 글이 비어 있어도 자리를 잡아 둬서 높이가 바뀌지 않는다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowSubtitle { get; set; }

    public TitleBlock(string caption, string title)
    {
        this.caption = caption;
        Text = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private Font CaptionFont() => new(Font.FontFamily, Font.SizeInPoints * 0.85f, FontStyle.Bold);
    private Font TitleFont() => new(Font.FontFamily, Font.SizeInPoints * 1.75f, FontStyle.Bold);
    private Font SubtitleFont() => new(Font.FontFamily, Font.SizeInPoints * 0.95f);

    public override Size GetPreferredSize(Size proposedSize)
    {
        using var cf = CaptionFont();
        using var tf = TitleFont();
        using var sf = SubtitleFont();
        int w = Math.Max(TextRenderer.MeasureText(Spaced(caption), cf).Width, TextRenderer.MeasureText(Text, tf).Width);
        if (ShowSubtitle) w = Math.Max(w, (int)Math.Round(260 * DeviceDpi / 96.0)); // 작은 줄이 너무 일찍 …로 잘리지 않게 최소 폭
        if (MaximumSize.Width > 0) w = Math.Min(w, MaximumSize.Width); // 긴 이름은 …로 줄여 그린다(OnPaint의 EndEllipsis)
        return new Size(w, cf.Height + tf.Height + 2 + (ShowSubtitle ? sf.Height + 2 : 0));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    /// <summary>GDI 글자에는 자간 설정이 없어서, 영문 머리말은 글자 사이에 가는 공백을 넣어 넓게 보이게 한다.</summary>
    private static string Spaced(string s) => string.Join(" ", s.ToCharArray());

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette == null) return;
        using var cf = CaptionFont();
        using var tf = TitleFont();
        var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, Spaced(caption), cf, new Rectangle(0, 0, Width, cf.Height), palette.Dim, flags);
        TextRenderer.DrawText(g, Text, tf, new Rectangle(0, cf.Height + 2, Width, tf.Height), palette.Fore, flags);
        if (ShowSubtitle && subtitle.Length > 0)
        {
            using var sf = SubtitleFont();
            TextRenderer.DrawText(g, subtitle, sf, new Rectangle(0, cf.Height + tf.Height + 4, Width, sf.Height), palette.Dim, flags);
        }
    }
}

/// <summary>
/// 가로 스크롤바가 없는 트리. 폴더 이름은 직접 그리면서 칸 폭에 맞춰 …로 줄이는데(LibraryForm.DrawTreeNode), 윈도우는 원래
/// 글자 길이를 기준으로 가로 스크롤바를 띄워서 긴 폴더 이름 하나 때문에 막대가 생겼다.
/// </summary>
internal sealed class NoHScrollTreeView : TreeView
{
    private const int TVS_NOHSCROLL = 0x8000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= TVS_NOHSCROLL;
            return cp;
        }
    }
}

/// <summary>옆 막대의 "이름 ……… 숫자" 한 줄(등록 폴더 · 책 수 같은). 아래에 가는 구분선을 긋는다.</summary>
internal sealed class StatRow : Control
{
    private LibraryPalette? palette;
    private string value = "0";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Value { get => value; set { this.value = value; Invalidate(); } }

    public StatRow(string label)
    {
        Text = label;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private Font ValueFont() => new(Font.FontFamily, Font.SizeInPoints * 1.5f, FontStyle.Bold);

    public override Size GetPreferredSize(Size proposedSize)
    {
        using var vf = ValueFont();
        return new Size(proposedSize.Width, vf.Height + (int)Math.Round(14 * DeviceDpi / 96.0));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette == null) return;
        using var vf = ValueFont();
        var row = new Rectangle(0, 0, Width, Height - 1);
        TextRenderer.DrawText(g, Text, Font, row, palette.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, value, vf, row, palette.Fore, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        using var pen = new Pen(palette.Line);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>맨 위 머리 막대: 앱 아이콘(둥근 네모 안) · 이름 · 한 줄 소개 · 여는 형식. 오른쪽 버튼들은 이 안에 Dock=Right로 붙는다.</summary>
internal sealed class LibraryHeader : Panel
{
    private LibraryPalette? palette;
    private readonly Bitmap? logo;

    public LibraryHeader()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        try { using var s = typeof(LibraryHeader).Assembly.GetManifestResourceStream("Caffeum.ico"); if (s != null) { using var ic = new Icon(s, 256, 256); logo = ic.ToBitmap(); } else logo = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap(); } catch { }
        Disposed += (_, _) => logo?.Dispose();
    }

    /// <summary>로고 · 이름 · 부제를 그릴지. 끄면(Caffeum 볼트 창) 로고는 왼쪽 옆 막대가 맡고, 여기는 문서 툴바와 아이콘 버튼만 담는다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowBrand { get; set; } = true;

    private bool compact;
    /// <summary>책을 읽는 동안: 로고 · 이름 · 소개만 남기고 여는 형식 안내는 감춘다(오른쪽 버튼은 LibraryForm이 감춤).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Compact { get => compact; set { if (compact == value) return; compact = value; Invalidate(); } }

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = hosted?.BackColor ?? p.Header; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private ToolStrip? hosted;

    /// <summary>책을 읽는 동안 읽기 화면의 아이콘 툴바(메뉴 · 서재 · 수정 · 열기 · 이전/다음 · 책 이름 · 네비게이션 · 보기 설정 · 낭독 · 몰입 · 항상 위 · 설정)를 로고 · 이름 오른쪽에 얹는다.</summary>
    public void HostToolbar(ToolStrip bar)
    {
        hosted = bar;
        compact = true;
        bar.Dock = DockStyle.None;
        bar.AutoSize = false;
        bar.BackColorChanged += SyncHostedColor;
        Controls.Add(bar);
        BackColor = bar.BackColor;
        PerformLayout();
        Invalidate();
    }

    public void UnhostToolbar(ToolStrip bar)
    {
        if (hosted != bar) return;
        bar.BackColorChanged -= SyncHostedColor;
        Controls.Remove(bar);
        hosted = null;
        compact = false;
        if (palette != null) BackColor = palette.Header;
        Invalidate();
    }

    private void SyncHostedColor(object? sender, EventArgs e) { if (hosted != null) BackColor = hosted.BackColor; }

    /// <summary>책을 읽는 동안 로고 · 이름을 누르면(서재로 돌아가기).</summary>
    public event Action? BrandClicked;

    private int BrandRight()
    {
        if (!ShowBrand) return 0;
        using var titleFont = TitleFont();
        using var subFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
        return S(18) + S(44) + S(12)
            + Math.Max(TextRenderer.MeasureText(AppShared.AppDisplayName, titleFont).Width, TextRenderer.MeasureText(AppShared.AppSubtitle, subFont).Width);
    }

    /// <summary>이름(Caffeum)은 필기체로: Windows에 늘 있는 Segoe Script 굵게, 없으면 기본 글꼴 굵게.</summary>
    private Font TitleFont()
    {
        using var probe = new Font("Segoe Script", 10f);
        return probe.Name == "Segoe Script"
            ? new Font("Segoe Script", Font.SizeInPoints * 1.35f, FontStyle.Bold)
            : new Font(Font.FontFamily, Font.SizeInPoints * 1.25f, FontStyle.Bold);
    }

    private bool OnBrand(Point pt) => ShowBrand && BrandClicked != null && pt.X < BrandRight() + S(4);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = OnBrand(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && OnBrand(e.Location)) BrandClicked?.Invoke();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (hosted == null) return;
        int left = BrandRight() + S(12);
        int h = Math.Min(Height - 2, hosted.GetPreferredSize(Size.Empty).Height);
        int rightW = Controls.OfType<Control>().Where(c => c != hosted && c.Visible && c.Dock == DockStyle.Right).Sum(c => c.Width);
        hosted.SetBounds(left, (Height - h) / 2, Math.Max(0, Width - left - rightW - S(8)), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = palette;
        if (p == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (!ShowBrand) { using (var pen0 = new Pen(p.Line)) g.DrawLine(pen0, 0, Height - 1, Width, Height - 1); return; }
        int box = S(44), x = S(18), y = (Height - box) / 2;
        if (logo != null) g.DrawImage(logo, new Rectangle(x, y, box, box));

        int tx = x + box + S(12);
        using var titleFont = TitleFont();
        using var subFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
        int titleH = titleFont.Height, subH = subFont.Height;
        int ty = (Height - titleH - subH) / 2;
        var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, AppShared.AppDisplayName, titleFont, new Point(tx, ty), p.Fore, flags);
        TextRenderer.DrawText(g, AppShared.AppSubtitle, subFont, new Point(tx, ty + titleH), p.Dim, flags);

        using (var pen = new Pen(p.Line)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

internal enum IconKind { Reorder, Sort, View, Select, Gear }

/// <summary>글자 없이 선 그림만 있는 네모난 버튼(작품 화면의 순서 관리 · 정렬 기준 · 표시 방식). Selected면 켜진 것으로 칠한다.</summary>
internal sealed class IconButton : Control
{
    private readonly IconKind kind;
    private readonly ToolTip tip = new();
    private LibraryPalette? palette;
    private bool hover, selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    /// <summary>아이콘 뒤에 붙는 글자(비우면 아이콘만). 폭은 쓰는 쪽이 <see cref="WidthFor"/>로 정한다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string? Label { get; set; }

    /// <summary>높이가 height일 때 글자까지 들어가는 폭.</summary>
    public int WidthFor(int height) => Label == null ? height : height + S(2) + TextRenderer.MeasureText(Label, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(14);

    public IconButton(IconKind kind, string tooltip)
    {
        this.kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Size = new Size(36, 36);
        tip.SetToolTip(this, tooltip);
        AccessibleName = tooltip;
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    private bool keyDownHere;
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Space or Keys.Enter) keyDownHere = true; }
    protected override void OnLostFocus(EventArgs e) { keyDownHere = false; base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode is Keys.Space or Keys.Enter && keyDownHere) { keyDownHere = false; OnClick(EventArgs.Empty); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (selected || (hover && Enabled))
        {
            using var path = LibraryPalette.Rounded(r, S(8));
            using var b = new SolidBrush(selected ? p.AccentSoft : p.CardHover);
            g.FillPath(b, path);
        }
        Color c = !Enabled ? Themes.Blend(p.Dim, p.Main, 0.5f) : selected ? p.Accent : p.Fore;
        using var pen = new Pen(c, Math.Max(1.5f, S(1.6f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = Label == null ? Width / 2f : Height / 2f, cy = Height / 2f, u = S(1f); // 글자가 붙으면 아이콘은 왼쪽 정사각형 안에
        if (Label != null)
            TextRenderer.DrawText(g, Label, Font, new Rectangle(Height + S(2), 0, Math.Max(0, Width - Height - S(2)), Height), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        switch (kind)
        {
            case IconKind.Reorder: // 위아래 화살표 두 개
                g.DrawLine(pen, cx - 4 * u, cy - 6 * u, cx - 4 * u, cy + 6 * u);
                g.DrawLine(pen, cx - 7 * u, cy - 3 * u, cx - 4 * u, cy - 6 * u);
                g.DrawLine(pen, cx - 1 * u, cy - 3 * u, cx - 4 * u, cy - 6 * u);
                g.DrawLine(pen, cx + 4 * u, cy - 6 * u, cx + 4 * u, cy + 6 * u);
                g.DrawLine(pen, cx + 1 * u, cy + 3 * u, cx + 4 * u, cy + 6 * u);
                g.DrawLine(pen, cx + 7 * u, cy + 3 * u, cx + 4 * u, cy + 6 * u);
                break;
            case IconKind.Sort: // 길이가 줄어드는 세 줄
                g.DrawLine(pen, cx - 7 * u, cy - 5 * u, cx + 7 * u, cy - 5 * u);
                g.DrawLine(pen, cx - 7 * u, cy, cx + 3 * u, cy);
                g.DrawLine(pen, cx - 7 * u, cy + 5 * u, cx - 1 * u, cy + 5 * u);
                break;
            case IconKind.Select: // 옅은 회색 둥근 네모 + 마우스 화살표(고르기)
            {
                var box = new RectangleF(cx - 9 * u, cy - 9 * u, 16 * u, 16 * u);
                using (var bp = LibraryPalette.Rounded(box, 2.5f * u))
                {
                    using (var fb = new SolidBrush(Color.FromArgb(46, c))) g.FillPath(fb, bp);
                    using (var bpen = new Pen(Color.FromArgb(110, c), Math.Max(1f, S(1f)))) g.DrawPath(bpen, bp);
                }
                // 화살표: 끝이 네모 안쪽을 가리키고 아래로 길게(흰 테두리 + 진한 속)
                float ax = cx - 5.5f * u, ay = cy - 6f * u;
                var arrow = new PointF[]
                {
                    new(ax, ay), new(ax, ay + 13 * u), new(ax + 3.2f * u, ay + 10 * u), new(ax + 5.4f * u, ay + 15.2f * u),
                    new(ax + 7.6f * u, ay + 14.2f * u), new(ax + 5.4f * u, ay + 9.2f * u), new(ax + 9.6f * u, ay + 9.2f * u),
                };
                using (var wp = new Pen(Color.White, Math.Max(1.6f, S(1.8f))) { LineJoin = LineJoin.Round }) g.DrawPolygon(wp, arrow);
                using (var ab = new SolidBrush(c)) g.FillPolygon(ab, arrow);
                break;
            }
            case IconKind.Gear: // 톱니바퀴
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * 6 * u, cy + (float)Math.Sin(a) * 6 * u, cx + (float)Math.Cos(a) * 9 * u, cy + (float)Math.Sin(a) * 9 * u);
                }
                g.DrawEllipse(pen, cx - 6 * u, cy - 6 * u, 12 * u, 12 * u);
                g.DrawEllipse(pen, cx - 2.5f * u, cy - 2.5f * u, 5 * u, 5 * u);
                break;
            default: // 2x2 칸
                float s = 5 * u, gap = 2 * u;
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        var q = new RectangleF(cx - s - gap / 2 + i * (s + gap), cy - s - gap / 2 + j * (s + gap), s, s);
                        using var qp = LibraryPalette.Rounded(q, S(1.5f));
                        g.DrawPath(pen, qp);
                    }
                break;
        }
        if (Focused && ShowFocusCues) { using var fp = new Pen(p.Accent); using var path = LibraryPalette.Rounded(r, S(8)); g.DrawPath(fp, path); }
    }
}
