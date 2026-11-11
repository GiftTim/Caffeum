using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// 글자 크기를 바꿀 때 툴바의 보기 버튼 밑에 잠깐 뜨는 작은 상자(크롬의 확대/축소 표시처럼):
///   [ 12pt (109%)   −   +   초기화 ]
/// 몇 초 뒤 저절로 사라지고, 마우스를 올려 두면 그동안은 남아 있다. 버튼으로 바로 한 단계씩 바꾸거나 기본 크기로 되돌린다.
/// </summary>
internal sealed class ZoomBubble : Control
{
    public event Action<int>? StepRequested;
    public event Action? ResetRequested;

    private readonly PillButton minus = new("−"), plus = new("+"), reset = new("초기화");
    private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 2200 };
    private LibraryPalette? palette;
    private string text = "";

    public ZoomBubble()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Visible = false;
        TabStop = false;
        foreach (var b in new[] { minus, plus, reset })
        {
            b.Compact = true;
            b.Margin = Padding.Empty;
            Controls.Add(b);
        }
        minus.Click += (_, _) => StepRequested?.Invoke(-1);
        plus.Click += (_, _) => StepRequested?.Invoke(1);
        reset.Click += (_, _) => ResetRequested?.Invoke();
        // 마우스가 상자 위에 있으면(버튼을 연달아 누르는 중) 숨기지 않고 기다린다.
        hideTimer.Tick += (_, _) =>
        {
            if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            hideTimer.Stop();
            Visible = false;
        };
        Disposed += (_, _) => hideTimer.Dispose();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        foreach (var b in new[] { minus, plus, reset }) { b.Surface = p.Main; b.SetPalette(p); }
        Invalidate();
    }

    /// <summary>값을 바꿔 보여주고, 숨김 시간을 처음부터 다시 잰다.</summary>
    public void ShowValue(string value)
    {
        text = value;
        LayoutParts();
        Visible = true;
        BringToFront();
        hideTimer.Stop();
        hideTimer.Start();
        Invalidate();
    }

    private void LayoutParts()
    {
        int pad = S(14), gap = S(6);
        using var f = TextFont();
        int textW = Math.Max(TextRenderer.MeasureText("48pt (436%)", f).Width, TextRenderer.MeasureText(text, f).Width); // 숫자가 바뀌어도 상자 폭이 들썩이지 않게
        int h = Math.Max(minus.Height, f.Height) + S(16);
        int x = pad + textW + S(12);
        foreach (var b in new[] { minus, plus })
        {
            b.Width = Math.Max(b.Width, b.Height + S(6));
            b.Location = new Point(x, (h - b.Height) / 2);
            x += b.Width + gap;
        }
        reset.Location = new Point(x + S(4), (h - reset.Height) / 2);
        Size = new Size(reset.Right + pad, h);
        // 둥근 모서리 바깥이 네모난 바탕색으로 보이지 않게, 창 모양 자체를 둥근 상자로 자른다(뒤의 본문이 그대로 비친다).
        using var shape = LibraryPalette.Rounded(new RectangleF(0, 0, Width, Height), S(12));
        Region?.Dispose();
        Region = new Region(shape);
    }

    private Font TextFont() => new(Font.FontFamily, 10f, FontStyle.Bold);

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f), S(12)))
        {
            using (var b = new SolidBrush(p.Main)) g.FillPath(b, path);
            using (var pen = new Pen(Themes.Blend(p.Line, p.Dim, 0.35f))) g.DrawPath(pen, path);
        }
        using var f = TextFont();
        TextRenderer.DrawText(g, text, f, new Rectangle(S(14), 0, minus.Left - S(14), Height), p.Fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}
