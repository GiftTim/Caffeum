using System.Runtime.InteropServices;

namespace CoffeeViewer;

/// <summary>
/// 집필 화면에서 상세 정보(머리)와 글 상자를 한 장으로 이어 같이 스크롤시키는 자리.
/// 글 상자(TextBox)는 스스로 스크롤하지 않도록 글 길이만큼 키워 두고, 이 패널이 머리 + 글 상자를 통째로 민다.
/// 자동 스크롤(AutoScroll) 대신 세로 스크롤바 하나를 직접 쓴다 — 그래서 가로 스크롤바는 생길 수가 없다.
/// </summary>
internal sealed class TallScroller : Panel
{
    private Control? header;
    private PageLockTextBox? editor;
    private readonly VScrollBar vbar = new() { Dock = DockStyle.Right, Visible = false };
    private bool busy, pending;
    private int pos; // 지금 스크롤된 거리(px)
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE, EM_LINESCROLL = 0x00B6;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public TallScroller()
    {
        SetStyle(ControlStyles.ResizeRedraw, true);
        vbar.Scroll += (_, _) => { pos = vbar.Value; Place(); };
        Controls.Add(vbar);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void Attach(Control head, PageLockTextBox box)
    {
        header = head;
        editor = box;
        head.Dock = DockStyle.None;
        box.Dock = DockStyle.None;
        box.ScrollBars = ScrollBars.None;
        Controls.Add(head);
        Controls.Add(box);
        box.TallHost = this;
        box.TextChanged += OnChanged;
        box.KeyUp += OnCaretMoved;
        box.MouseUp += OnCaretMoved;
        head.SizeChanged += OnChanged;
        pos = 0;
        Relayout();
    }

    public void Detach()
    {
        if (editor == null) return;
        editor.TextChanged -= OnChanged;
        editor.KeyUp -= OnCaretMoved;
        editor.MouseUp -= OnCaretMoved;
        editor.TallHost = null;
        if (header != null) header.SizeChanged -= OnChanged;
        if (header != null) Controls.Remove(header);
        Controls.Remove(editor);
        vbar.Visible = false;
        editor = null;
        header = null;
    }

    private void OnChanged(object? s, EventArgs e)
    {
        if (busy || pending || !IsHandleCreated) return;
        pending = true;
        BeginInvoke(() => { pending = false; Relayout(); EnsureCaretVisible(); });
    }

    private void OnCaretMoved(object? s, EventArgs e) => EnsureCaretVisible();

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Relayout(); }

    /// <summary>머리 + 글 상자의 전체 높이를 다시 재고(쓸 수 있는 폭에 맞춰), 스크롤바와 두 자식의 자리를 맞춘다.</summary>
    public void Relayout()
    {
        if (busy || header == null || editor == null || !editor.IsHandleCreated || Width <= 0) return;
        busy = true;
        try
        {
            SuspendLayout();
            // 세로 스크롤바가 필요한지는 글 높이에 달렸고 글 높이는 폭에 달렸으니, 두 번 맞춰 본다.
            bool needBar = vbar.Visible;
            int w = 0, total = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                w = Math.Max(10, Width - (needBar ? vbar.Width : 0));
                if (header.Width != w) header.Width = w;
                if (editor.Width != w) editor.Width = w;
                int first = (int)SendMessage(editor.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
                if (first != 0) SendMessage(editor.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-first));
                int fh = (int)Math.Ceiling(editor.Font.Height * editor.ZoomFactor);
                int contentH = fh;
                try { contentH = editor.GetPositionFromCharIndex(editor.TextLength).Y + fh; } catch { }
                int lines = editor.GetLineFromCharIndex(Math.Max(0, editor.TextLength)) + 1;
                contentH = Math.Max(contentH, lines * fh);
                int minH = Math.Max(0, Height - header.Height);
                int h = Math.Max(minH, contentH + S(160));
                if (editor.Height != h) editor.Height = h;
                total = header.Height + h;
                bool want = total > Height;
                if (want == needBar) break;
                needBar = want;
            }
            vbar.Visible = needBar;
            if (needBar)
            {
                int max = Math.Max(0, total - Height);
                pos = Math.Clamp(pos, 0, max);
                vbar.Minimum = 0;
                vbar.LargeChange = Math.Max(1, Height);
                vbar.SmallChange = Math.Max(1, (int)(editor.Font.Height * editor.ZoomFactor) * 3);
                vbar.Maximum = total - 1; // VScrollBar는 Value가 Maximum - LargeChange + 1까지만 간다
                vbar.Value = Math.Min(pos, Math.Max(0, vbar.Maximum - vbar.LargeChange + 1));
                pos = vbar.Value;
            }
            else pos = 0;
            Place();
            ResumeLayout(false);
        }
        finally { busy = false; }
    }

    private void Place()
    {
        if (header == null || editor == null) return;
        header.Location = new Point(0, -pos);
        editor.Location = new Point(0, header.Height - pos);
    }

    public void EnsureCaretVisible()
    {
        if (editor == null || !editor.IsHandleCreated) return;
        Point p;
        try { p = editor.GetPositionFromCharIndex(editor.SelectionStart); } catch { return; }
        int lh = (int)Math.Ceiling(editor.Font.Height * editor.ZoomFactor);
        int y = editor.Top + p.Y; // 이 패널 화면 기준(스크롤이 이미 반영된 값)
        if (y < 0) ScrollTo(pos + y - S(40));
        else if (y + lh > Height) ScrollTo(pos + (y + lh - Height) + S(80));
    }

    private void ScrollTo(int value)
    {
        if (!vbar.Visible) return;
        int max = Math.Max(0, vbar.Maximum - vbar.LargeChange + 1);
        pos = vbar.Value = Math.Clamp(value, 0, max);
        Place();
    }

    /// <summary>글 상자 위에서 돌린 휠을 받아 통째로 민다.</summary>
    public void WheelScroll(int delta)
    {
        int step = (int)((editor?.Font.Height ?? 20) * (editor?.ZoomFactor ?? 1f)) * 3;
        ScrollTo(pos - delta / 120 * step);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        WheelScroll(e.Delta);
    }
}
