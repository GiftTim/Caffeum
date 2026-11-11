using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// 작업 화면 위쪽의 문서 탭 막대. 탭마다 제목과 [×]가 있고, 가운데 단추(휠 클릭)로도 닫는다.
/// 탭 안의 화면(MainForm)은 이 컨트롤이 아니라 VaultForm이 들고 있다 — 여기서는 이름표만 그린다.
/// </summary>
internal sealed class DocTabStrip : Control
{
    private sealed record Tab(object Key, string Title);

    private readonly List<Tab> tabs = new();
    private int active = -1, hoverTab = -1;
    private bool hoverClose;
    private LibraryPalette palette = LibraryPalette.From(Themes.All[0]);

    public event Action<object>? TabSelected;
    public event Action<object>? TabCloseRequested;

    public DocTabStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 38;
    }

    public int Count => tabs.Count;
    public object? ActiveKey => active >= 0 && active < tabs.Count ? tabs[active].Key : null;
    public IEnumerable<object> Keys => tabs.Select(t => t.Key);

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Header; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void Add(object key, string title)
    {
        tabs.Add(new Tab(key, title));
        Invalidate();
    }

    public bool Contains(object key) => tabs.Any(t => t.Key.Equals(key));

    public void SelectTab(object key)
    {
        int i = tabs.FindIndex(t => t.Key.Equals(key));
        if (i < 0) return;
        active = i;
        Invalidate();
    }

    public void SetTitle(object key, string title)
    {
        int i = tabs.FindIndex(t => t.Key.Equals(key));
        if (i < 0 || tabs[i].Title == title) return;
        tabs[i] = tabs[i] with { Title = title };
        Invalidate();
    }

    /// <summary>탭을 빼고, 그 자리를 이어받을 탭(닫힌 탭의 오른쪽, 없으면 왼쪽)의 키를 돌려준다. 빼려는 탭이 열려 있던 탭이 아니면 null.</summary>
    public object? Remove(object key)
    {
        int i = tabs.FindIndex(t => t.Key.Equals(key));
        if (i < 0) return null;
        bool wasActive = i == active;
        tabs.RemoveAt(i);
        if (i < active) active--;
        else if (wasActive) active = Math.Min(i, tabs.Count - 1);
        Invalidate();
        return wasActive && active >= 0 ? tabs[active].Key : null;
    }

    // ── 배치: 탭 하나가 막대 전체를 쓰고, 탭이 늘 때마다 전체 폭을 똑같이 나눈다(사파리처럼). ──
    private Rectangle TabRect(int i)
    {
        int n = Math.Max(1, tabs.Count);
        int x0 = Width * i / n, x1 = Width * (i + 1) / n;
        return new Rectangle(x0, 0, x1 - x0, Height - 1);
    }

    private Rectangle CloseRect(Rectangle tab)
    {
        int s = S(14);
        return new Rectangle(tab.Right - s - S(8), tab.Top + (tab.Height - s) / 2, s, s);
    }

    private int HitTab(Point p)
    {
        for (int i = 0; i < tabs.Count; i++) if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int t = HitTab(e.Location);
        bool close = t >= 0 && CloseRect(TabRect(t)).Contains(e.Location);
        if (t != hoverTab || close != hoverClose) { hoverTab = t; hoverClose = close; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverTab != -1) { hoverTab = -1; hoverClose = false; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int t = HitTab(e.Location);
        if (t < 0) return;
        if (e.Button == MouseButtons.Middle) { TabCloseRequested?.Invoke(tabs[t].Key); return; }
        if (e.Button != MouseButtons.Left) return;
        if (CloseRect(TabRect(t)).Contains(e.Location)) TabCloseRequested?.Invoke(tabs[t].Key);
        else TabSelected?.Invoke(tabs[t].Key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 테마에 어울리는 납작한 탭: 막대 · 안 연 탭은 머리 막대보다 한 톤 진하게, 열린 탭은 옆 막대 정도로 밝게(튀지 않게 같은 색 계열).
        Color idle = Themes.Blend(palette.Main, palette.Fore, palette.Dark ? 0.12f : 0.115f);
        Color activeFill = Themes.Blend(palette.Main, palette.Fore, palette.Dark ? 0.04f : 0.03f);
        Color hover = Themes.Blend(palette.Main, palette.Fore, palette.Dark ? 0.085f : 0.075f);
        Color sepColor = Themes.Blend(palette.Main, palette.Fore, palette.Dark ? 0.20f : 0.17f);
        Color textActive = palette.Fore, textIdle = palette.Dim;
        g.Clear(idle);
        var tabFont = Font;
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = TabRect(i);
            bool isActive = i == active;
            Color fill = isActive ? activeFill : (i == hoverTab ? hover : idle);
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r.X, r.Y, r.Width, r.Height + 1);
            if (i > 0) using (var pen = new Pen(sepColor)) g.DrawLine(pen, r.Left, r.Top, r.Left, r.Bottom);
            var closeRect = CloseRect(r);
            bool showClose = i == hoverTab; // 닫기(x)는 마우스를 올린 탭에서만
            int pad = S(10) + closeRect.Width + S(6);
            var textRect = new Rectangle(r.X + pad, r.Y, Math.Max(0, r.Width - pad * 2), r.Height);
            TextRenderer.DrawText(g, tabs[i].Title, tabFont, textRect, isActive ? textActive : textIdle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (showClose)
            {
                if (hoverClose)
                    using (var b = new SolidBrush(palette.CardHover))
                        g.FillEllipse(b, closeRect);
                using var pen = new Pen(textActive, Math.Max(1.2f, S(1)));
                int m = S(5);
                g.DrawLine(pen, closeRect.Left + m, closeRect.Top + m, closeRect.Right - m, closeRect.Bottom - m);
                g.DrawLine(pen, closeRect.Right - m, closeRect.Top + m, closeRect.Left + m, closeRect.Bottom - m);
            }
        }
        using (var pen = new Pen(sepColor)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
