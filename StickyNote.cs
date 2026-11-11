using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoffeeViewer;

// 윈도우 스티커 메모를 본뜬 메모 기능.
// 사이드바 [메모] → 파일 트리 자리에 메모 목록(검색 · 카드). 카드를 누르면 메모 한 장이 따로 뜨는 작은 창으로 열린다.
// 메모 창: 위쪽 줄 [+ 새 메모] ··· [··· 메모 목록 · 삭제] [색] [압정 = 항상 위] [×] / 이름(진하게) + 본문 / 아래쪽 줄 [B I U S 글머리 체크] ··· [투명도].
// ×는 창만 숨긴다(목록에 남는다). 완전히 없애는 건 [메모 삭제]로만. 작품마다 따로 저장된다(.caffeum-memo.json).

/// <summary>메모 한 장: 이름 · 본문(RTF) · 색 · 투명도 · 창 위치.</summary>
public sealed class MemoNote
{
    public const int CurrentFormat = 2;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>메모 이름(창 맨 위에 진하게).</summary>
    public string? Title { get; set; }
    public string Rtf { get; set; } = "";
    /// <summary>본문의 글만(목록 미리보기 · 검색용).</summary>
    public string Text { get; set; } = "";
    public int Color { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>항상 위(압정).</summary>
    public bool Pinned { get; set; }
    /// <summary>속한 메모 그룹 이름(null이면 그룹 밖).</summary>
    public string? Group { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    /// <summary>창이 열려 있었는지.</summary>
    public bool Open { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    /// <summary>저장 형식. 0이면 예전 포스트잇 메모라 읽을 때 한 번 바꾼다.</summary>
    public int Format { get; set; }

    public static MemoNote Create(int color) => new() { Color = color, Format = CurrentFormat };
}

/// <summary>스티커 메모의 일곱 가지 색(본문색, 위쪽 줄 색). 마지막 숯색만 어두워서 글자가 흰색이다.</summary>
internal static class StickyColors
{
    public static readonly (Color Body, Color Bar, string Name)[] All =
    {
        (Color.FromArgb(0xFF, 0xF7, 0xD1), Color.FromArgb(0xFF, 0xEB, 0x9A), "노랑"),
        (Color.FromArgb(0xE4, 0xF9, 0xE0), Color.FromArgb(0xC5, 0xEE, 0xBC), "초록"),
        (Color.FromArgb(0xFF, 0xE4, 0xF1), Color.FromArgb(0xFF, 0xC7, 0xE2), "분홍"),
        (Color.FromArgb(0xF2, 0xE6, 0xFF), Color.FromArgb(0xE1, 0xCB, 0xFF), "보라"),
        (Color.FromArgb(0xE2, 0xF1, 0xFF), Color.FromArgb(0xC4, 0xE3, 0xFF), "파랑"),
        (Color.FromArgb(0xF3, 0xF2, 0xF1), Color.FromArgb(0xDF, 0xDE, 0xDD), "회색"),
        (Color.FromArgb(0x69, 0x69, 0x69), Color.FromArgb(0x52, 0x52, 0x52), "숯색"),
    };

    public static (Color Body, Color Bar, string Name) Of(int i) => All[Math.Clamp(i, 0, All.Length - 1)];
    public static bool IsDark(int i) => Math.Clamp(i, 0, All.Length - 1) == All.Length - 1;
    public static Color Text(int i) => IsDark(i) ? Color.White : Color.FromArgb(32, 32, 32);
    public static Color Dim(int i) => IsDark(i) ? Color.FromArgb(205, 205, 205) : Color.FromArgb(110, 110, 110);
}

/// <summary>작품 폴더의 .caffeum-memo.json에 메모들을 담는다(작품마다 따로). 목록과 메모 창들이 같은 목록을 함께 쓴다.</summary>
internal static class MemoStore
{
    private const string FileName = ".caffeum-memo.json";
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Dictionary<string, List<MemoNote>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>메모가 바뀌어 저장됐다(작품 폴더). 목록이 다시 그린다.</summary>
    public static event Action<string>? Changed;

    public static List<MemoNote> Get(string novelDir)
    {
        if (Cache.TryGetValue(novelDir, out var list)) return list;
        list = Load(novelDir);
        Cache[novelDir] = list;
        return list;
    }

    public static void Save(string novelDir)
    {
        if (Cache.TryGetValue(novelDir, out var list)) Write(novelDir, list);
        Changed?.Invoke(novelDir);
    }

    private static List<MemoNote> Load(string novelDir)
    {
        var list = new List<MemoNote>();
        try
        {
            string p = Path.Combine(novelDir, FileName);
            if (File.Exists(p)) list = JsonSerializer.Deserialize<List<MemoNote>>(File.ReadAllText(p, Encoding.UTF8), Opt) ?? new();
        }
        catch { }
        if (list.Any(n => n.Format < MemoNote.CurrentFormat))
        {
            foreach (var n in list.Where(n => n.Format < MemoNote.CurrentFormat)) Upgrade(n);
            list.RemoveAll(n => n.Text.Length == 0 && n.Rtf.Length == 0 && string.IsNullOrWhiteSpace(n.Title)); // 예전 창이 저절로 만들던 빈 메모
            Write(novelDir, list);
        }
        return list;
    }

    // 예전 포스트잇의 색 순서(노랑 분홍 초록 파랑 보라 회색) → 지금 순서
    private static readonly int[] OldColor = { 0, 2, 1, 4, 3, 5 };

    /// <summary>예전 포스트잇 메모를 지금 형식으로. 비밀번호는 없어진다(본문은 원래 그대로 저장돼 있었다).</summary>
    private static void Upgrade(MemoNote n)
    {
        using var rtb = new RichTextBox();
        try { if (!string.IsNullOrEmpty(n.Rtf)) rtb.Rtf = n.Rtf; } catch { }
        n.Text = rtb.Text;
        n.Title = string.IsNullOrWhiteSpace(n.Title) ? null : n.Title.Trim();
        n.Color = n.Color >= 0 && n.Color < OldColor.Length ? OldColor[n.Color] : 0;
        n.Created = n.Modified;
        n.Format = MemoNote.CurrentFormat;
    }

    private static void Write(string novelDir, List<MemoNote> notes) => WriteHidden(Path.Combine(novelDir, FileName), JsonSerializer.Serialize(notes, Opt));

    private static void WriteHidden(string p, string json)
    {
        try
        {
            if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal); // 숨김 파일은 덮어쓰기가 막혀서(첫 저장 뒤로는 저장이 조용히 실패했다) 먼저 풀어 둔다
            File.WriteAllText(p, json, Encoding.UTF8);
            var fi = new FileInfo(p);
            if ((fi.Attributes & FileAttributes.Hidden) == 0) fi.Attributes |= FileAttributes.Hidden;
        }
        catch { }
    }

    // ───────── 메모 그룹(에피소드 그룹처럼: 이름 순서는 .caffeum-memo-groups.json, 메모마다 Group) ─────────

    private const string GroupFile = ".caffeum-memo-groups.json";
    public const string DefaultGroupName = "새 메모 그룹";
    private static readonly Dictionary<string, List<string>> GroupCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<string> Groups(string novelDir)
    {
        if (GroupCache.TryGetValue(novelDir, out var list)) return list;
        list = new();
        try
        {
            string p = Path.Combine(novelDir, GroupFile);
            if (File.Exists(p)) list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p, Encoding.UTF8), Opt) ?? new();
        }
        catch { }
        GroupCache[novelDir] = list;
        return list;
    }

    private static void SaveGroups(string novelDir)
    {
        WriteHidden(Path.Combine(novelDir, GroupFile), JsonSerializer.Serialize(Groups(novelDir), Opt));
        Save(novelDir);
    }

