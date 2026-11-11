namespace CoffeeViewer;

/// <summary>
/// 책의 끝(처음)에 닿았을 때 화면 가운데 위쪽에 띄우는 반디뷰 스타일 안내 상자:
///   ✔ 마지막 이미지입니다.
///   [ 이전 압축파일 ( [ )   파일 이름 ]
///   [ 다음 압축파일 ( ] )   파일 이름 ]
/// 줄을 누르면 그 파일로 넘어간다([ ] 키와 같다). 넘어갈 곳이 없는 줄은 흐리게 두고 눌리지 않는다.
/// 자리는 MainForm이 잡고(RepositionOverlays), 페이지를 넘기거나 파일을 열거나 Esc를 누르면 MainForm이 치운다.
/// </summary>
public sealed class EdgePrompt : Control
{
    /// <summary>줄을 눌렀다 — false면 이전, true면 다음.</summary>
    public event Action<bool>? RowClicked;

    private sealed class Row
    {
        public string Label = "";
        public string Name = "";
        public bool Enabled;
        public Rectangle Bounds;
    }

    private readonly Row prevRow = new();
    private readonly Row nextRow = new();
    private string message = "";
    private Row? hover;

    private static readonly Color Back = Color.FromArgb(0x2a, 0x2a, 0x2c);
    private static readonly Color RowBack = Color.FromArgb(0x3a, 0x3a, 0x3d);
    private static readonly Color RowHover = Color.FromArgb(0x4a, 0x4a, 0x4f);
    private static readonly Color Border = Color.FromArgb(0x5a, 0xa0, 0xe6);
    private static readonly Color Dim = Color.FromArgb(0x80, 0x80, 0x80);
    private static readonly Color NameColor = Color.FromArgb(0xc8, 0xc8, 0xc8);

    private Font boldFont;

    public EdgePrompt()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Visible = false;
        Font = new Font("맑은 고딕", 11f);
        boldFont = new Font(Font, FontStyle.Bold);
        ForeColor = Color.White;
        Cursor = Cursors.Default;
    }

    /// <summary>내용을 채우고 크기를 맞춘다(보이게 하는 건 MainForm).</summary>
    public void SetContent(string text, string prevLabel, string? prevName, bool prevEnabled, string nextLabel, string? nextName, bool nextEnabled)
    {
        message = text;
        prevRow.Label = prevLabel; prevRow.Name = prevName ?? ""; prevRow.Enabled = prevEnabled;
        nextRow.Label = nextLabel; nextRow.Name = nextName ?? ""; nextRow.Enabled = nextEnabled;
        hover = null;
        LayoutRows();
        Invalidate();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>창 폭에 맞춰 상자 크기와 두 줄의 자리를 정한다(가로는 창의 70%, 최대 720px).</summary>
    public void FitTo(int availableWidth)
    {
        Width = Math.Clamp((int)(availableWidth * 0.7), S(360), S(720));
        LayoutRows();
    }

    private void LayoutRows()
    {
        int pad = S(16), rowH = S(46), gap = S(10), head = S(44);
        prevRow.Bounds = new Rectangle(pad, head + S(8), Width - pad * 2, rowH);
        nextRow.Bounds = new Rectangle(pad, prevRow.Bounds.Bottom + gap, Width - pad * 2, rowH);
        Height = nextRow.Bounds.Bottom + pad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Back);
        using (var pen = new Pen(Border, Math.Max(1, S(2))))
            g.DrawRectangle(pen, pen.Width / 2, pen.Width / 2, Width - pen.Width, Height - pen.Width);

        // 머리 줄: 체크 표시 + 안내 문장
        int pad = S(16);
        int iconR = S(8);
        int headMid = S(26);
        using (var b = new SolidBrush(Border)) g.FillEllipse(b, pad, headMid - iconR, iconR * 2, iconR * 2);
        using (var pen = new Pen(Color.White, Math.Max(1.5f, S(2))))
            g.DrawLines(pen, new[] { new Point(pad + S(4), headMid), new Point(pad + S(7), headMid + S(3)), new Point(pad + S(12), headMid - S(3)) });
        TextRenderer.DrawText(g, message, Font, new Rectangle(pad + iconR * 2 + S(10), headMid - S(14), Width - pad * 3, S(28)), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        DrawRow(g, prevRow);
        DrawRow(g, nextRow);
    }

    private void DrawRow(Graphics g, Row row)
    {
        using (var b = new SolidBrush(row == hover && row.Enabled ? RowHover : RowBack))
            g.FillRectangle(b, row.Bounds);
        var fore = row.Enabled ? ForeColor : Dim;
        var labelRect = new Rectangle(row.Bounds.X + S(16), row.Bounds.Y, row.Bounds.Width - S(32), row.Bounds.Height);
        var labelSize = TextRenderer.MeasureText(g, row.Label, boldFont, labelRect.Size, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, row.Label, boldFont, labelRect, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        if (row.Name.Length == 0) return;
        var nameRect = new Rectangle(labelRect.X + labelSize.Width + S(16), row.Bounds.Y, labelRect.Right - (labelRect.X + labelSize.Width + S(16)), row.Bounds.Height);
        TextRenderer.DrawText(g, row.Name, Font, nameRect, row.Enabled ? NameColor : Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private Row? RowAt(Point p) => prevRow.Bounds.Contains(p) ? prevRow : nextRow.Bounds.Contains(p) ? nextRow : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var r = RowAt(e.Location);
        if (r != hover) { hover = r; Invalidate(); }
        Cursor = r is { Enabled: true } ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != null) { hover = null; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var r = RowAt(e.Location);
        if (r is { Enabled: true }) RowClicked?.Invoke(r == nextRow);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutRows();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) boldFont.Dispose();
        base.Dispose(disposing);
    }
}
