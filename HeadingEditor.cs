using System.Runtime.InteropServices;
using System.Text;

namespace CoffeeViewer;

/// <summary>
/// 위키 본문 편집기(Muvel처럼 제목이 바로 제목으로 보이는 글 상자).
/// 줄 맨 앞에서 "## " · "### "를 치면 그 표시가 사라지고 줄이 항목(큰 제목) · 부항목(작은 제목)으로 바뀐다.
/// 항목 왼쪽 여백에는 "1." · "1-1." 번호가 흐리게 붙는다 — 글자가 아니라 그려 넣은 것이라 지워지거나 겹치지 않고,
/// 항목을 넣고 빼면 저절로 다시 매겨진다. 파일에도 이 번호 그대로 "1. 제목" · "1-1. 제목"으로 저장한다(LoadText · ToText).
/// 제목 줄 끝에서 Enter를 치면 다음 줄은 보통 글, 제목 줄 맨 앞에서 Backspace를 치면 보통 줄로 돌아간다.
/// 어느 줄이 제목인지는 커서를 움직이지 않고(TOM의 ITextRange로) 읽는다 — 한글을 조합하는 도중에 커서를 옮기면 글자가 깨진다.
/// </summary>
internal sealed class HeadingEditor : RichTextBox
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, out IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int WM_PAINT = 0x0F, WM_SETREDRAW = 0x0B, EM_SETRECT = 0xB3, EM_GETSCROLLPOS = 0x4DD, EM_SETSCROLLPOS = 0x4DE, EM_GETOLEINTERFACE = 0x43C;

    private readonly Font normal, h2, h3;
    private bool busy;
    private int columnWidth = 760;
    private dynamic? tom; // ITextDocument
    /// <summary>지금 글의 항목들(깊이, 번호, 이 상자 안 위치, 제목).</summary>
    private List<(int Level, string Num, int Offset, string Title)> heads = new();

    /// <summary>사용자가 글을 고쳤다(불러오기 중의 변화는 빼고).</summary>
    public event Action? ContentChanged;
    /// <summary>항목이 바뀌었다(목차를 다시 그리라고). (깊이, "1. 제목", 위치)</summary>
    public event Action<List<(int Level, string Text, int Offset)>>? HeadingsChanged;

    public HeadingEditor()
    {
        BorderStyle = BorderStyle.None;
        DetectUrls = false;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        AcceptsTab = true;
        WordWrap = true;
        HideSelection = false;
        normal = new Font(NovelStore.UiFontFamily, 11f);
        h2 = new Font(NovelStore.UiFontFamily, 16f, FontStyle.Bold);
        h3 = new Font(NovelStore.UiFontFamily, 13f, FontStyle.Bold);
        Font = normal;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { normal.Dispose(); h2.Dispose(); h3.Dispose(); }
        base.Dispose(disposing);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int LevelOf(Font? f) => f == null ? 0 : LevelOf(f.Size, f.Bold);
    private int LevelOf(float size, bool bold) => size >= h2.Size - 0.1f ? 2 : bold && size >= h3.Size - 0.1f ? 3 : 0;
    private Font FontFor(int level) => level == 2 ? h2 : level == 3 ? h3 : normal;

    // ───────── 커서를 건드리지 않고 서식 읽기(TOM) ─────────

    private dynamic? Tom
    {
        get
        {
            if (tom != null || !IsHandleCreated) return tom;
            try
            {
                SendMessage(Handle, EM_GETOLEINTERFACE, IntPtr.Zero, out IntPtr unk);
                if (unk != IntPtr.Zero)
                {
                    try { tom = Marshal.GetObjectForIUnknown(unk); }
                    finally { Marshal.Release(unk); }
                }
            }
            catch { tom = null; }
            return tom;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (tom != null) { try { Marshal.ReleaseComObject(tom); } catch { } tom = null; }
        base.OnHandleDestroyed(e);
    }

    /// <summary>idx 글자의 제목 깊이(0 = 보통 글). TOM이 없으면 커서를 잠깐 옮겨 읽는다.</summary>
    private int LevelAt(int idx)
    {
        if (idx < 0 || idx >= TextLength) return 0;
        if (Tom is { } doc)
        {
            try
            {
                dynamic font = doc.Range(idx, idx + 1).Font;
                float size = (float)font.Size;
                int bold = (int)font.Bold;
                return LevelOf(size, bold != 0);
            }
            catch { }
        }
        int s = SelectionStart, l = SelectionLength;
        bool was = busy;
        busy = true;
        try { Select(idx, 1); return LevelOf(SelectionFont); }
        finally { Select(s, l); busy = was; }
    }

    /// <summary>항목 목록을 새로 세고 번호를 매긴다. 바뀌었으면 여백을 다시 그리고 HeadingsChanged.</summary>
    private void RecomputeHeads()
    {
        var list = new List<(int, string, int, string)>();
        string text = Text;
        int off = 0, major = 0, minor = 0;
        foreach (var p in text.Split('\n'))
        {
            if (p.Trim().Length > 0)
            {
                int level = LevelAt(off);
                if (level > 0)
                {
                    string num = level == 2 ? $"{++major}." : $"{major}-{++minor}.";
                    if (level == 2) minor = 0;
                    list.Add((level, num, off, p.Trim()));
                }
            }
            off += p.Length + 1;
        }
        bool same = list.Count == heads.Count && list.Zip(heads).All(z => z.First.Equals(z.Second));
        heads = list;
        if (IsHandleCreated) Invalidate(new Rectangle(0, 0, Math.Max(1, SideMargin), ClientSize.Height));
        if (!same) HeadingsChanged?.Invoke(heads.Select(h => (h.Level, $"{h.Num} {h.Title}", h.Offset)).ToList());
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (busy) return;
        RecomputeHeads();
        ContentChanged?.Invoke();
    }

    // ───────── 글 칸 폭(가운데 정렬) · 번호 그리기 ─────────

    /// <summary>글이 놓일 칸의 폭(px). 남는 폭은 좌우 여백이 된다(왼쪽 여백에 항목 번호가 들어갈 만큼은 늘 남긴다).</summary>
    public void SetColumnWidth(int px) { columnWidth = px; ApplyRect(); }

    public int SideMargin => Math.Max(S(64), (ClientSize.Width - columnWidth) / 2);

    private void ApplyRect()
    {
        if (!IsHandleCreated) return;
        int m = SideMargin;
        var r = new RECT { Left = m, Top = 2, Right = Math.Max(m + 10, ClientSize.Width - Math.Min(m, Math.Max(S(16), (ClientSize.Width - columnWidth) / 2))), Bottom = ClientSize.Height };
        SendMessage(Handle, EM_SETRECT, IntPtr.Zero, ref r);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyRect(); RecomputeHeads(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyRect(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && heads.Count > 0) PaintNumbers();
    }

    /// <summary>항목 줄 왼쪽 여백에 "1." · "1-1."을 흐리게 그린다.</summary>
    private void PaintNumbers()
    {
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            var dim = Themes.Blend(BackColor, ForeColor, 0.42f);
            foreach (var (level, num, offset, _) in heads)
            {
                if (offset >= TextLength) continue;
                var pt = GetPositionFromCharIndex(offset);
                if (pt.Y > ClientSize.Height || pt.Y < -S(40)) continue;
                var f = FontFor(level);
                var size = TextRenderer.MeasureText(g, num, f, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, num, f, new Point(pt.X - size.Width - S(10), pt.Y + S(3)), dim, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
        catch { }
    }

    // ───────── 불러오기 · 저장 ─────────

    public void LoadText(string body)
    {
        busy = true;
        bool redrawOff = IsHandleCreated;
        if (redrawOff) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1]; // 파일 끝의 줄바꿈 하나는 빈 줄로 보이지 않게
            var levels = new int[lines.Length];
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                // 파일의 "1. 제목"(항목) · "1-1. 제목"(부항목) — 번호는 떼고 제목 서식으로 보여 준다(번호는 여백에 그린다)
                var (lvl, l) = WikiDoc.HeadingOf(lines[i]);
                levels[i] = lvl;
                if (i > 0) sb.Append('\n');
                sb.Append(l);
                lines[i] = l;
            }
            Text = sb.ToString();
            SelectAll();
            SelectionFont = normal;
            SelectionColor = ForeColor;
            int off = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (levels[i] > 0 && lines[i].Length > 0) { Select(off, lines[i].Length); SelectionFont = FontFor(levels[i]); }
                off += lines[i].Length + 1;
            }
            Select(0, 0);
            SelectionFont = normal;
            ClearUndo();
        }
        finally
        {
            if (redrawOff) SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            busy = false;
        }
        heads = new();
        RecomputeHeads();
        Refresh();
    }

    /// <summary>지금 글을 파일에 적을 평범한 글로 바꾼다(항목 "1. 제목" · 부항목 "1-1. 제목"). 목차(깊이, 번호 붙은 제목, 위치)도 함께.
    /// 커서는 건드리지 않는다.</summary>
    public string ToText(out List<(int Level, string Text, int Offset)> headings)
    {
        RecomputeHeads();
        headings = heads.Select(h => (h.Level, $"{h.Num} {h.Title}", h.Offset)).ToList();
        var byOffset = heads.ToDictionary(h => h.Offset);
        var sb = new StringBuilder();
        int off = 0;
        var parts = Text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append("\r\n");
            if (byOffset.TryGetValue(off, out var h)) sb.Append(h.Num).Append(' ').Append(h.Title);
            else sb.Append(parts[i]);
            off += parts[i].Length + 1;
        }
        if (sb.Length > 0) sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>맨 위로 스크롤하고 커서를 글 맨 앞에.</summary>
    public void ScrollToTop()
    {
        Select(0, 0);
        if (!IsHandleCreated) return;
        var zero = Point.Empty;
        SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref zero);
    }

    // ───────── 입력 ─────────

    private int LineStart(int idx) => idx <= 0 ? 0 : Text.LastIndexOf('\n', idx - 1) + 1;
    private int LineEnd(int idx) { int e = Text.IndexOf('\n', idx); return e < 0 ? TextLength : e; }

    /// <summary>idx가 든 줄 전체를 이 깊이로 바꾼다(0 = 보통 글).</summary>
    private void SetLineLevel(int idx, int level)
    {
        int ls = LineStart(idx), le = LineEnd(idx), caret = SelectionStart;
        busy = true;
        try
        {
            if (le > ls) { Select(ls, le - ls); SelectionFont = FontFor(level); }
            Select(caret, 0);
            SelectionFont = FontFor(level);
        }
        finally { busy = false; }
        RecomputeHeads();
        ContentChanged?.Invoke();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (e.KeyChar == ' ' && SelectionLength == 0)
        {
            int idx = SelectionStart, ls = LineStart(idx);
            string prefix = Text.Substring(ls, idx - ls);
            int level = prefix switch { "###" => 3, "##" => 2, "#" => 2, _ => 0 };
            if (level > 0)
            {
                e.Handled = true;
                busy = true;
                try { Select(ls, idx - ls); SelectedText = ""; }
                finally { busy = false; }
                SetLineLevel(ls, level);
                return;
            }
        }
        base.OnKeyPress(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 서식 없이 붙여 넣기(다른 곳의 글꼴 · 색이 섞여 들어와 제목으로 잘못 보이지 않게)
        if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
        {
            e.SuppressKeyPress = e.Handled = true;
            if (Clipboard.ContainsText())
            {
                int ls = LineStart(SelectionStart);
                var f = ls < TextLength && LineEnd(ls) > ls ? null : SelectionFont;
                SelectionFont = f != null && LevelOf(f) > 0 ? f : normal;
                SelectionColor = ForeColor;
                SelectedText = Clipboard.GetText().Replace("\r\n", "\n");
            }
            return;
        }
        if (e.KeyCode == Keys.Enter && !e.Shift && SelectionLength == 0)
        {
            int idx = SelectionStart;
            if (idx == LineEnd(idx) && LevelOf(SelectionFont) > 0)
            {
                // 제목 줄 끝에서 Enter: 다음 줄은 보통 글로
                e.SuppressKeyPress = e.Handled = true;
                SelectedText = "\n";
                SelectionFont = normal;
                SelectionColor = ForeColor;
                return;
            }
        }
        if (e.KeyCode == Keys.Back && SelectionLength == 0)
        {
            int idx = SelectionStart;
            if (idx == LineStart(idx))
            {
                int le = LineEnd(idx);
                int level = le > idx ? LevelAt(idx) : LevelOf(SelectionFont);
                if (level > 0) { e.SuppressKeyPress = e.Handled = true; SetLineLevel(idx, 0); return; }
            }
        }
        base.OnKeyDown(e);
    }
}
