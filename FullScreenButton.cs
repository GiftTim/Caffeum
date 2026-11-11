using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>서재 오른쪽 위의 전체 화면 버튼(켜지면 "나가기" 모양으로 바뀐다).</summary>
internal sealed class FullScreenButton : Control
{
    private LibraryPalette? palette;
    private bool hover, on;
    private readonly ToolTip tip = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => on;
        set { if (on == value) return; on = value; tip.SetToolTip(this, on ? "전체 화면 나가기" : "전체 화면 (고정됨)"); Invalidate(); }
    }

    public FullScreenButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        Cursor = Cursors.Hand;
        tip.SetToolTip(this, "전체 화면 (고정됨)");
        AccessibleName = "전체 화면";
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = palette;
        g.Clear(Parent?.BackColor ?? p?.Main ?? SystemColors.Control);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (hover)
        {
            using var path = LibraryPalette.Rounded(r, DeviceDpi / 96f * 8);
            using var b = new SolidBrush(p.CardHover);
            g.FillPath(b, path);
        }
        using var font = new Font(MainForm.IconFontName(), 13f);
        TextRenderer.DrawText(g, on ? "" : "", font, r, hover ? p.Fore : p.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>글꼴 아이콘 한 개짜리 작은 버튼(마우스를 올리면 둥근 바탕). 소설 메인의 설정(톱니바퀴)에 쓴다.</summary>
internal sealed class GlyphIconButton : Control
{
    private LibraryPalette? palette;
    private bool hover;
    private readonly string glyph;
    private readonly ToolTip tip = new();

    public GlyphIconButton(string glyph, string tooltip)
    {
        this.glyph = glyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Cursor = Cursors.Hand;
        tip.SetToolTip(this, tooltip);
        AccessibleName = tooltip;
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = palette;
        g.Clear(Parent?.BackColor ?? p?.Main ?? SystemColors.Control);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (hover)
        {
            using var path = LibraryPalette.Rounded(r, DeviceDpi / 96f * 8);
            using var b = new SolidBrush(p.CardHover);
            g.FillPath(b, path);
        }
        using var font = new Font(MainForm.IconFontName(), 14f);
        TextRenderer.DrawText(g, glyph, font, r, hover ? p.Fore : p.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