    /// <summary>새 그룹을 만든다(이름이 겹치면 뒤에 숫자). 만든 그룹 이름을 돌려준다.</summary>
    public static string CreateGroup(string novelDir)
    {
        var groups = Groups(novelDir);
        string name = DefaultGroupName;
        for (int i = 2; groups.Contains(name); i++) name = $"{DefaultGroupName} {i}";
        groups.Add(name);
        SaveGroups(novelDir);
        return name;
    }

    /// <summary>그룹 이름을 바꾼다(겹치면 뒤에 숫자). 바뀐 이름을 돌려준다(못 바꿨으면 원래 이름).</summary>
    public static string RenameGroup(string novelDir, string oldName, string newName)
    {
        newName = newName.Trim();
        var groups = Groups(novelDir);
        int at = groups.IndexOf(oldName);
        if (newName.Length == 0 || newName == oldName || at < 0) return oldName;
        string final = newName;
        for (int i = 2; groups.Contains(final); i++) final = $"{newName} {i}";
        groups[at] = final;
        foreach (var n in Get(novelDir).Where(n => n.Group == oldName)) n.Group = final;
        SaveGroups(novelDir);
        return final;
    }

    /// <summary>그룹을 지운다(속해 있던 메모는 지워지지 않고 그룹 밖으로 나온다).</summary>
    public static void DeleteGroup(string novelDir, string name)
    {
        Groups(novelDir).Remove(name);
        foreach (var n in Get(novelDir).Where(n => n.Group == name)) n.Group = null;
        SaveGroups(novelDir);
    }

    /// <summary>메모를 그룹에 넣는다(null이면 그룹에서 뺀다).</summary>
    public static void SetGroup(string novelDir, MemoNote note, string? group)
    {
        note.Group = group;
        Save(novelDir);
    }
}

/// <summary>테두리 없는 메모 창의 공통 처리: 가장자리 끌어 크기 조절 · 빈 곳 끌어 옮기기 · 둥근 모서리 · 화면 안으로 맞추기.</summary>
internal static class StickyChrome
{
    public const string GlyphAdd = "\uE710", GlyphMore = "\uE712", GlyphClose = "\uE8BB", GlyphBullets = "\uE8FD",
        GlyphCheckbox = "\uE739", GlyphCheck = "\uE73E", GlyphSearch = "\uE721", GlyphPalette = "\uE790", GlyphPin = "\uE718", GlyphPinned = "\uE841";

    private const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTTRANSPARENT = -1;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    private static readonly Dictionary<float, Font> IconFonts = new();

    public static Font IconFont(float size)
    {
        if (!IconFonts.TryGetValue(size, out var f)) IconFonts[size] = f = new Font(MainForm.IconFontName(), size);
        return f;
    }

    private static int Edge(Control c) => (int)Math.Round(6 * c.DeviceDpi / 96.0);

    private static Point ClientPoint(Control c, IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        return c.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
    }

    /// <summary>자식 칸에서: 창 가장자리(크기 조절 띠) 위면 창이 받게 넘긴다. 넘겼으면 true.</summary>
    public static bool PassEdge(Control c, ref Message m)
    {
        if (m.Msg != WM_NCHITTEST || c.FindForm() is not StickyNoteWindow f) return false;
        var pt = ClientPoint(f, m.LParam);
        int b = Edge(f);
        if (pt.X >= b && pt.Y >= b && pt.X < f.ClientSize.Width - b && pt.Y < f.ClientSize.Height - b) return false;
        m.Result = (IntPtr)HTTRANSPARENT;
        return true;
    }

    public static bool IsHitTest(in Message m) => m.Msg == WM_NCHITTEST;

    /// <summary>창에서(기본 처리 뒤에): 가장자리 · 모서리를 크기 조절 자리로 알린다.</summary>
    public static void ResizeEdges(Form f, ref Message m)
    {
        if ((int)m.Result != HTCLIENT) return;
        var pt = ClientPoint(f, m.LParam);
        int b = Edge(f);
        bool l = pt.X < b, rt = pt.X >= f.Width - b, t = pt.Y < b, bt = pt.Y >= f.Height - b;
        int hit = t && l ? 13 : t && rt ? 14 : bt && l ? 16 : bt && rt ? 17 : l ? 10 : rt ? 11 : t ? 12 : bt ? 15 : HTCLIENT;
        m.Result = (IntPtr)hit;
    }

