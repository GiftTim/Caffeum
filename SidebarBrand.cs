using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// Muvel처럼 왼쪽 옆 막대 맨 위에 붙는 로고 줄: [앱 아이콘] Caffeum(필기체) · 부제, 오른쪽 끝에 접기 버튼(‹ / ›).
/// 로고를 누르면 BrandClicked(작품 목록으로), 접기 버튼을 누르면 CollapseClicked.
/// 좁게 접혀 있으면(Collapsed) 아이콘과 펼치기 버튼만 그린다.
/// </summary>
internal sealed class SidebarBrand : Control
{
    private static Bitmap? logo;
    private LibraryPalette? palette;
    private bool collapsed, hoverBrand, hoverToggle;
    private Rectangle toggleRect;

    public event Action? BrandClicked;
    public event Action? CollapseClicked;
    /// <summary>접기 버튼을 보일지(회차 패널처럼 접을 일이 없는 곳에서는 끈다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowToggle { get; set; } = true;

    public SidebarBrand()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        if (logo == null)
            try { using var s = typeof(SidebarBrand).Assembly.GetManifestResourceStream("Caffeum.ico"); if (s != null) { using var ic = new Icon(s, 256, 256); logo = ic.ToBitmap(); } } catch { }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed { get => collapsed; set { collapsed = value; Invalidate(); } }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    /// <summary>필기체 이름 글꼴(Segoe Script가 없으면 기본 글꼴 굵게).</summary>
    internal static Font TitleFont(float pt)
    {
        using var probe = new Font("Segoe Script", 10f);
        return probe.Name == "Segoe Script" ? new Font("Segoe Script", pt, FontStyle.Bold) : new Font(NovelStore.UiFontFamily, pt, FontStyle.Bold);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool t = ShowToggle && toggleRect.Contains(e.Location), b = !t && (!collapsed || !ShowToggle);
        if (t != hoverToggle || b != hoverBrand) { hoverToggle = t; hoverBrand = b; Invalidate(); }
        Cursor = t || b ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverToggle = hoverBrand = false; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (ShowToggle && toggleRect.Contains(e.Location)) CollapseClicked?.Invoke();
        else if (collapsed && ShowToggle) CollapseClicked?.Invoke(); // 접힌 상태에서 로고를 눌러도 펼친다
        else BrandClicked?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int box = S(collapsed ? 44 : 56);
        int x = collapsed ? (Width - box) / 2 : S(16), y = collapsed || ShowToggle ? S(14) : (Height - box) / 2; // 접기 버튼이 있으면 로고는 위에, 버튼은 그 밑에
        if (logo != null) g.DrawImage(logo, new Rectangle(x, y, box, box));

        if (collapsed)
        {
            toggleRect = ShowToggle ? new Rectangle((Width - S(28)) / 2, y + box + S(8), S(28), S(26)) : Rectangle.Empty;
            if (ShowToggle) DrawToggle(g, p, "›");
            return;
        }

        // 접기 버튼: 로고 옆이 아니라 로고 바로 밑(접힌 상태와 같은 자리 감각)
        toggleRect = ShowToggle ? new Rectangle(x - S(2), y + box + S(8), S(28), S(26)) : Rectangle.Empty;
        int tx = x + box + S(10);
        int tw = Math.Max(0, Width - S(8) - tx);
        using var tf = TitleFont(15f);
        using var sf = new Font(NovelStore.UiFontFamily, 8.5f);
        int th = tf.Height, sh = sf.Height;
        int ty = y + box / 2 - (th + sh - S(4)) / 2; // 글자는 로고와 같은 높이 중앙에
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, AppShared.AppDisplayName, tf, new Rectangle(tx, ty, tw, th), hoverBrand ? p.Accent : p.Fore, flags);
        TextRenderer.DrawText(g, AppShared.AppSubtitle, sf, new Rectangle(tx + S(2), ty + th - S(4), tw, sh), p.Dim, flags);
        if (ShowToggle) DrawToggle(g, p, "‹");
    }

    private void DrawToggle(Graphics g, LibraryPalette p, string glyph)
    {
        if (hoverToggle)
        {
            using var hp = LibraryPalette.Rounded(toggleRect, S(6));
            using var hb = new SolidBrush(p.CardHover);
            g.FillPath(hb, hp);
        }
        using var f = new Font(NovelStore.UiFontFamily, 14f);
        TextRenderer.DrawText(g, glyph, f, toggleRect, p.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>옆 막대의 메뉴 한 줄: [아이콘] 글자. 고른 줄은 옅은 바탕. 접혀 있으면 아이콘만(글자는 툴팁으로).</summary>
internal sealed class SidebarNavItem : Control
{
    private readonly string glyph;
    private LibraryPalette? palette;
    private bool hover, selected, collapsed;
    private readonly ToolTip tip = new();

    public SidebarNavItem(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Font = new Font(NovelStore.UiFontFamily, 10f);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed { get => collapsed; set { collapsed = value; tip.SetToolTip(this, value ? Text : null); Invalidate(); } }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(S(10) + 0.5f, 1.5f, Width - S(20) - 1, Height - 3);
        if (selected || hover)
        {
            using var path = LibraryPalette.Rounded(r, S(8));
            using var b = new SolidBrush(selected ? Themes.Blend(p.Side, p.Fore, p.Dark ? 0.12f : 0.07f) : Themes.Blend(p.Side, p.Fore, p.Dark ? 0.07f : 0.04f));
            g.FillPath(b, path);
        }
        using var iconFont = new Font(MainForm.IconFontName(), 11f);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (collapsed)
        {
            TextRenderer.DrawText(g, glyph, iconFont, ClientRectangle, selected ? p.Accent : p.Fore, flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        int ix = S(22);
        TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(ix, 0, S(20), Height), selected ? p.Accent : p.Dim, flags | TextFormatFlags.HorizontalCenter);
        using var tf = selected ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular);
        TextRenderer.DrawText(g, Text, tf, new Rectangle(ix + S(30), 0, Math.Max(0, Width - ix - S(40)), Height), p.Fore, flags | TextFormatFlags.EndEllipsis);
    }
}
