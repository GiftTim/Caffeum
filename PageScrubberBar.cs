namespace CoffeeViewer;

/// <summary>
/// 만화(이미지) 보기 하단에 두는 위치 막대. 꿀뷰처럼 클릭하거나 눌러서 끌면(드래그) 그 자리에 해당하는
/// 페이지로 바로 넘어간다. 지금까지 읽은 비율을 막대 채움으로, 마우스를 올린 위치의 페이지 번호는
/// 작은 말풍선으로 미리 보여준다.
/// </summary>
public class PageScrubberBar : Panel
{
    /// <summary>사용자가 막대를 누르거나 끌어서 이 페이지(0-based)로 가고 싶어할 때.</summary>
    public event Action<int>? PageSeek;

    private int pageCount = 1;
    private int currentPage = 0;
    private bool dragging;
    private int hoverX = -1; // 지금 마우스가 가리키는 x좌표(미리보기 표시용) — 막대 밖이면 -1

    public PageScrubberBar()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 28);
        Cursor = Cursors.Hand;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => dragging = false;
        MouseLeave += (_, _) => { hoverX = -1; Invalidate(); };
    }

    /// <summary>전체 페이지 수와 지금 페이지(0-based)를 갱신한다. 페이지를 보여줄 때마다 호출한다.</summary>
    public void SetProgress(int current, int total)
    {
        pageCount = Math.Max(1, total);
        currentPage = Math.Clamp(current, 0, pageCount - 1);
        Invalidate();
    }

    private int PageFromX(int x)
    {
        if (Width <= 0) return 0;
        float ratio = Math.Clamp((float)x / Width, 0f, 1f);
        return Math.Clamp((int)(ratio * pageCount), 0, pageCount - 1);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        PageSeek?.Invoke(PageFromX(e.X));
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        hoverX = e.X;
        if (dragging)
        {
            int page = PageFromX(e.X);
            if (page != currentPage) PageSeek?.Invoke(page);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 트랙(바탕 막대)
        var track = new Rectangle(0, Height / 2 - 3, Width, 6);
        using (var trackBrush = new SolidBrush(Color.FromArgb(75, 75, 75)))
            g.FillRectangle(trackBrush, track);

        // 지금까지 읽은 비율만큼 채우기
        float ratio = pageCount <= 1 ? 0f : (float)currentPage / (pageCount - 1);
        int filledWidth = (int)(Width * ratio);
        using (var fillBrush = new SolidBrush(Color.FromArgb(90, 170, 255)))
            g.FillRectangle(fillBrush, new Rectangle(0, track.Y, filledWidth, track.Height));

        // 지금 위치를 가리키는 손잡이
        int thumbX = (int)(Width * ratio);
        using (var thumbBrush = new SolidBrush(Color.White))
            g.FillEllipse(thumbBrush, thumbX - 5, Height / 2 - 5, 10, 10);

        // 마우스를 올려놓은 위치의 페이지 번호 미리보기(말풍선)
        if (hoverX >= 0)
        {
            string hoverText = $"{PageFromX(hoverX) + 1}";
            var size = g.MeasureString(hoverText, Font);
            float tx = Math.Clamp(hoverX - size.Width / 2, 2, Width - size.Width - 2);
            using var bubbleBrush = new SolidBrush(Color.FromArgb(210, 30, 30, 30));
            g.FillRectangle(bubbleBrush, tx - 4, 0, size.Width + 8, size.Height + 2);
            g.DrawString(hoverText, Font, Brushes.White, tx, 1);
        }
        // "지금 / 전체" 숫자는 그림 위 [현재/전체] 표시와 제목표시줄에서 보여주므로 막대에는 따로 안 그린다.
    }
}
