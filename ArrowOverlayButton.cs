using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// 본문(txt/epub/만화) 화면 위에 그대로 얹는 좌우 넘김 버튼 — 회색 원 안에 흰 꺾쇠(‹ ›).
/// 컨트롤의 Region을 원 모양으로 잘라내므로, 원 바깥쪽은 이 컨트롤의 일부가 아니다(윈도우 영역
/// 자체가 없음) — 그래서 그 자리는 항상 밑에 있던 그림/글이 그대로 비친다. 본문 폭을 줄이는
/// 여백(Padding) 없이, 사각형 배경도 없이 그 위에 동그란 버튼만 떠 있는 것처럼 보인다.
///
/// 평소엔 숨어 있다가 마우스가 가까이 오면 나타난다 — 언제 보일지는 MainForm(ArrowHoverTick)이 정한다.
/// 버튼 자체는 작아서 터치로 누르기 어렵다 — 그래서 이 컨트롤을 손대는 대신(레이어드 윈도우 등
/// 저수준 기법은 일부 환경에서 자식 컨트롤 핸들 생성 자체를 실패시키는 게 실제로 확인됐다), 밑에
/// 깔린 본문/그림 쪽에서 "화면 반쪽 탭"으로 넘기는 걸 따로 처리한다(MainForm의 mainEditor 탭 처리,
/// ImageCanvas의 OnMouseUp, epub의 EpubPaginateScript).
/// </summary>
public sealed class ArrowOverlayButton : Control
{
    private const int Diameter = 48;
    private static readonly Color CircleColor = Color.FromArgb(88, 88, 88);
    private static readonly Color CircleHoverColor = Color.FromArgb(118, 118, 118);

    /// <summary>클릭(또는 그에 준하는 활성화)됐을 때.</summary>
    public event Action? Activated;

    private readonly bool pointsLeft;
    private bool hover;

    public ArrowOverlayButton(bool pointsLeft)
    {
        this.pointsLeft = pointsLeft;
        Size = new Size(Diameter, Diameter);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(0, 0, Diameter, Diameter);
            Region = new Region(path);
        }

        Click += (_, _) => Activated?.Invoke();
        MouseEnter += (_, _) => { hover = true; Invalidate(); };
        MouseLeave += (_, _) => { hover = false; Invalidate(); };
        // 마우스를 올린 채로 숨겨지면 MouseLeave가 안 와서, 다음에 나타날 때 밝은 채로 남는다.
        VisibleChanged += (_, _) => { if (!Visible) hover = false; };
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // 원이 Region을 꽉 채우니 따로 안 칠한다

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(hover ? CircleHoverColor : CircleColor))
            g.FillRectangle(fill, ClientRectangle); // Region이 이미 원 모양이라 사각형으로 칠해도 원만 남는다

        // 꺾쇠: 원 높이의 40%쯤. 방향에 따라 좌우만 뒤집는다.
        float cx = Width / 2f, cy = Height / 2f;
        float half = Height * 0.2f, depth = half * 0.9f;
        float tipX = pointsLeft ? cx - depth / 2 : cx + depth / 2;
        float tailX = pointsLeft ? cx + depth / 2 : cx - depth / 2;
        using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(tailX, cy - half), new PointF(tipX, cy), new PointF(tailX, cy + half) });
    }
}
