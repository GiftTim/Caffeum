namespace CoffeeViewer;

/// <summary>
/// 아이콘이 앞에 붙는 짧은 목록(새 편 쓰기 밑의 에피소드 / 새 에피소드 그룹). 기본 메뉴 항목은 글자 높이 · 폭 · 마우스를 올린 칸이
/// 마음대로 어긋나서, 상자 안쪽 전체를 직접 그리는 컨트롤 하나로 만든다(ToolStripDropDown 안에 넣어 쓴다).
/// 아이콘은 왼쪽에서 조금 떨어지고, 글자는 줄 높이의 정중앙, 마우스를 올린 회색은 양쪽 끝까지 꽉 찬다.
/// </summary>
internal sealed class IconMenuPanel : Control
{
    private sealed record Row(string Text, Image Icon, Action Click);
    private readonly List<Row> rows = new();
    private readonly Color fore, back;
    private readonly int dpi;
    private int hot = -1;

    /// <summary>항목을 눌렀다(목록을 닫으라는 뜻).</summary>
    public event Action? Chosen;

    public IconMenuPanel(Font font, int dpi, Color back, Color fore)
    {
        Font = font;
        this.dpi = dpi;
        this.back = back;
        this.fore = fore;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    private int S(int v) => (int)Math.Round(v * dpi / 96.0);
    private int RowHeight => S(32);

    public void Add(string text, Image icon, Action click) => rows.Add(new Row(text, icon, click));

    /// <summary>항목 수와 가장 긴 글자에 맞는 크기(왼쪽 여백 + 아이콘 + 간격 + 글자 + 오른쪽 여백).</summary>
    public Size MeasureAll()
    {
        int tw = rows.Max(r => TextRenderer.MeasureText(r.Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        return new Size(S(14) + S(16) + S(14) + tw + S(24), RowHeight * rows.Count);
    }

    private int RowAt(Point p) => p.Y < 0 ? -1 : Math.Min(rows.Count - 1, p.Y / RowHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = RowAt(e.Location);
        if (i != hot) { hot = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int i = RowAt(e.Location);
        if (i < 0) return;
        var click = rows[i].Click;
        Chosen?.Invoke();
        click();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(back);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int h = RowHeight, isz = S(16);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = new Rectangle(0, i * h, Width, h);
            if (i == hot)
            {
                using var hb = new SolidBrush(Themes.Blend(back, fore, 0.10f));
                g.FillRectangle(hb, r);
            }
            g.DrawImage(rows[i].Icon, new Rectangle(S(14), r.Y + (h - isz) / 2, isz, isz));
            int tx = S(14) + isz + S(14);
            TextRenderer.DrawText(g, rows[i].Text, Font, new Rectangle(tx, r.Y, Width - tx, h), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}