    public static void DragMove(Control c)
    {
        if (c.FindForm() is not StickyNoteWindow f) return;
        ReleaseCapture();
        SendMessage(f.Handle, 0x00A1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    /// <summary>윈도우 11이면 창 모서리를 둥글게(10에서는 아무 일도 없다).</summary>
    public static void RoundCorners(Form f)
    {
        try { int v = 2; DwmSetWindowAttribute(f.Handle, 33, ref v, sizeof(int)); } catch { }
    }

    public static Rectangle FitOnScreen(Rectangle r)
    {
        var wa = Screen.FromRectangle(r).WorkingArea;
        int w = Math.Min(r.Width, wa.Width), h = Math.Min(r.Height, wa.Height);
        return new Rectangle(Math.Clamp(r.X, wa.Left, wa.Right - w), Math.Clamp(r.Y, wa.Top, wa.Bottom - h), w, h);
    }

    /// <summary>목록 카드의 날짜: 오늘이면 시각, 올해면 월 · 일, 그 전이면 연도까지.</summary>
    public static string When(DateTime t)
    {
        var ko = CultureInfo.GetCultureInfo("ko-KR");
        var now = DateTime.Now;
        return t.Date == now.Date ? t.ToString("tt h:mm", ko) : t.Year == now.Year ? t.ToString("M월 d일", ko) : t.ToString("yyyy. M. d.", ko);
    }
}

/// <summary>창 가장자리의 크기 조절 띠를 가리지 않는 패널.</summary>
internal sealed class EdgePassPanel : Panel
{
    public EdgePassPanel() { DoubleBuffered = true; }
    protected override void WndProc(ref Message m) { if (!StickyChrome.PassEdge(this, ref m)) base.WndProc(ref m); }
}

/// <summary>버튼 몇 개를 직접 그리는 줄(메모 창의 위 · 아래 줄). 버튼 아닌 곳을 끌면 창이 움직인다.</summary>
internal abstract class StickyStrip : Control
{
    protected readonly record struct StripButton(string Id, Rectangle Rect, string Text, string Tip, FontStyle? Letter = null);

    protected readonly List<StripButton> Buttons = new();
    private readonly ToolTip tip = new();
    private string? hot;
    private bool showButtons = true;

    /// <summary>버튼을 눌렀다(아이디, 버튼 왼쪽 아래의 화면 좌표 — 메뉴를 거기 띄운다).</summary>
    public event Action<string, Point>? ButtonClicked;

    /// <summary>켜져 있는 버튼(진하게 · 기울임 …).</summary>
    public HashSet<string> On { get; } = new();

    /// <summary>창이 비활성일 때는 버튼을 숨긴다(스티커 메모처럼).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowButtons { get => showButtons; set { if (showButtons == value) return; showButtons = value; hot = null; Invalidate(); } }

    protected StickyStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    /// <summary>Buttons를 지금 크기에 맞춰 채운다.</summary>
    protected abstract void Arrange();

    protected bool Dark => BackColor.GetBrightness() < 0.45f;

    private StripButton? HitAt(Point p) => showButtons ? Buttons.FirstOrDefault(b => b.Rect.Contains(p)) is { Id: not null } b ? b : null : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var b = HitAt(e.Location);
        if (b?.Id == hot) return;
        hot = b?.Id;
        Cursor = b != null ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, b?.Tip ?? "");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = null; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (HitAt(e.Location) is { } b) ButtonClicked?.Invoke(b.Id, PointToScreen(new Point(b.Rect.Left, b.Rect.Bottom)));
        else StickyChrome.DragMove(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Buttons.Clear();
        Arrange();
        if (!showButtons) return;
        int shade = Dark ? 255 : 0;
        foreach (var b in Buttons)
        {
            bool on = On.Contains(b.Id);
            if (on || hot == b.Id)
                using (var hb = new SolidBrush(Color.FromArgb(on ? 50 : 28, shade, shade, shade)))
                using (var hp = LibraryPalette.Rounded(b.Rect, S(4))) g.FillPath(hb, hp);
            if (b.Letter is { } style)
            {
                using var f = new Font(NovelStore.UiFontFamily, 10.5f, style);
                TextRenderer.DrawText(g, b.Text, f, b.Rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else TextRenderer.DrawText(g, b.Text, StickyChrome.IconFont(10.5f), b.Rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void WndProc(ref Message m) { if (!StickyChrome.PassEdge(this, ref m)) base.WndProc(ref m); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
}

/// <summary>메모 창 위쪽 줄: [+] ············ [···] [색] [압정] [×].</summary>
internal sealed class StickyNoteHeader : StickyStrip
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned { get; set; }

    protected override void Arrange()
    {
        int btn = S(30), y = (Height - btn) / 2, pad = S(4);
        Buttons.Add(new("add", new Rectangle(pad, y, btn, btn), StickyChrome.GlyphAdd, "새 메모 (Ctrl+N)"));
        int x = Width - pad - btn;
        Buttons.Add(new("close", new Rectangle(x, y, btn, btn), StickyChrome.GlyphClose, "숨기기 (Ctrl+W)")); x -= btn;
        Buttons.Add(new("pin", new Rectangle(x, y, btn, btn), Pinned ? StickyChrome.GlyphPinned : StickyChrome.GlyphPin, Pinned ? "항상 위 끄기" : "항상 위")); x -= btn;
        Buttons.Add(new("palette", new Rectangle(x, y, btn, btn), StickyChrome.GlyphPalette, "색")); x -= btn;
        Buttons.Add(new("more", new Rectangle(x, y, btn, btn), StickyChrome.GlyphMore, "메뉴"));
    }
}

/// <summary>메모 창 아래쪽 줄: 진하게 · 기울임 · 밑줄 · 취소선 · 글머리 기호 · 체크 상자 ······ 투명도(오른쪽 끝, 좌우로 끈다).</summary>
internal sealed class StickyFormatBar : StickyStrip
{
    private Rectangle track;
    private bool dragging;
    private double opacity = 1.0;
    public event Action<double>? OpacityChanged;

    public const double MinOpacity = 0.3;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double OpacityValue { get => opacity; set { opacity = Math.Clamp(value, MinOpacity, 1.0); Invalidate(); } }

    protected override void Arrange()
    {
        int btn = S(30), y = (Height - btn) / 2, x = S(6);
        void Add(string id, string text, string tipText, FontStyle? letter = null) { Buttons.Add(new(id, new Rectangle(x, y, btn, btn), text, tipText, letter)); x += btn + S(1); }
        Add("b", "B", "굵게 (Ctrl+B)", FontStyle.Bold);
        Add("i", "I", "기울임꼴 (Ctrl+I)", FontStyle.Italic);
        Add("u", "U", "밑줄 (Ctrl+U)", FontStyle.Underline);
        Add("s", "T", "취소선 (Ctrl+T)", FontStyle.Strikeout);
        Add("bullet", StickyChrome.GlyphBullets, "글머리 기호 (Ctrl+Shift+L)");
        Add("check", StickyChrome.GlyphCheckbox, "체크 상자 (Ctrl+Shift+C)");
        int tw = Math.Clamp(Width - x - S(28), S(30), S(80));
        track = new Rectangle(Width - S(14) - tw, Height / 2 - S(1), tw, S(3));
    }

    private Rectangle Grab => Rectangle.Inflate(track, S(8), S(12));

    private void SetFromX(int x)
    {
        if (track.Width <= 0) return;
        double t = Math.Clamp((x - track.Left) / (double)track.Width, 0, 1);
        opacity = MinOpacity + t * (1 - MinOpacity);
        Invalidate();
        OpacityChanged?.Invoke(opacity);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && ShowButtons && Grab.Contains(e.Location)) { dragging = true; Capture = true; SetFromX(e.X); return; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) { SetFromX(e.X); return; }
        base.OnMouseMove(e);
        if (ShowButtons && Grab.Contains(e.Location)) Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ShowButtons) return;
        var g = e.Graphics;
        int shade = Dark ? 255 : 0;
        using (var tb = new SolidBrush(Color.FromArgb(90, shade, shade, shade))) using (var tp = LibraryPalette.Rounded(track, S(1))) g.FillPath(tb, tp);
        float kx = track.Left + (float)((opacity - MinOpacity) / (1 - MinOpacity)) * track.Width, kd = S(12);
        using var kb = new SolidBrush(Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(70, 70, 70));
        g.FillEllipse(kb, kx - kd / 2, Height / 2f - kd / 2, kd, kd);
    }
}

/// <summary>··· 메뉴 맨 위의 색 고르기 줄.</summary>
internal sealed class StickyColorRow : Control
{
    private readonly int current;
    private int hot = -1;
    public event Action<int>? Picked;

    public StickyColorRow(int current)
    {
        this.current = current;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int Cell => S(36);
    public Size Preferred => new(Cell * StickyColors.All.Length + S(8), Cell + S(8));
    private Rectangle CellRect(int i) => new(S(4) + i * Cell, S(4), Cell, Cell);
    private int HitAt(Point p) { for (int i = 0; i < StickyColors.All.Length; i++) if (CellRect(i).Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitAt(e.Location); if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && HitAt(e.Location) is int i and >= 0) Picked?.Invoke(i); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        for (int i = 0; i < StickyColors.All.Length; i++)
        {
            var r = CellRect(i);
            var c = StickyColors.All[i];
            using (var b = new SolidBrush(c.Bar)) g.FillRectangle(b, r);
            if (hot == i) using (var p = new Pen(Color.FromArgb(90, 0, 0, 0), S(2))) g.DrawRectangle(p, Rectangle.Inflate(r, -S(1), -S(1)));
            if (i == current) TextRenderer.DrawText(g, StickyChrome.GlyphCheck, StickyChrome.IconFont(11f), r, StickyColors.Text(i), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>메모 한 장의 창.</summary>
internal sealed class StickyNoteWindow : Form
{
    private readonly string dir;
    public MemoNote Note { get; }
    private readonly StickyNoteHeader header = new();
    private readonly StickyFormatBar tools = new();
    private readonly EdgePassPanel host = new();
    private readonly TextBox title;
    private readonly Panel titleGap = new();
    private readonly PageLockTextBox body;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    private bool loading, textDirty, metaDirty, discarded;

    internal int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public StickyNoteWindow(string dir, MemoNote note)
    {
        this.dir = dir;
        Note = note;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        Icon = AppShared.LoadAppIcon();
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(NovelStore.UiFontFamily, 10f);
        MinimumSize = new Size(S(260), S(200));
        ClientSize = new Size(S(320), S(340));
        DoubleBuffered = true;
        Padding = new Padding(1); // 테두리 한 줄

        header.Dock = DockStyle.Top; header.Height = S(36);
        tools.Dock = DockStyle.Bottom; tools.Height = S(40);
        title = new TextBox { BorderStyle = BorderStyle.None, Dock = DockStyle.Top, Font = new Font(NovelStore.UiFontFamily, 13f, FontStyle.Bold), PlaceholderText = "제목을 작성하세요." };
        titleGap.Dock = DockStyle.Top; titleGap.Height = S(6);
        body = new PageLockTextBox { BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = new Font(NovelStore.UiFontFamily, 11.5f), ScrollBars = ScrollBars.Vertical };
        host.Dock = DockStyle.Fill;
        host.Padding = new Padding(S(14), S(6), S(7), S(4));
        // 빈 본문 안내는 본문 상자가 직접 그린다(위에 얹은 Label은 깜빡이는 커서를 가린다)
        body.Placeholder = "메모를 작성하세요.";
        body.PlaceholderAt = new Point(1, 0);
        // 도킹은 뒤에 넣은 것부터(제목 → 틈 → 본문)
        host.Controls.Add(body);
        host.Controls.Add(titleGap);
        host.Controls.Add(title);
        Controls.Add(host);
        Controls.Add(tools);
        Controls.Add(header);

        header.ButtonClicked += (id, pt) =>
        {
            switch (id)
            {
                case "add": StickyNotes.NewNote(dir, this); break;
                case "more": ShowMore(pt); break;
                case "palette": ShowPalette(pt); break;
                case "pin": SetPinned(!Note.Pinned); break;
                case "close": Close(); break;
            }
        };
        tools.ButtonClicked += (id, _) => ApplyFormat(id);
        tools.OpacityChanged += o => { Opacity = o; Note.Opacity = o; MarkMeta(); };
        title.TextChanged += (_, _) => { if (!loading) MarkText(); };
        title.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Down) { e.SuppressKeyPress = true; body.Focus(); body.Select(0, 0); } };        body.TextChanged += (_, _) => { UpdateHint(); if (!loading) MarkText(); };
        body.SelectionChanged += (_, _) => SyncTools();
        body.MouseDown += OnBodyMouseDown;
        saveTimer.Tick += (_, _) => Flush();
        LocationChanged += (_, _) => MarkBounds();
        SizeChanged += (_, _) => MarkBounds();
        Activated += (_, _) => SetActive(true);
        Deactivate += (_, _) => SetActive(false);
        FormClosing += (_, e) =>
        {
            if (discarded) return;
            Flush();
            if (e.CloseReason != CloseReason.UserClosing) return; // 앱이 끝날 때는 열린 채로 기억해 둔다
            Note.Open = false; // ×는 창만 숨긴다(빈 메모여도 목록에 남는다). 없애는 건 [메모 삭제]로만
            MemoStore.Save(dir);
        };
        FormClosed += (_, _) => saveTimer.Dispose();

        loading = true;
        try
        {
            title.Text = note.Title ?? "";
            try { if (!string.IsNullOrEmpty(note.Rtf)) body.Rtf = note.Rtf; } catch { body.Clear(); }
            ApplyColors();
            body.Select(body.TextLength, 0);
            tools.OpacityValue = note.Opacity;
            Opacity = tools.OpacityValue;
            header.Pinned = TopMost = note.Pinned;
        }
        finally { loading = false; }
        UpdateTitle();
        UpdateHint();
        SetActive(false);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StickyChrome.RoundCorners(this); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (StickyChrome.IsHitTest(m)) StickyChrome.ResizeEdges(this, ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Themes.Blend(StickyColors.Of(Note.Color).Bar, Color.Black, 0.18f));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N: StickyNotes.NewNote(dir, this); return true;
            case Keys.Control | Keys.D: Delete(); return true;
            case Keys.Control | Keys.W: Close(); return true;
        }
        if (body.Focused)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.B: ApplyFormat("b"); return true;
                case Keys.Control | Keys.I: ApplyFormat("i"); return true;
                case Keys.Control | Keys.U: ApplyFormat("u"); return true;
                case Keys.Control | Keys.T: ApplyFormat("s"); return true;
                case Keys.Control | Keys.Shift | Keys.L: ApplyFormat("bullet"); return true;
                case Keys.Control | Keys.Shift | Keys.C: ApplyFormat("check"); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>새 메모: 이름 칸부터 쓰게 한다.</summary>
    public void FocusTitle() { title.Focus(); title.SelectAll(); }

    // ───────── 모양 ─────────

    /// <summary>창이 활성일 때만 위 · 아래 줄의 버튼이 보인다.</summary>
    private void SetActive(bool on)
    {
        header.ShowButtons = on;
        tools.ShowButtons = on;
    }

    private void ApplyColors()
    {
        var c = StickyColors.Of(Note.Color);
        Color fore = StickyColors.Text(Note.Color);
        BackColor = c.Body;
        header.BackColor = c.Bar; header.ForeColor = fore;
        tools.BackColor = c.Body; tools.ForeColor = fore;
        host.BackColor = titleGap.BackColor = c.Body;
        title.BackColor = c.Body; title.ForeColor = fore;
        body.PlaceholderColor = StickyColors.Dim(Note.Color); body.Invalidate();
        body.BackColor = c.Body;
        // 저장된 RTF에는 글자색이 박혀 있어서(숯색 ↔ 밝은 색), 글 전체를 지금 색에 맞춘다
        bool was = loading;
        loading = true;
        try
        {
            body.ForeColor = fore;
            int s = body.SelectionStart, l = body.SelectionLength;
            body.SelectAll();
            body.SelectionColor = fore;
            body.Select(s, l);
        }
        finally { loading = was; }
        Invalidate(true);
    }

    private bool bodyEmpty = true;
    /// <summary>본문이 비었다 · 찼다가 바뀌면 다시 그려서 안내 글을 보이거나 지운다.</summary>
    private void UpdateHint()
    {
        bool e = body.TextLength == 0;
        if (e != bodyEmpty) { bodyEmpty = e; body.Invalidate(); }
    }

    /// <summary>작업 표시줄에 보일 이름: 메모 이름(없으면 본문 첫 줄).</summary>
    private void UpdateTitle()
    {
        string first = title.Text.Trim();
        if (first.Length == 0) first = body.Text.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? "";
        Text = first.Length == 0 ? "메모" : first.Length > 40 ? first[..40] + "…" : first;
    }

    private void SetColor(int i)
    {
        Note.Color = i;
        ApplyColors();
        MarkText(); // 글자색이 바뀐 RTF도 저장
        Flush();
    }

    private void SetPinned(bool on)
    {
        Note.Pinned = header.Pinned = TopMost = on;
        header.Invalidate();
        MarkMeta();
    }

    private void ShowPalette(Point screen)
    {
        var m = new ContextMenuStrip { Font = Font, ShowImageMargin = false };
        var row = new StickyColorRow(Note.Color) { BackColor = m.BackColor };
        row.Size = row.Preferred;
        row.Picked += i => { m.Close(); SetColor(i); };
        m.Items.Add(new ToolStripControlHost(row) { AutoSize = false, Size = row.Size, Margin = Padding.Empty, Padding = Padding.Empty });
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(screen);
    }

    private void ShowMore(Point screen)
    {
        var m = new ContextMenuStrip { Font = Font, ShowImageMargin = false };
        m.Items.Add("메모 목록", null, (_, _) => StickyNotes.ShowList(dir));
        m.Items.Add(new ToolStripMenuItem("메모 삭제", null, (_, _) => Delete()) { ShortcutKeyDisplayString = "Ctrl+D" });
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(screen);
    }

    private void Delete() => StickyNotes.Delete(this, dir, Note, ask: true);

    /// <summary>지운 메모의 창: 저장하지 않고 닫는다.</summary>
    public void Discard()
    {
        discarded = true;
        saveTimer.Stop();
        Close();
    }

    // ───────── 서식 ─────────

    private void ApplyFormat(string id)
    {
        switch (id)
        {
            case "b": body.ToggleStyle(FontStyle.Bold); break;
            case "i": body.ToggleStyle(FontStyle.Italic); break;
            case "u": body.ToggleStyle(FontStyle.Underline); break;
            case "s": body.ToggleStyle(FontStyle.Strikeout); break;
            case "bullet": body.SelectionBullet = !body.SelectionBullet; break;
            case "check": ToggleCheck(); break;
        }
        body.Focus();
        MarkText();
        SyncTools();
    }

    private void SyncTools()
    {
        if (loading) return;
        tools.On.Clear();
        if (body.HasStyle(FontStyle.Bold)) tools.On.Add("b");
        if (body.HasStyle(FontStyle.Italic)) tools.On.Add("i");
        if (body.HasStyle(FontStyle.Underline)) tools.On.Add("u");
        if (body.HasStyle(FontStyle.Strikeout)) tools.On.Add("s");
        if (body.SelectionBullet) tools.On.Add("bullet");
        tools.Invalidate();
    }

    /// <summary>고른 줄(없으면 커서 줄) 앞에 ☐를 붙이거나 뗀다.</summary>
    private void ToggleCheck()
    {
        int s = body.SelectionStart, l = body.SelectionLength;
        int first = body.GetLineFromCharIndex(s), last = body.GetLineFromCharIndex(s + Math.Max(0, l));
        for (int line = last; line >= first; line--)
        {
            int idx = body.GetFirstCharIndexFromLine(line);
            if (idx < 0) continue;
            string t = body.Text;
            if (idx < t.Length && (t[idx] == '☐' || t[idx] == '☑'))
            {
                int len = idx + 1 < t.Length && t[idx + 1] == ' ' ? 2 : 1;
                body.Select(idx, len); body.SelectedText = "";
            }
            else { body.Select(idx, 0); body.SelectedText = "☐ "; }
        }
        body.Select(Math.Min(body.TextLength, s + (l == 0 ? 2 : 0)), 0);
    }

    /// <summary>☐ / ☑ 글자를 누르면 서로 바꾼다.</summary>
    private void OnBodyMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = body.GetCharIndexFromPosition(e.Location);
        if (idx < 0 || idx >= body.TextLength) return;
        char c = body.Text[idx];
        if (c != '☐' && c != '☑') return;
        var p = body.GetPositionFromCharIndex(idx);
        if (e.X < p.X - S(2) || e.X > p.X + S(20)) return; // 상자 근처를 눌렀을 때만
        body.Select(idx, 1);
        body.SelectedText = c == '☐' ? "☑" : "☐";
        body.Select(idx + 1, 0);
        MarkText();
    }

    // ───────── 저장 ─────────

    private void MarkText() { textDirty = true; saveTimer.Stop(); saveTimer.Start(); }
    private void MarkMeta() { metaDirty = true; saveTimer.Stop(); saveTimer.Start(); }

    private void MarkBounds()
    {
        if (loading || !IsHandleCreated || WindowState != FormWindowState.Normal) return;
        MarkMeta();
    }

    /// <summary>바뀐 글 · 창 위치 · 투명도를 지금 저장한다.</summary>
    public void Flush()
    {
        saveTimer.Stop();
        if (discarded || !textDirty && !metaDirty) return;
        if (textDirty)
        {
            Note.Title = title.Text.Trim().Length == 0 ? null : title.Text;
            try { Note.Rtf = body.TextLength == 0 ? "" : body.Rtf ?? ""; } catch { }
            Note.Text = body.Text;
            Note.Modified = DateTime.Now;
            UpdateTitle();
        }
        if (WindowState == FormWindowState.Normal) { Note.X = Left; Note.Y = Top; Note.W = Width; Note.H = Height; }
        textDirty = metaDirty = false;
        MemoStore.Save(dir);
    }
}

/// <summary>목록의 카드 한 장: 위쪽 색 띠 · 이름(진하게) · 날짜(마우스를 올리면 ···) · 글 미리보기.</summary>
internal sealed class StickyCard : Control
{
    public MemoNote Note { get; }
    private bool hover, hotMore;
    private Rectangle more;
    public event Action<StickyCard>? Opened;
    public event Action<StickyCard, Point>? MenuRequested;

    // 꾹 누르기: 누른 채 가만히 LongPressMs가 지나면 카드가 들려서(Lifted) 끌어 순서를 바꿀 수 있다. 그 전에 움직이면 취소.
    public const int LongPressMs = 1000;
    private readonly System.Windows.Forms.Timer press = new() { Interval = LongPressMs };
    private Point downAt;
    private bool pressing, lifted;
    /// <summary>꾹 눌러 들었다.</summary>
    public event Action<StickyCard>? LiftStarted;
    /// <summary>든 채 움직인다(화면 좌표).</summary>
    public event Action<StickyCard, Point>? LiftMoved;
    /// <summary>내려놓았다(화면 좌표 — 끌기가 끊겼으면 null).</summary>
    public event Action<StickyCard, Point?>? LiftEnded;

    public StickyCard(MemoNote note)
    {
        Note = note;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        press.Tick += (_, _) =>
        {
            press.Stop();
            if (!pressing) return;
            lifted = true;
            Cursor = Cursors.SizeAll;
            Invalidate();
            LiftStarted?.Invoke(this);
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) press.Dispose();
        base.Dispose(disposing);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = hotMore = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || more.Contains(e.Location)) return;
        downAt = e.Location;
        pressing = true;
        press.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (lifted) { LiftMoved?.Invoke(this, PointToScreen(e.Location)); return; }
        if (pressing)
        {
            var ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - downAt.X) > ds.Width || Math.Abs(e.Y - downAt.Y) > ds.Height) { pressing = false; press.Stop(); } // 들기 전에 움직이면 취소
        }
        bool h = more.Contains(e.Location); if (h != hotMore) { hotMore = h; Invalidate(); }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // 손을 떼서 놓인 경우는 OnMouseUp이 먼저 처리한다 — 한 박자 뒤에도 들려 있으면(Alt+Tab 등으로 끊김) 취소
        if (!Capture && lifted && IsHandleCreated) BeginInvoke(() => EndLift(null));
    }

    private void EndLift(Point? at)
    {
        if (!lifted) return;
        lifted = false;
        Cursor = Cursors.Hand;
        Invalidate();
        LiftEnded?.Invoke(this, at);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        press.Stop();
        bool wasPressing = pressing;
        pressing = false;
        if (lifted) { if (e.Button == MouseButtons.Left) EndLift(PointToScreen(e.Location)); return; } // 들었다 놓은 건 열기가 아니다
        if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(this, PointToScreen(e.Location));
        else if (e.Button == MouseButtons.Left)
        {
            if (more.Contains(e.Location)) MenuRequested?.Invoke(this, PointToScreen(new Point(more.Left, more.Bottom)));
            else if (wasPressing) Opened?.Invoke(this);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = StickyColors.Of(Note.Color);
        Color fore = StickyColors.Text(Note.Color), dim = StickyColors.Dim(Note.Color);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = LibraryPalette.Rounded(rect, S(6)))
        {
            using (var bb = new SolidBrush(hover ? Themes.Blend(c.Body, c.Bar, 0.35f) : c.Body)) g.FillPath(bb, path);
            var clip = g.Clip;
            g.SetClip(path);
            using (var sb = new SolidBrush(c.Bar)) g.FillRectangle(sb, 0, 0, Width, S(4));
            g.Clip = clip;
            using var pen = lifted ? new Pen(Color.FromArgb(StickyColors.IsDark(Note.Color) ? 200 : 140, StickyColors.Text(Note.Color)), S(2)) : new Pen(Color.FromArgb(30, 0, 0, 0)); // 꾹 눌러 든 카드는 테두리를 진하게
            g.DrawPath(pen, path);
        }
        int pad = S(14), btn = S(28);
        more = new Rectangle(Width - S(6) - btn, S(8), btn, btn);
        int right = Width - pad;
        if (hover)
        {
            bool dark = StickyColors.IsDark(Note.Color);
            if (hotMore) using (var hb = new SolidBrush(Color.FromArgb(dark ? 60 : 30, dark ? Color.White : Color.Black))) using (var hp = LibraryPalette.Rounded(more, S(4))) g.FillPath(hb, hp);
            TextRenderer.DrawText(g, StickyChrome.GlyphMore, StickyChrome.IconFont(10.5f), more, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right = more.Left - S(4);
        }
        else
        {
            string when = StickyChrome.When(Note.Modified);
            int ww = TextRenderer.MeasureText(when, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, when, Font, new Rectangle(Width - pad - ww, S(14), ww, S(20)), dim, TextFormatFlags.NoPadding);
            right = Width - pad - ww - S(8);
        }

        // 첫 줄: 이름(진하게). 이름이 없으면 그 자리부터 본문.
        int y = S(12);
        if (!string.IsNullOrWhiteSpace(Note.Title))
        {
            using var bold = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, Note.Title.Trim(), bold, new Rectangle(pad, y, Math.Max(0, right - pad), S(22)), fore, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += S(26);
        }
        else y += S(24);
        string text = Note.Text.Replace("￼", "").Trim();
        var textRect = new Rectangle(pad, y, Width - pad * 2, Height - y - S(10));
        if (text.Length == 0)
        {
            using var it = new Font(Font, FontStyle.Italic);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(Note.Title) ? "빈 메모" : "", it, textRect, dim, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        else TextRenderer.DrawText(g, text, Font, textRect, fore, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
    }
}

/// <summary>메모 목록의 그룹 줄: 접기 화살표 · 그룹 이름(진하게) · 메모 수. 누르면 접고 펴고, 오른쪽 누르면 그룹 메뉴.</summary>
internal sealed class StickyGroupHeader : Control
{
    public string GroupName { get; }
    public int Count { get; }
    public bool Collapsed { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public LibraryPalette? Palette { get; set; }
    private bool hover;
    public event Action<StickyGroupHeader>? Toggled;
    public event Action<StickyGroupHeader, Point>? MenuRequested;

    public StickyGroupHeader(string name, int count, bool collapsed)
    {
        GroupName = name; Count = count; Collapsed = collapsed;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(this, PointToScreen(e.Location));
        else if (e.Button == MouseButtons.Left) Toggled?.Invoke(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Palette;
        g.Clear(p?.Main ?? Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover) using (var hb = new SolidBrush(p.CardHover)) using (var hp = LibraryPalette.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(6))) g.FillPath(hb, hp);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, Collapsed ? "" : "", StickyChrome.IconFont(7.5f), new Rectangle(S(8), 0, S(14), Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
        using var bold = new Font(Font, FontStyle.Bold);
        string count = Count.ToString();
        int cw = TextRenderer.MeasureText(count, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int nw = Math.Min(TextRenderer.MeasureText(GroupName, bold, Size.Empty, TextFormatFlags.NoPadding).Width + 2, Math.Max(0, Width - S(30) - cw - S(16)));
        TextRenderer.DrawText(g, GroupName, bold, new Rectangle(S(28), 0, nw, Height), p.Fore, flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, count, Font, new Rectangle(S(28) + nw + S(8), 0, cw + 2, Height), p.Dim, flags);
    }
}

/// <summary>파일 트리 자리에 뜨는 메모 목록: 검색칸 + [메모 쓰기](새 메모 · 새 메모 그룹) / 그룹 밖 카드들 → 그룹 줄과 그 그룹의 카드들(최근에 고친 순).</summary>
internal sealed class StickyListPanel : Panel
{
    private string? dir;
    private LibraryPalette? palette;
    private readonly Panel top = new();
    private readonly TextBox search;
    private readonly PillButton addButton;
    private readonly Panel cards = new();
    private readonly Label empty;
    private List<string> shownIds = new();
    private readonly List<Control> items = new(); // 위에서부터 놓는 순서(그룹 줄 · 카드)
    private readonly HashSet<string> collapsed = new();
    private ToolStripDropDown? addDropDown;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public StickyListPanel()
    {
        DoubleBuffered = true;
        string ff = NovelStore.UiFontFamily;
        Font = new Font(ff, 10f);

        top.Dock = DockStyle.Top;
        search = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "메모 검색", Font = new Font(ff, 10.5f) };
        addButton = new PillButton("메모 쓰기") { Primary = true, Glyph = StickyChrome.GlyphAdd, Font = new Font(ff, 10f, FontStyle.Bold) };
        addButton.Click += (_, _) => ShowAddMenu();
        top.Controls.Add(search);
        top.Controls.Add(addButton);
        top.Resize += (_, _) => LayoutTop();
        top.Paint += PaintTop;
        top.MouseDown += (_, _) => search.Focus();
        search.GotFocus += (_, _) => top.Invalidate();
        search.LostFocus += (_, _) => top.Invalidate();
        search.TextChanged += (_, _) => Rebuild(force: true);
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && search.TextLength > 0) { search.Clear(); e.SuppressKeyPress = true; } };

        cards.Dock = DockStyle.Fill;
        cards.AutoScroll = true;
        cards.ClientSizeChanged += (_, _) => LayoutCards();
        empty = new Label { AutoSize = false, TextAlign = ContentAlignment.TopCenter, Visible = false };
        cards.Controls.Add(empty);

        Controls.Add(cards);
        Controls.Add(top);

        MemoStore.Changed += OnStoreChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { MemoStore.Changed -= OnStoreChanged; addDropDown?.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        top.Height = S(56);
        cards.AutoScrollMargin = new Size(0, S(12));
        LayoutTop();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (FindForm() is { } f) StickyNotes.HookOwner(f);
    }

    /// <summary>이 작품의 메모를 보인다.</summary>
    public void SetNovel(string? novelDir)
    {
        if (string.Equals(dir, novelDir, StringComparison.OrdinalIgnoreCase)) { Rebuild(force: true); return; }
        dir = novelDir;
        search.Clear();
        Rebuild(force: true);
        if (FindForm() is { } f) StickyNotes.HookOwner(f);
    }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = top.BackColor = cards.BackColor = p.Main;
        search.BackColor = p.Card; search.ForeColor = p.Fore;
        empty.BackColor = p.Main; empty.ForeColor = p.Dim;
        addButton.SetPalette(p);
        top.Invalidate();
        foreach (var c in cards.Controls.OfType<StickyCard>()) c.Invalidate();
        foreach (var h in cards.Controls.OfType<StickyGroupHeader>()) { h.Palette = p; h.Invalidate(); }
    }

    /// <summary>[메모 쓰기]: 새 메모 / 새 메모 그룹 중 하나를 고르는 목록(파일 트리 에피소드의 +와 같은 모양).</summary>
    private void ShowAddMenu()
    {
        if (dir == null) return;
        string d = dir;
        addDropDown?.Dispose();
        Color back = palette?.Card ?? BackColor, fore = palette?.Fore ?? ForeColor;
        var gray = Color.FromArgb(150, 148, 146);
        var panel = new IconMenuPanel(new Font(NovelStore.UiFontFamily, 9.5f), DeviceDpi, back, fore);
        panel.Add("새 메모", PurpleIcons.Document(S(40), gray), () => BeginInvoke(() => StickyNotes.NewNote(d, null)));
        panel.Add("새 메모 그룹", PurpleIcons.Lines(S(40), gray), () => BeginInvoke(() => { collapsed.Remove(MemoStore.CreateGroup(d)); }));
        panel.Size = panel.MeasureAll();
        var dd = addDropDown = new ToolStripDropDown { Padding = Padding.Empty, BackColor = back, AutoSize = true };
        dd.Items.Add(new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size });
        panel.Chosen += () => dd.Close();
        dd.Show(addButton.PointToScreen(new Point(addButton.Width - panel.Width, addButton.Height + S(4)))); // 버튼 바로 밑, 오른쪽 끝을 맞춰서
    }

    private Rectangle SearchBox() => new(S(12), S(10), Math.Max(S(60), addButton.Left - S(8) - S(12)), S(34));

    private void LayoutTop()
    {
        var pref = addButton.GetPreferredSize(Size.Empty);
        addButton.SetBounds(top.Width - S(12) - pref.Width, S(10), pref.Width, S(34));
        var box = SearchBox();
        search.SetBounds(box.Left + S(10), box.Top + (box.Height - search.PreferredHeight) / 2, Math.Max(S(20), box.Width - S(10) - S(30)), search.PreferredHeight);
        top.Invalidate();
    }

    private void PaintTop(object? sender, PaintEventArgs e)
    {
        if (palette == null) return;
        var box = SearchBox();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = LibraryPalette.Rounded(box, S(6)))
        {
            using var b = new SolidBrush(palette.Card); e.Graphics.FillPath(b, path);
            using var pen = new Pen(search.Focused ? palette.Accent : palette.Line); e.Graphics.DrawPath(pen, path);
        }
        var icon = new Rectangle(box.Right - S(30), box.Top, S(26), box.Height);
        TextRenderer.DrawText(e.Graphics, StickyChrome.GlyphSearch, StickyChrome.IconFont(9.5f), icon, palette.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void OnStoreChanged(string changedDir)
    {
        if (!IsDisposed && string.Equals(changedDir, dir, StringComparison.OrdinalIgnoreCase)) Rebuild(force: false);
    }

    /// <summary>
    /// 카드와 그룹 줄을 다시 만든다(그룹 밖 메모 → 그룹마다 [그룹 줄 + 그 그룹 메모]). 놓이는 순서가 그대로면(글만 고쳤으면)
    /// 다시 그리기만 한다 — 고칠 때마다 목록이 깜빡이지 않게. 검색 중에는 맞는 메모가 있는 그룹만 보인다.
    /// </summary>
    private void Rebuild(bool force)
    {
        string q = search.Text.Trim();
        var groups = dir == null ? new List<string>() : MemoStore.Groups(dir);
        // 순서는 저장된 목록 순서(새 메모는 맨 앞에 들어간다 · 꾹 눌러 끌어서 바꾼다)
        var notes = dir == null ? new List<MemoNote>() : MemoStore.Get(dir)
            .Where(n => q.Length == 0 || n.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) || (n.Title ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        string? GroupOf(MemoNote n) => n.Group != null && groups.Contains(n.Group) ? n.Group : null; // 없어진 그룹이면 그룹 밖
        var plan = new List<(string? Group, MemoNote? Note)>();
        foreach (var n in notes.Where(n => GroupOf(n) == null)) plan.Add((null, n));
        foreach (var g in groups)
        {
            var members = notes.Where(n => GroupOf(n) == g).ToList();
            if (q.Length > 0 && members.Count == 0) continue;
            plan.Add((g, null));
            if (!collapsed.Contains(g)) foreach (var n in members) plan.Add((g, n));
        }
        var ids = plan.Select(x => x.Note != null ? $"{x.Group}\n{x.Note.Id}" : $"#{x.Group}\n{notes.Count(n => GroupOf(n) == x.Group)}\n{collapsed.Contains(x.Group!)}").ToList();
        if (!force && ids.SequenceEqual(shownIds))
        {
            foreach (var c in items) c.Invalidate();
            return;
        }
        shownIds = ids;
        cards.SuspendLayout();
        foreach (var c in items) { cards.Controls.Remove(c); c.Dispose(); }
        items.Clear();
        foreach (var (g, n) in plan)
        {
            if (n == null)
            {
                var head = new StickyGroupHeader(g!, notes.Count(x => GroupOf(x) == g), collapsed.Contains(g!)) { Font = Font, Palette = palette };
                head.Toggled += h => { if (!collapsed.Remove(h.GroupName)) collapsed.Add(h.GroupName); BeginInvoke(() => Rebuild(force: true)); };
                head.MenuRequested += ShowGroupMenu;
                items.Add(head);
                continue;
            }
            var card = new StickyCard(n) { Font = Font, Tag = g };
            card.Opened += c => { if (dir != null) StickyNotes.Open(dir, c.Note, null); };
            card.MenuRequested += ShowCardMenu;
            card.LiftStarted += _ => { dropLine.BackColor = palette?.Accent ?? SystemColors.Highlight; };
            card.LiftMoved += (c, pt) => ShowDrop(c, pt);
            card.LiftEnded += (c, pt) => DropCard(c, pt);
            items.Add(card);
        }
        cards.Controls.AddRange(items.ToArray());
        bool none = dir == null || (MemoStore.Get(dir).Count == 0 && groups.Count == 0);
        empty.Text = none ? "아직 메모가 없어요.\n[+ 메모 쓰기]로 시작해 보세요." : "찾는 메모가 없어요.";
        empty.Visible = plan.Count == 0;
        cards.ResumeLayout();
        LayoutCards();
    }

    private void LayoutCards()
    {
        int pad = S(12), gap = S(8), h = S(100), head = S(34), indent = S(12);
        int w = Math.Max(S(60), cards.ClientSize.Width - pad * 2);
        int y = S(2) + cards.AutoScrollPosition.Y;
        foreach (var c in items)
        {
            if (c is StickyGroupHeader) { c.SetBounds(pad, y + S(4), w, head); y += head + S(4) + gap / 2; }
            else if (c.Tag is string) { c.SetBounds(pad + indent, y, Math.Max(S(40), w - indent), h); y += h + gap; } // 그룹 안 메모는 조금 들여서
            else { c.SetBounds(pad, y, w, h); y += h + gap; }
        }
        empty.SetBounds(pad, S(30), w, S(60));
    }

    // ───────── 꾹 눌러 순서 바꾸기 ─────────

    private readonly Panel dropLine = new() { Visible = false }; // 놓일 자리 표시선

    /// <summary>놓을 자리: Anchor 메모 앞(After면 뒤), Anchor가 null이면 Group의 맨 앞. Group은 놓은 뒤 소속(null = 그룹 밖). LineY = 표시선 위치.</summary>
    private sealed record MemoDrop(MemoNote? Anchor, bool After, string? Group, int LineY);

    private MemoDrop? DropAt(StickyCard dragged, Point screen)
    {
        var pt = cards.PointToClient(screen);
        var shown = items.Where(c => c != dragged).ToList();
        if (shown.Count == 0) return null;
        int gap = S(8);
        var hit = shown.FirstOrDefault(c => pt.Y < c.Bottom) ?? shown[^1];
        if (hit is StickyGroupHeader h) return new MemoDrop(null, false, h.GroupName, h.Bottom + gap / 4);
        var card = (StickyCard)hit;
        bool after = pt.Y > card.Top + card.Height / 2;
        return new MemoDrop(card.Note, after, card.Tag as string, after ? card.Bottom + gap / 2 : card.Top - gap / 2);
    }

    private void ShowDrop(StickyCard dragged, Point screen)
    {
        // 칸 끝에 가까우면 그쪽으로 조금씩 굴린다
        var pt = cards.PointToClient(screen);
        int edge = S(28);
        if (pt.Y < edge || pt.Y > cards.ClientSize.Height - edge)
        {
            int step = pt.Y < edge ? -S(16) : S(16);
            cards.AutoScrollPosition = new Point(0, Math.Max(0, -cards.AutoScrollPosition.Y + step));
        }
        if (DropAt(dragged, screen) is not { } d) { dropLine.Visible = false; return; }
        if (dropLine.Parent != cards) cards.Controls.Add(dropLine);
        dropLine.SetBounds(S(12), d.LineY - S(1), Math.Max(S(20), cards.ClientSize.Width - S(24)), S(3));
        dropLine.Visible = true;
        dropLine.BringToFront();
    }

    private void DropCard(StickyCard dragged, Point? screen)
    {
        dropLine.Visible = false;
        if (dir == null || screen is not Point pt || DropAt(dragged, pt) is not { } d) return;
        var list = MemoStore.Get(dir);
        var note = dragged.Note;
        if (d.Anchor == note) return;
        list.Remove(note);
        int at;
        if (d.Anchor != null) { int ai = list.IndexOf(d.Anchor); at = ai < 0 ? list.Count : d.After ? ai + 1 : ai; }
        else { var first = list.FirstOrDefault(n => n.Group == d.Group); at = first != null ? list.IndexOf(first) : 0; } // 그 그룹의 맨 앞
        list.Insert(Math.Clamp(at, 0, list.Count), note);
        note.Group = d.Group; // 다른 그룹 · 그룹 밖으로도 옮겨진다
        BeginInvoke(() => MemoStore.Save(dir)); // 끌던 카드를 그 안에서 바로 없애지 않게 한 박자 늦게 다시 그린다
    }

    private void ShowGroupMenu(StickyGroupHeader head, Point screen)
    {
        if (dir == null) return;
        string d = dir, group = head.GroupName;
        var m = new ContextMenuStrip { Font = Font, ShowImageMargin = false };
        m.Items.Add("이 그룹에 새 메모", null, (_, _) => BeginInvoke(() => { collapsed.Remove(group); StickyNotes.NewNote(d, null, group); }));
        m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(() =>
        {
            using var dlg = new NovelInputDialog("그룹 이름 바꾸기", "그룹 이름", group);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            bool wasCollapsed = collapsed.Remove(group);
            string renamed = MemoStore.RenameGroup(d, group, dlg.FirstValue);
            if (wasCollapsed) { collapsed.Add(renamed); Rebuild(force: true); } // 접혀 있던 그룹은 새 이름으로도 접힌 채
        }));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("그룹 지우기", null, (_, _) => BeginInvoke(() =>
        {
            if (MessageBox.Show(FindForm(), $"'{group}' 그룹을 지울까요?\n그룹 안의 메모는 지워지지 않고 그룹 밖으로 나와요.", "메모 그룹 지우기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            collapsed.Remove(group);
            MemoStore.DeleteGroup(d, group);
        }));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(screen);
    }

    private void ShowCardMenu(StickyCard card, Point screen)
    {
        if (dir == null) return;
        string d = dir;
        var m = new ContextMenuStrip { Font = Font, ShowImageMargin = false };
        m.Items.Add("메모 열기", null, (_, _) => StickyNotes.Open(d, card.Note, null));
        // 그룹으로 옮기기(에피소드처럼): 있는 그룹들 · 새 그룹 만들어 넣기 / 그룹에서 빼기
        string? now = card.Tag as string;
        var moveTo = new ToolStripMenuItem("그룹으로 옮기기");
        foreach (var g in MemoStore.Groups(d))
        {
            string target = g;
            moveTo.DropDownItems.Add(new ToolStripMenuItem(target, null, (_, _) => BeginInvoke(() => MemoStore.SetGroup(d, card.Note, target))) { Enabled = target != now });
        }
        if (moveTo.DropDownItems.Count > 0) moveTo.DropDownItems.Add(new ToolStripSeparator());
        moveTo.DropDownItems.Add("새 그룹 만들어 넣기", null, (_, _) => BeginInvoke(() => { string created = MemoStore.CreateGroup(d); MemoStore.SetGroup(d, card.Note, created); }));
        m.Items.Add(moveTo);
        if (now != null) m.Items.Add("그룹에서 빼기", null, (_, _) => BeginInvoke(() => MemoStore.SetGroup(d, card.Note, null)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("메모 삭제", null, (_, _) => StickyNotes.Delete(FindForm() ?? (IWin32Window)this, d, card.Note, ask: true));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(screen);
    }
}

/// <summary>메모 창을 띄우고 거두는 곳.</summary>
internal static class StickyNotes
{
    private static readonly Dictionary<string, StickyNoteWindow> Windows = new();
    private static readonly HashSet<Form> Hooked = new();

    /// <summary>메모 창의 [메모 목록]: 작업 화면이 파일 트리 자리에 목록을 띄운다(작품 폴더).</summary>
    public static event Action<string>? ListRequested;

    public static void ShowList(string novelDir) => ListRequested?.Invoke(novelDir);

    /// <summary>앱이 끝나기 전에 고치던 메모를 저장하도록 주 창에 건다(한 번만).</summary>
    public static void HookOwner(Form f)
    {
        if (Hooked.Add(f)) f.FormClosing += (_, _) => FlushAll();
    }

    public static StickyNoteWindow Open(string novelDir, MemoNote note, Form? near, bool activate = true)
    {
        if (Windows.TryGetValue(note.Id, out var w) && !w.IsDisposed)
        {
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Show();
            if (activate) w.Activate();
            return w;
        }
        w = new StickyNoteWindow(novelDir, note) { StartPosition = FormStartPosition.Manual };
        Windows[note.Id] = w;
        w.FormClosed += (_, _) => Windows.Remove(note.Id);
        w.Bounds = PlaceFor(w, note, near);
        if (!note.Open) { note.Open = true; MemoStore.Save(novelDir); }
        w.Show();
        if (activate) w.Activate();
        return w;
    }

    /// <summary>저장된 자리가 있으면 거기(화면 밖이면 안으로), 없으면 옆 메모 창 오른쪽(모자라면 왼쪽)에, 그것도 없으면 화면 오른쪽 위에 겹쳐 놓는다.</summary>
    private static Rectangle PlaceFor(StickyNoteWindow w, MemoNote n, Form? near)
    {
        if (n.W > 0 && n.H > 0) return StickyChrome.FitOnScreen(new Rectangle(n.X, n.Y, n.W, n.H));
        var size = w.Size;
        if (near is StickyNoteWindow && near.Visible)
        {
            var wa = Screen.FromControl(near).WorkingArea;
            int x = near.Right + w.S(12);
            if (x + size.Width > wa.Right) x = near.Left - w.S(12) - size.Width;
            return StickyChrome.FitOnScreen(new Rectangle(new Point(x, near.Top), size));
        }
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int step = w.S(28) * (Windows.Count % 8);
        return StickyChrome.FitOnScreen(new Rectangle(new Point(area.Right - size.Width - w.S(40) - step, area.Top + w.S(80) + step), size));
    }

    /// <summary>새 메모: 메모 창에서 만들면 그 메모의 색 · 그룹을 이어받고 그 옆에 뜬다. 이름 칸부터 쓴다.</summary>
    public static void NewNote(string novelDir, Form? near, string? group = null)
    {
        var sw = near as StickyNoteWindow;
        var n = MemoNote.Create(sw?.Note.Color ?? 0);
        n.Group = group ?? sw?.Note.Group;
        MemoStore.Get(novelDir).Insert(0, n);
        Open(novelDir, n, near).FocusTitle();
    }

    public static bool Delete(IWin32Window owner, string novelDir, MemoNote note, bool ask)
    {
        if (ask && MessageBox.Show(owner, "이 메모를 삭제할까요?", "메모 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        if (Windows.TryGetValue(note.Id, out var w) && !w.IsDisposed) w.Discard();
        MemoStore.Get(novelDir).Remove(note);
        MemoStore.Save(novelDir);
        return true;
    }

    public static void FlushAll()
    {
        foreach (var w in Windows.Values.ToList()) if (!w.IsDisposed) w.Flush();
    }
}
