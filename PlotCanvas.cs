using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoffeeViewer;

// 플롯 캔버스(Muvel의 캔버스): 에피소드 · 위키 · 메모를 공간에 놓고 선으로 이어 작품의 흐름과 관계를 정리한다.
// 파일 트리의 에피소드 그룹 줄 오른쪽 [캔버스] 아이콘 → 그 그룹의 캔버스가 문서 칸에 열린다(그룹마다 하나, 작품폴더\canvas\그룹이름.canvas).
// 저장 형식은 JSON Canvas(옵시디언 .canvas와 같은 구조): 에피소드 · 위키 노드는 "file"(작품 폴더 기준 경로), 메모 노드는 "text".
// 에피소드 · 위키 노드는 원본을 복사하지 않고 가리키기만 한다 — 두 번 누르면 옆 칸에 원본이 열린다.

/// <summary>JSON Canvas 노드. type: text(메모) · file(에피소드 · 위키) · link · group.</summary>
public sealed class CanvasNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = NewId();
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; } = 240;
    [JsonPropertyName("height")] public int Height { get; set; } = 120;
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..16];
    [JsonIgnore] public RectangleF Bounds => new(X, Y, Width, Height);
    public CanvasNode Clone() => (CanvasNode)MemberwiseClone();
}

/// <summary>JSON Canvas 연결선. side: top · right · bottom · left(없으면 가까운 쪽을 고른다).</summary>
public sealed class CanvasEdge
{
    [JsonPropertyName("id")] public string Id { get; set; } = CanvasNode.NewId();
    [JsonPropertyName("fromNode")] public string FromNode { get; set; } = "";
    [JsonPropertyName("fromSide")] public string? FromSide { get; set; }
    [JsonPropertyName("toNode")] public string ToNode { get; set; } = "";
    [JsonPropertyName("toSide")] public string? ToSide { get; set; }
    [JsonPropertyName("fromEnd")] public string? FromEnd { get; set; }
    [JsonPropertyName("toEnd")] public string? ToEnd { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    public CanvasEdge Clone() => (CanvasEdge)MemberwiseClone();
}

public sealed class CanvasDoc
{
    [JsonPropertyName("nodes")] public List<CanvasNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<CanvasEdge> Edges { get; set; } = new();
}

/// <summary>캔버스 파일 읽기 · 쓰기와 캔버스 설정(정렬 가이드 스냅 · 클릭으로 드래그).</summary>
internal static class CanvasStore
{
    public const string Folder = "canvas";
    public const string Glyph = "";

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsCanvas(string path) => path.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase);

    /// <summary>에피소드 그룹의 캔버스 파일 경로(파일 이름에 못 쓰는 글자는 _).</summary>
    public static string PathFor(string novelDir, string group)
    {
        var bad = Path.GetInvalidFileNameChars();
        string safe = new(group.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(novelDir, Folder, safe.Trim() + ".canvas");
    }

    public static CanvasDoc Load(string path)
    {
        try { if (File.Exists(path)) return Parse(File.ReadAllText(path, Encoding.UTF8)) ?? new(); }
        catch { }
        return new();
    }

    public static CanvasDoc? Parse(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<CanvasDoc>(json, Opt);
            if (d == null) return null;
            d.Nodes ??= new(); d.Edges ??= new();
            d.Nodes.RemoveAll(n => n == null);
            d.Edges.RemoveAll(e => e == null || !d.Nodes.Any(n => n.Id == e.FromNode) || !d.Nodes.Any(n => n.Id == e.ToNode));
            return d;
        }
        catch { return null; }
    }

    public static string ToJson(CanvasDoc doc) => JsonSerializer.Serialize(doc, Opt);

    public static void Save(string path, CanvasDoc doc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(doc), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    /// <summary>그룹 이름이 바뀌면 캔버스 파일도 따라간다.</summary>
    public static void Rename(string novelDir, string oldGroup, string newGroup)
    {
        string a = PathFor(novelDir, oldGroup), b = PathFor(novelDir, newGroup);
        try { if (File.Exists(a) && !File.Exists(b)) File.Move(a, b); } catch { }
    }

    /// <summary>작품 폴더 기준 경로(슬래시) ↔ 전체 경로.</summary>
    public static string ToRel(string novelDir, string full) => Path.GetRelativePath(novelDir, full).Replace('\\', '/');
    public static string ToFull(string novelDir, string rel) => Path.GetFullPath(Path.Combine(novelDir, rel.Replace('/', '\\')));

    public static bool IsWikiRel(string rel) => rel.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase);

    // ── 설정 ──
    private sealed class Prefs { public bool Snap { get; set; } = true; public bool ClickDrag { get; set; } }
    private static Prefs? prefs;
    private static string PrefsPath => Path.Combine(AppSettings.DataDir, "canvas.json");
    private static Prefs P
    {
        get
        {
            if (prefs != null) return prefs;
            try { if (File.Exists(PrefsPath)) prefs = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsPath)); } catch { }
            return prefs ??= new Prefs();
        }
    }
    private static void SavePrefs() { try { Directory.CreateDirectory(AppSettings.DataDir); File.WriteAllText(PrefsPath, JsonSerializer.Serialize(P)); } catch { } }

    /// <summary>정렬 가이드 스냅: 노드를 옮길 때 다른 노드의 가장자리 · 가운데에 맞춤선을 보이고 달라붙는다(기본 켬).</summary>
    public static bool Snap { get => P.Snap; set { P.Snap = value; SavePrefs(); } }
    /// <summary>클릭으로 드래그: 빈 곳을 끌면 영역 선택 대신 화면을 옮긴다(기본 끔).</summary>
    public static bool ClickDrag { get => P.ClickDrag; set { P.ClickDrag = value; SavePrefs(); } }
}

/// <summary>캔버스 한 장을 그리고 다루는 판: 노드 · 연결선 · 화면 이동 · 확대 · 선택 · 복사 · 붙여넣기 · 되돌리기.</summary>
internal sealed class PlotCanvasView : Control
{
    private readonly string path, novelDir;
    private CanvasDoc doc;
    private LibraryPalette? palette;

    private float zoom = 1f;
    private PointF pan = new(40, 40);
    private readonly HashSet<CanvasNode> selNodes = new();
    private readonly HashSet<CanvasEdge> selEdges = new();
    private CanvasNode? hoverNode;

    private enum Mode { None, Pan, Select, Move, Connect, Reconnect, Resize }
    private Mode mode;
    private Point downScreen;
    private PointF downWorld, curWorld;
    private bool moved;
    private PointF panStart;
    private readonly Dictionary<CanvasNode, Point> moveOrigins = new();
    private readonly List<(PointF A, PointF B)> guides = new();
    private CanvasNode? connectFrom; private string? connectSide;
    private CanvasEdge? reconnectEdge; private bool reconnectFromEnd;
    private CanvasNode? resizeNode; private Size resizeStart;
    private bool selectAdd;

    private readonly Stack<string> undo = new(), redo = new();
    private string? pendingUndo; // 끌기 시작 때 찍어 둔 상태(실제로 바뀌었을 때만 쌓는다)
    private static List<CanvasNode>? clipNodes;
    private static List<CanvasEdge>? clipEdges;

    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    private bool dirty;
    private TextBox? editor;
    private CanvasNode? editing;
    private readonly Dictionary<string, (DateTime Stamp, string Title, string Sub, string Preview, string Glyph, bool Missing)> info = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>에피소드 · 위키 노드를 두 번 눌렀다(전체 경로).</summary>
    public event Action<string>? OpenRequested;
    /// <summary>확대 비율이 바뀌었다(툴바 표시).</summary>
    public event Action? ZoomChanged;

    public PlotCanvasView(string path, string novelDir)
    {
        this.path = path;
        this.novelDir = novelDir;
        doc = CanvasStore.Load(path);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        saveTimer.Tick += (_, _) => Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Flush(); saveTimer.Dispose(); }
        base.Dispose(disposing);
    }

    public float Zoom => zoom;
    public CanvasDoc Doc => doc;

    public void ApplyTheme(LibraryPalette p) { palette = p; BackColor = p.Main; Invalidate(); }

    private float Dpi => DeviceDpi / 96f;
    private float ZoomPx => zoom * Dpi; // 세계 1칸이 화면 몇 픽셀인지
    private PointF W2S(PointF w) => new(w.X * ZoomPx + pan.X, w.Y * ZoomPx + pan.Y);
    private PointF S2W(PointF s) => new((s.X - pan.X) / ZoomPx, (s.Y - pan.Y) / ZoomPx);
    private RectangleF W2S(RectangleF r) { var a = W2S(r.Location); return new RectangleF(a.X, a.Y, r.Width * ZoomPx, r.Height * ZoomPx); }
    private int S(int v) => (int)Math.Round(v * Dpi);

    // ───────── 저장 · 되돌리기 ─────────

    private void Changed()
    {
        dirty = true;
        saveTimer.Stop(); saveTimer.Start();
        Invalidate();
    }

    public void Flush()
    {
        saveTimer.Stop();
        CommitEditor();
        if (!dirty) return;
        dirty = false;
        CanvasStore.Save(path, doc);
    }

    private string Snapshot() => CanvasStore.ToJson(doc);
    private void PushUndo(string? state = null) { undo.Push(state ?? Snapshot()); redo.Clear(); if (undo.Count > 100) TrimUndo(); }
    private void TrimUndo() { var keep = undo.Take(100).Reverse().ToList(); undo.Clear(); foreach (var s in keep) undo.Push(s); }

    private void Restore(string json)
    {
        if (CanvasStore.Parse(json) is not { } d) return;
        doc = d;
        selNodes.Clear(); selEdges.Clear(); hoverNode = null;
        Changed();
    }

    public void Undo() { if (undo.Count == 0) return; redo.Push(Snapshot()); Restore(undo.Pop()); }
    public void Redo() { if (redo.Count == 0) return; undo.Push(Snapshot()); Restore(redo.Pop()); }

    // ───────── 노드 정보(에피소드 · 위키 원본에서 제목 · 미리보기) ─────────

    private (string Title, string Sub, string Preview, string Glyph, bool Missing) Info(CanvasNode n)
    {
        string rel = n.File ?? "";
        string full;
        try { full = CanvasStore.ToFull(novelDir, rel); } catch { return (rel, "", "", "", true); }
        DateTime stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue;
        if (info.TryGetValue(rel, out var c) && c.Stamp == stamp) return (c.Title, c.Sub, c.Preview, c.Glyph, c.Missing);
        string title = Path.GetFileNameWithoutExtension(rel), sub, preview = "", glyph;
        bool missing = stamp == DateTime.MinValue;
        if (CanvasStore.IsWikiRel(rel))
        {
            var cat = WikiCategory.Find("none");
            if (!missing) try { cat = WikiCategory.Find(WikiStore.Load(full).Category); } catch { }
            sub = "위키 · " + cat.Label;
            glyph = cat.Glyph;
        }
        else
        {
            sub = "에피소드";
            glyph = "";
            if (!missing)
                try
                {
                    using var r = new StreamReader(full, Encoding.UTF8, true);
                    var buf = new char[400];
                    int got = r.Read(buf, 0, buf.Length);
                    preview = string.Join(" ", new string(buf, 0, got).Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
                catch { }
        }
        if (missing) { sub = "찾을 수 없는 문서"; preview = rel; }
        info[rel] = (stamp, title, sub, preview, glyph, missing);
        return (title, sub, preview, glyph, missing);
    }

    // ───────── 그리기 ─────────

    private static readonly Dictionary<string, Color> Preset = new()
    {
        ["1"] = Color.FromArgb(0xFB, 0x46, 0x4C), ["2"] = Color.FromArgb(0xE9, 0x97, 0x3F), ["3"] = Color.FromArgb(0xE0, 0xDE, 0x71),
        ["4"] = Color.FromArgb(0x44, 0xCF, 0x6E), ["5"] = Color.FromArgb(0x53, 0xDF, 0xDD), ["6"] = Color.FromArgb(0xA8, 0x82, 0xFF),
    };
    public static readonly (string Key, string Name)[] ColorNames = { ("", "기본"), ("1", "빨강"), ("2", "주황"), ("3", "노랑"), ("4", "초록"), ("5", "청록"), ("6", "보라") };

    private static Color? ParseColor(string? c)
    {
        if (string.IsNullOrEmpty(c)) return null;
        if (Preset.TryGetValue(c, out var p)) return p;
        try { if (c.StartsWith('#') && c.Length == 7) return Color.FromArgb(Convert.ToInt32(c[1..3], 16), Convert.ToInt32(c[3..5], 16), Convert.ToInt32(c[5..7], 16)); } catch { }
        return null;
    }

    private LibraryPalette Pal => palette ?? LibraryPalette.From(Themes.All[0]);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Pal;
        g.Clear(p.Main);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(g, p);
        Render(g, ZoomPx, pan, export: false);
        // 맞춤선 · 선택 상자 · 잇는 중인 선
        if (mode == Mode.Move && guides.Count > 0)
            using (var gp = new Pen(p.Accent, 1f) { DashStyle = DashStyle.Dash })
                foreach (var (a, b) in guides) g.DrawLine(gp, W2S(a), W2S(b));
        if (mode == Mode.Select && moved)
        {
            var r = Norm(W2S(downWorld), W2S(curWorld));
            using var fb = new SolidBrush(Color.FromArgb(40, p.Accent));
            using var pen = new Pen(p.Accent, 1f);
            g.FillRectangle(fb, r);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
        if (mode == Mode.Connect && connectFrom != null && moved)
        {
            var target = NodeAt(curWorld, skipGroups: true);
            var a = SidePoint(connectFrom, connectSide!);
            var b = target != null && target != connectFrom ? SidePoint(target, NearestSide(target, curWorld)) : curWorld;
            using var pen = new Pen(p.Accent, Math.Max(1.5f, 2 * Dpi)) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, W2S(a), W2S(b));
            if (target != null && target != connectFrom) using (var hp = new Pen(p.Accent, 2 * Dpi)) using (var rp = LibraryPalette.Rounded(W2S(target.Bounds), 10 * ZoomPx)) g.DrawPath(hp, rp);
        }
    }

    private void DrawGrid(Graphics g, LibraryPalette p)
    {
        float step = 24 * ZoomPx;
        if (step < 10) return;
        using var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.14f : 0.12f));
        float sx = pan.X % step, sy = pan.Y % step, d = Math.Max(1.5f, 1.6f * Dpi);
        for (float x = sx; x < Width; x += step)
            for (float y = sy; y < Height; y += step) g.FillEllipse(b, x - d / 2, y - d / 2, d, d);
    }

    private static RectangleF Norm(PointF a, PointF b) => RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>캔버스 전체를 그린다(화면 · 그림 내보내기 공용). scale = 세계 1칸이 몇 픽셀인지.</summary>
    private void Render(Graphics g, float scale, PointF origin, bool export)
    {
        var p = Pal;
        PointF T(PointF w) => new(w.X * scale + origin.X, w.Y * scale + origin.Y);
        RectangleF TR(RectangleF r) { var a = T(r.Location); return new RectangleF(a.X, a.Y, r.Width * scale, r.Height * scale); }
        float fs = scale * 96f / g.DpiY; // 글꼴(pt)에 곱할 비율
        bool text = scale >= 0.28f * Dpi || export;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // 1) 그룹 상자(맨 밑)
        foreach (var n in doc.Nodes.Where(n => n.Type == "group"))
        {
            var r = TR(n.Bounds);
            var col = ParseColor(n.Color) ?? p.Dim;
            using (var fb = new SolidBrush(Color.FromArgb(p.Dark ? 30 : 22, col))) using (var rp = LibraryPalette.Rounded(r, 12 * scale)) g.FillPath(fb, rp);
            bool sel = !export && selNodes.Contains(n);
            using (var pen = new Pen(sel ? p.Accent : Color.FromArgb(150, col), sel ? 2 * Dpi : Math.Max(1f, scale))) using (var rp = LibraryPalette.Rounded(r, 12 * scale)) g.DrawPath(pen, rp);
            if (text && !string.IsNullOrEmpty(n.Label))
                using (var f = new Font(Font.FontFamily, Math.Max(1f, 11f * fs), FontStyle.Bold))
                    TextRenderer.DrawText(g, n.Label, f, Rectangle.Round(new RectangleF(r.X + 10 * scale, r.Y - 24 * scale, Math.Max(10, r.Width - 20 * scale), 22 * scale)), p.Fore, flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Bottom);
        }

        // 2) 연결선
        foreach (var e in doc.Edges)
        {
            var a = Node(e.FromNode); var b = Node(e.ToNode);
            if (a == null || b == null) continue;
            var (p0, c1, c2, p3) = Curve(e, a, b);
            var col = ParseColor(e.Color) ?? Themes.Blend(p.Main, p.Fore, 0.5f);
            bool sel = !export && selEdges.Contains(e);
            using var pen = new Pen(sel ? p.Accent : col, Math.Max(1.2f, (sel ? 3f : 2f) * scale));
            g.DrawBezier(pen, T(p0), T(c1), T(c2), T(p3));
            if (e.ToEnd != "none") Arrow(g, T(c2), T(p3), sel ? p.Accent : col, scale);
            if (e.FromEnd == "arrow") Arrow(g, T(c1), T(p0), sel ? p.Accent : col, scale);
            if (sel)
                using (var hb = new SolidBrush(p.Accent))
                    foreach (var q in new[] { T(p0), T(p3) }) g.FillEllipse(hb, q.X - 5 * Dpi, q.Y - 5 * Dpi, 10 * Dpi, 10 * Dpi);
            if (text && !string.IsNullOrEmpty(e.Label))
            {
                var mid = T(BezierAt(p0, c1, c2, p3, 0.5f));
                using var f = new Font(Font.FontFamily, Math.Max(1f, 9f * fs));
                var sz = TextRenderer.MeasureText(e.Label, f, Size.Empty, flags);
                var lr = new RectangleF(mid.X - sz.Width / 2f - 4, mid.Y - sz.Height / 2f - 2, sz.Width + 8, sz.Height + 4);
                using (var lb = new SolidBrush(p.Main)) g.FillRectangle(lb, lr);
                TextRenderer.DrawText(g, e.Label, f, Rectangle.Round(lr), p.Fore, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // 3) 노드
        foreach (var n in doc.Nodes.Where(n => n.Type != "group"))
        {
            var r = TR(n.Bounds);
            if (!export && !r.IntersectsWith(ClientRectangle)) continue;
            bool sel = !export && selNodes.Contains(n);
            var col = ParseColor(n.Color);
            bool isFile = n.Type == "file";
            var fill = col is Color cc ? Themes.Blend(p.Card, cc, p.Dark ? 0.30f : 0.22f) : (isFile ? p.Card : Themes.Blend(p.Main, p.Card, 0.6f));
            using (var path = LibraryPalette.Rounded(r, 10 * scale))
            {
                if (!export)
                    using (var sh = new SolidBrush(Color.FromArgb(p.Dark ? 60 : 18, 0, 0, 0)))
                    {
                        var sr = r; sr.Offset(0, 2 * scale);
                        using var sp = LibraryPalette.Rounded(sr, 10 * scale);
                        g.FillPath(sh, sp);
                    }
                using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
                using (var pen = new Pen(sel ? p.Accent : col ?? p.Line, sel ? 2 * Dpi : Math.Max(1f, scale))) g.DrawPath(pen, path);
            }
            if (!text) continue;
            float pad = 12 * scale;
            var inner = new RectangleF(r.X + pad, r.Y + pad * 0.85f, r.Width - pad * 2, r.Height - pad * 1.7f);
            if (inner.Width < 4 || inner.Height < 4) continue;
            if (isFile)
            {
                var (title, sub, preview, glyph, missing) = Info(n);
                bool wiki = CanvasStore.IsWikiRel(n.File ?? "");
                // 왼쪽 색 띠: 에피소드는 강조색, 위키는 옅은 강조색
                var band = new RectangleF(r.X, r.Y + 10 * scale, Math.Max(2f, 3 * scale), Math.Max(0, r.Height - 20 * scale));
                using (var bb = new SolidBrush(missing ? p.Dim : wiki ? Themes.Blend(p.Card, p.Accent, 0.55f) : p.Accent)) g.FillRectangle(bb, band);
                using var small = new Font(Font.FontFamily, Math.Max(1f, 8.5f * fs));
                using var icon = new Font(MainForm.IconFontName(), Math.Max(1f, 8.5f * fs));
                using var bold = new Font(Font.FontFamily, Math.Max(1f, 11.5f * fs), FontStyle.Bold);
                using var body = new Font(Font.FontFamily, Math.Max(1f, 9f * fs));
                float y = inner.Y;
                int lh = TextRenderer.MeasureText("가", small, Size.Empty, flags).Height;
                TextRenderer.DrawText(g, glyph, icon, Rectangle.Round(new RectangleF(inner.X, y, lh * 1.4f, lh)), p.Dim, flags | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, sub, small, Rectangle.Round(new RectangleF(inner.X + lh * 1.5f, y, inner.Width - lh * 1.5f, lh)), p.Dim, flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                y += lh + 4 * scale;
                int bh = TextRenderer.MeasureText("가", bold, Size.Empty, flags).Height;
                TextRenderer.DrawText(g, title, bold, Rectangle.Round(new RectangleF(inner.X, y, inner.Width, bh)), missing ? p.Dim : p.Fore, flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += bh + 4 * scale;
                if (preview.Length > 0 && y < inner.Bottom)
                    TextRenderer.DrawText(g, preview, body, Rectangle.Round(new RectangleF(inner.X, y, inner.Width, inner.Bottom - y)), p.Dim, flags | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            }
            else if (n != editing)
            {
                string t = n.Type == "link" ? n.Url ?? "" : n.Text ?? "";
                using var body = new Font(Font.FontFamily, Math.Max(1f, 10.5f * fs));
                if (t.Length == 0 && !export) TextRenderer.DrawText(g, "두 번 눌러 메모 쓰기", body, Rectangle.Round(inner), p.Dim, flags | TextFormatFlags.WordBreak);
                else DrawMemoText(g, t, body, inner, p.Fore, fs);
            }
        }

        if (export) return;
        // 4) 잇는 점(마우스를 올린 · 고른 노드) · 크기 손잡이
        var show = new HashSet<CanvasNode>(selNodes.Where(n => n.Type != "group"));
        if (hoverNode != null && hoverNode.Type != "group") show.Add(hoverNode);
        if (mode is Mode.None or Mode.Connect)
            using (var hb = new SolidBrush(p.Main)) using (var hp = new Pen(p.Accent, 1.6f * Dpi))
                foreach (var n in show)
                    foreach (var side in Sides)
                    {
                        var q = W2S(SidePoint(n, side));
                        float rr = 5 * Dpi;
                        g.FillEllipse(hb, q.X - rr, q.Y - rr, rr * 2, rr * 2);
                        g.DrawEllipse(hp, q.X - rr, q.Y - rr, rr * 2, rr * 2);
                    }
        if (selNodes.Count == 1 && mode is Mode.None or Mode.Resize)
        {
            var hr = ResizeHandle(selNodes.First());
            using var b = new SolidBrush(p.Accent);
            g.FillRectangle(b, hr);
        }
    }

    /// <summary>메모 글: 줄 맨 앞 "# "은 제목처럼 진하게, 나머지는 그대로(넘치면 잘린다).</summary>
    private void DrawMemoText(Graphics g, string t, Font body, RectangleF inner, Color fore, float fs)
    {
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        float y = inner.Y;
        using var head = new Font(Font.FontFamily, Math.Max(1f, 12.5f * fs), FontStyle.Bold);
        foreach (var raw in t.Replace("\r\n", "\n").Split('\n'))
        {
            if (y >= inner.Bottom) break;
            bool h = raw.StartsWith("#");
            string line = h ? raw.TrimStart('#').TrimStart() : raw;
            var f = h ? head : body;
            var sz = TextRenderer.MeasureText(g, line.Length == 0 ? " " : line, f, new Size((int)inner.Width, int.MaxValue), flags);
            float lh = Math.Min(sz.Height, inner.Bottom - y);
            TextRenderer.DrawText(g, line, f, Rectangle.Round(new RectangleF(inner.X, y, inner.Width, lh)), fore, flags | TextFormatFlags.EndEllipsis);
            y += sz.Height;
        }
    }

    private static void Arrow(Graphics g, PointF from, PointF tip, Color c, float scale)
    {
        float dx = tip.X - from.X, dy = tip.Y - from.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return;
        dx /= len; dy /= len;
        float s = Math.Max(5f, 11f * scale), w = s * 0.55f;
        var basePt = new PointF(tip.X - dx * s, tip.Y - dy * s);
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[] { tip, new PointF(basePt.X - dy * w, basePt.Y + dx * w), new PointF(basePt.X + dy * w, basePt.Y - dx * w) });
    }

    // ───────── 도형 계산 ─────────

    private static readonly string[] Sides = { "top", "right", "bottom", "left" };

    private CanvasNode? Node(string id) => doc.Nodes.FirstOrDefault(n => n.Id == id);

    private static PointF SidePoint(CanvasNode n, string side) => side switch
    {
        "top" => new(n.X + n.Width / 2f, n.Y),
        "bottom" => new(n.X + n.Width / 2f, n.Y + n.Height),
        "left" => new(n.X, n.Y + n.Height / 2f),
        _ => new(n.X + n.Width, n.Y + n.Height / 2f),
    };

    private static PointF Normal(string side) => side switch { "top" => new(0, -1), "bottom" => new(0, 1), "left" => new(-1, 0), _ => new(1, 0) };

    private static string NearestSide(CanvasNode n, PointF w) =>
        Sides.OrderBy(s => { var q = SidePoint(n, s); return (q.X - w.X) * (q.X - w.X) + (q.Y - w.Y) * (q.Y - w.Y); }).First();

    /// <summary>쪽이 정해지지 않은 선(다른 캔버스에서 가져온 것 등)은 두 노드가 마주 보는 쪽을 쓴다.</summary>
    private static (string, string) AutoSides(CanvasNode a, CanvasNode b)
    {
        float dx = (b.X + b.Width / 2f) - (a.X + a.Width / 2f), dy = (b.Y + b.Height / 2f) - (a.Y + a.Height / 2f);
        return Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? ("right", "left") : ("left", "right")) : (dy > 0 ? ("bottom", "top") : ("top", "bottom"));
    }

    private static (PointF, PointF, PointF, PointF) Curve(CanvasEdge e, CanvasNode a, CanvasNode b)
    {
        var (sa, sb) = AutoSides(a, b);
        string fs = e.FromSide ?? sa, ts = e.ToSide ?? sb;
        var p0 = SidePoint(a, fs); var p3 = SidePoint(b, ts);
        float dist = MathF.Sqrt((p3.X - p0.X) * (p3.X - p0.X) + (p3.Y - p0.Y) * (p3.Y - p0.Y));
        float k = Math.Clamp(dist * 0.4f, 30f, 160f);
        var n0 = Normal(fs); var n3 = Normal(ts);
        return (p0, new PointF(p0.X + n0.X * k, p0.Y + n0.Y * k), new PointF(p3.X + n3.X * k, p3.Y + n3.Y * k), p3);
    }

    private static PointF BezierAt(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        float u = 1 - t;
        return new(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                   u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private static float SegDist(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        float t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    /// <summary>화면 좌표 근처의 연결선.</summary>
    private CanvasEdge? EdgeAt(Point screen)
    {
        float tol = 6 * Dpi;
        foreach (var e in Enumerable.Reverse(doc.Edges))
        {
            var a = Node(e.FromNode); var b = Node(e.ToNode);
            if (a == null || b == null) continue;
            var (p0, c1, c2, p3) = Curve(e, a, b);
            var prev = W2S(p0);
            for (int i = 1; i <= 24; i++)
            {
                var q = W2S(BezierAt(p0, c1, c2, p3, i / 24f));
                if (SegDist(screen, prev, q) <= tol) return e;
                prev = q;
            }
        }
        return null;
    }

    /// <summary>세계 좌표에 있는 노드(위에 그려진 것부터). 그룹 상자는 이름표 · 테두리 언저리에서만 잡힌다.</summary>
    private CanvasNode? NodeAt(PointF w, bool skipGroups = false)
    {
        var hit = doc.Nodes.LastOrDefault(n => n.Type != "group" && n.Bounds.Contains(w));
        if (hit != null || skipGroups) return hit;
        float band = 10 / zoom;
        return doc.Nodes.LastOrDefault(n =>
        {
            if (n.Type != "group") return false;
            var r = n.Bounds;
            var label = new RectangleF(r.X, r.Y - 26 / zoom, r.Width, 26 / zoom);
            if (label.Contains(w)) return true;
            var outer = RectangleF.Inflate(r, band, band); var inner = RectangleF.Inflate(r, -band, -band);
            return outer.Contains(w) && !inner.Contains(w);
        });
    }

    /// <summary>화면 좌표 근처의 잇는 점(마우스를 올린 · 고른 노드만).</summary>
    private (CanvasNode Node, string Side)? HandleAt(Point screen)
    {
        float tol = 8 * Dpi;
        var cands = selNodes.Where(n => n.Type != "group").ToList();
        if (hoverNode != null && hoverNode.Type != "group" && !cands.Contains(hoverNode)) cands.Add(hoverNode);
        foreach (var n in cands)
            foreach (var s in Sides)
            {
                var q = W2S(SidePoint(n, s));
                if (Math.Abs(q.X - screen.X) <= tol && Math.Abs(q.Y - screen.Y) <= tol) return (n, s);
            }
        return null;
    }

    private RectangleF ResizeHandle(CanvasNode n)
    {
        var r = W2S(n.Bounds);
        float s = 8 * Dpi;
        return new RectangleF(r.Right - s / 2, r.Bottom - s / 2, s, s);
    }

    // ───────── 마우스 ─────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        CommitEditor();
        downScreen = e.Location;
        downWorld = curWorld = S2W(e.Location);
        moved = false;
        guides.Clear();
        if (e.Button is MouseButtons.Middle or MouseButtons.Right) { mode = Mode.Pan; panStart = pan; return; }
        if (e.Button != MouseButtons.Left) return;

        if (selNodes.Count == 1 && ResizeHandle(selNodes.First()).Contains(e.Location))
        {
            resizeNode = selNodes.First(); resizeStart = new Size(resizeNode.Width, resizeNode.Height);
            pendingUndo = Snapshot(); mode = Mode.Resize; return;
        }
        if (HandleAt(e.Location) is { } h) { connectFrom = h.Node; connectSide = h.Side; mode = Mode.Connect; return; }
        foreach (var se in selEdges)
        {
            var a = Node(se.FromNode); var b = Node(se.ToNode);
            if (a == null || b == null) continue;
            var (p0, _, _, p3) = Curve(se, a, b);
            foreach (var (pt, fromEnd) in new[] { (p0, true), (p3, false) })
            {
                var q = W2S(pt);
                if (Math.Abs(q.X - e.X) <= 8 * Dpi && Math.Abs(q.Y - e.Y) <= 8 * Dpi)
                { reconnectEdge = se; reconnectFromEnd = fromEnd; pendingUndo = Snapshot(); mode = Mode.Reconnect; return; }
            }
        }
        var node = NodeAt(downWorld);
        if (node != null)
        {
            bool ctrl = (ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
            if (ctrl) { if (!selNodes.Remove(node)) selNodes.Add(node); }
            else if (!selNodes.Contains(node)) { selNodes.Clear(); selEdges.Clear(); selNodes.Add(node); }
            if (!selNodes.Contains(node)) { Invalidate(); return; }
            moveOrigins.Clear();
            var moving = new HashSet<CanvasNode>(selNodes);
            foreach (var gnode in selNodes.Where(n => n.Type == "group").ToList()) // 그룹 상자를 옮기면 안에 든 노드도 같이
                foreach (var inner in doc.Nodes.Where(n => n != gnode && gnode.Bounds.Contains(n.Bounds))) moving.Add(inner);
            foreach (var n in moving) moveOrigins[n] = new Point(n.X, n.Y);
            pendingUndo = Snapshot();
            mode = Mode.Move;
            Invalidate();
            return;
        }
        var edge = EdgeAt(e.Location);
        if (edge != null)
        {
            if ((ModifierKeys & Keys.Control) == 0) { selNodes.Clear(); selEdges.Clear(); }
            selEdges.Add(edge);
            Invalidate();
            return;
        }
        if (CanvasStore.ClickDrag) { mode = Mode.Pan; panStart = pan; return; }
        selectAdd = (ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
        if (!selectAdd) { selNodes.Clear(); selEdges.Clear(); }
        mode = Mode.Select;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        curWorld = S2W(e.Location);
        if (!moved && (Math.Abs(e.X - downScreen.X) > 3 || Math.Abs(e.Y - downScreen.Y) > 3) && mode != Mode.None) moved = true;
        switch (mode)
        {
            case Mode.Pan:
                pan = new PointF(panStart.X + e.X - downScreen.X, panStart.Y + e.Y - downScreen.Y);
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            case Mode.Move when moved:
                DoMove();
                return;
            case Mode.Resize when resizeNode != null:
                resizeNode.Width = Math.Max(80, (int)Math.Round(resizeStart.Width + curWorld.X - downWorld.X));
                resizeNode.Height = Math.Max(50, (int)Math.Round(resizeStart.Height + curWorld.Y - downWorld.Y));
                Invalidate();
                return;
            case Mode.Select:
            case Mode.Connect:
            case Mode.Reconnect:
                if (mode == Mode.Reconnect && reconnectEdge != null && moved && NodeAt(curWorld, skipGroups: true) is { } t)
                {
                    if (reconnectFromEnd) { reconnectEdge.FromNode = t.Id; reconnectEdge.FromSide = NearestSide(t, curWorld); }
                    else { reconnectEdge.ToNode = t.Id; reconnectEdge.ToSide = NearestSide(t, curWorld); }
                }
                Invalidate();
                return;
        }
        // 마우스만 올렸을 때: 잇는 점 · 노드 위 표시
        var hn = NodeAt(curWorld);
        if (hn != hoverNode) { hoverNode = hn; Invalidate(); }
        if (selNodes.Count == 1 && ResizeHandle(selNodes.First()).Contains(e.Location)) Cursor = Cursors.SizeNWSE;
        else if (HandleAt(e.Location) != null) Cursor = Cursors.Cross;
        else if (hn != null) Cursor = Cursors.SizeAll;
        else Cursor = Cursors.Default;
    }

    /// <summary>고른 노드를 끌어 옮긴다. 정렬 가이드 스냅이 켜져 있으면 다른 노드의 가장자리 · 가운데에 맞춘다.</summary>
    private void DoMove()
    {
        float dx = curWorld.X - downWorld.X, dy = curWorld.Y - downWorld.Y;
        guides.Clear();
        var movers = moveOrigins.Keys.ToList();
        if (CanvasStore.Snap && movers.Count > 0)
        {
            var box = movers.Select(n => new RectangleF(moveOrigins[n].X + dx, moveOrigins[n].Y + dy, n.Width, n.Height)).Aggregate(RectangleF.Union);
            float thr = 8 / zoom, bestX = thr, bestY = thr, adjX = 0, adjY = 0;
            RectangleF? ox = null, oy = null; float gx = 0, gy = 0;
            foreach (var o in doc.Nodes.Where(n => !moveOrigins.ContainsKey(n) && n.Type != "group"))
            {
                var r = o.Bounds;
                foreach (var mx in new[] { box.Left, box.Left + box.Width / 2, box.Right })
                    foreach (var tx in new[] { r.Left, r.Left + r.Width / 2, r.Right })
                        if (Math.Abs(tx - mx) < bestX) { bestX = Math.Abs(tx - mx); adjX = tx - mx; ox = r; gx = tx; }
                foreach (var my in new[] { box.Top, box.Top + box.Height / 2, box.Bottom })
                    foreach (var ty in new[] { r.Top, r.Top + r.Height / 2, r.Bottom })
                        if (Math.Abs(ty - my) < bestY) { bestY = Math.Abs(ty - my); adjY = ty - my; oy = r; gy = ty; }
            }
            dx += adjX; dy += adjY;
            box.Offset(adjX, adjY);
            if (ox is RectangleF rx) guides.Add((new PointF(gx, Math.Min(box.Top, rx.Top) - 20), new PointF(gx, Math.Max(box.Bottom, rx.Bottom) + 20)));
            if (oy is RectangleF ry) guides.Add((new PointF(Math.Min(box.Left, ry.Left) - 20, gy), new PointF(Math.Max(box.Right, ry.Right) + 20, gy)));
        }
        foreach (var n in movers) { n.X = (int)Math.Round(moveOrigins[n].X + dx); n.Y = (int)Math.Round(moveOrigins[n].Y + dy); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var m = mode;
        mode = Mode.None;
        guides.Clear();
        Cursor = Cursors.Default;
        switch (m)
        {
            case Mode.Pan when !moved && e.Button == MouseButtons.Right:
                ShowContextMenu(e.Location);
                break;
            case Mode.Move:
            case Mode.Resize:
            case Mode.Reconnect:
                if (moved && pendingUndo != null) { PushUndo(pendingUndo); Changed(); }
                break;
            case Mode.Select when moved:
            {
                var r = Norm(downWorld, curWorld);
                foreach (var n in doc.Nodes.Where(n => r.IntersectsWith(n.Bounds) && (n.Type != "group" || r.Contains(n.Bounds)))) selNodes.Add(n);
                foreach (var ed in doc.Edges.Where(ed => selNodes.Any(n => n.Id == ed.FromNode) && selNodes.Any(n => n.Id == ed.ToNode))) selEdges.Add(ed);
                break;
            }
            case Mode.Connect when moved && connectFrom != null:
            {
                var target = NodeAt(curWorld, skipGroups: true);
                if (target != null && target != connectFrom)
                {
                    PushUndo();
                    var edge = new CanvasEdge { FromNode = connectFrom.Id, FromSide = connectSide, ToNode = target.Id, ToSide = NearestSide(target, curWorld) };
                    doc.Edges.Add(edge);
                    selNodes.Clear(); selEdges.Clear(); selEdges.Add(edge);
                    Changed();
                }
                break;
            }
        }
        pendingUndo = null;
        connectFrom = null; reconnectEdge = null; resizeNode = null;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var w = S2W(e.Location);
        var n = NodeAt(w);
        if (n == null) { AddMemo(new PointF(w.X - 120, w.Y - 60), edit: true); return; }
        if (n.Type == "file" && n.File != null) { OpenRequested?.Invoke(CanvasStore.ToFull(novelDir, n.File)); return; }
        if (n.Type == "text") BeginEdit(n);
        if (n.Type == "group") RenameGroupNode(n);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) { ZoomAt(e.Location, MathF.Pow(1.12f, e.Delta / 120f)); return; }
        float d = e.Delta * 0.6f * Dpi;
        if ((ModifierKeys & Keys.Shift) != 0) pan.X += d; else pan.Y += d;
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (m.Msg == WM_MOUSEHWHEEL) { pan.X -= (short)((long)m.WParam >> 16) * 0.6f * Dpi; Invalidate(); m.Result = IntPtr.Zero; return; } // 트랙패드 좌우 스크롤
        base.WndProc(ref m);
    }

    public void ZoomAt(PointF screen, float factor)
    {
        var w = S2W(screen);
        zoom = Math.Clamp(zoom * factor, 0.1f, 4f);
        pan = new PointF(screen.X - w.X * ZoomPx, screen.Y - w.Y * ZoomPx);
        Invalidate();
        ZoomChanged?.Invoke();
    }

    public void ZoomBy(float factor) => ZoomAt(new PointF(Width / 2f, Height / 2f), factor);
    public void ResetZoom() => ZoomAt(new PointF(Width / 2f, Height / 2f), 1f / zoom);

    /// <summary>모든 노드가 보이게 맞춘다.</summary>
    public void FitAll()
    {
        if (doc.Nodes.Count == 0) { zoom = 1; pan = new PointF(S(40), S(40)); Invalidate(); ZoomChanged?.Invoke(); return; }
        var b = doc.Nodes.Select(n => n.Bounds).Aggregate(RectangleF.Union);
        b.Inflate(60, 60);
        zoom = Math.Clamp(Math.Min(Width / (b.Width * Dpi), Height / (b.Height * Dpi)), 0.1f, 1.5f);
        pan = new PointF(Width / 2f - (b.X + b.Width / 2) * ZoomPx, Height / 2f - (b.Y + b.Height / 2) * ZoomPx);
        Invalidate();
        ZoomChanged?.Invoke();
    }

    private bool fitted;
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!fitted && Width > 100 && Height > 100 && doc.Nodes.Count > 0) { fitted = true; FitAll(); } // 처음 열 때 한 번
    }

    // ───────── 키 ─────────

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Delete or Keys.Back or Keys.Escape or Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editor != null) return;
        switch (e.KeyCode)
        {
            case Keys.Delete or Keys.Back: DeleteSelection(); e.Handled = true; break;
            case Keys.Escape: selNodes.Clear(); selEdges.Clear(); Invalidate(); break;
            case Keys.C when e.Control: Copy(); e.Handled = true; break;
            case Keys.X when e.Control: Copy(); DeleteSelection(); e.Handled = true; break;
            case Keys.V when e.Control: Paste(); e.Handled = true; break;
            case Keys.A when e.Control: selNodes.UnionWith(doc.Nodes); selEdges.UnionWith(doc.Edges); Invalidate(); e.Handled = true; break;
            case Keys.Z when e.Control && e.Shift: Redo(); e.Handled = true; break;
            case Keys.Z when e.Control: Undo(); e.Handled = true; break;
            case Keys.Y when e.Control: Redo(); e.Handled = true; break;
            case Keys.D0 when e.Control: ResetZoom(); e.Handled = true; break;
            case Keys.Oemplus or Keys.Add when e.Control: ZoomBy(1.2f); e.Handled = true; break;
            case Keys.OemMinus or Keys.Subtract when e.Control: ZoomBy(1 / 1.2f); e.Handled = true; break;
            case Keys.F2 when selNodes.Count == 1 && selNodes.First().Type == "text": BeginEdit(selNodes.First()); break;
            case Keys.Enter when selNodes.Count == 1:
                var n = selNodes.First();
                if (n.Type == "text") BeginEdit(n);
                else if (n.Type == "file" && n.File != null) OpenRequested?.Invoke(CanvasStore.ToFull(novelDir, n.File));
                break;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when selNodes.Count > 0: // 화살표로 조금씩 옮기기(Shift면 크게)
                int step = e.Shift ? 40 : 8;
                int ax = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0, ay = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                PushUndo();
                foreach (var s in selNodes) { s.X += ax; s.Y += ay; }
                Changed();
                e.Handled = true;
                break;
        }
    }

    public void DeleteSelection()
    {
        if (selNodes.Count == 0 && selEdges.Count == 0) return;
        PushUndo();
        var ids = selNodes.Select(n => n.Id).ToHashSet();
        doc.Nodes.RemoveAll(n => ids.Contains(n.Id));
        doc.Edges.RemoveAll(e => selEdges.Contains(e) || ids.Contains(e.FromNode) || ids.Contains(e.ToNode));
        selNodes.Clear(); selEdges.Clear(); hoverNode = null;
        Changed();
    }

    // ───────── 복사 · 붙여넣기 ─────────

    public void Copy()
    {
        if (selNodes.Count == 0) return;
        var ids = selNodes.Select(n => n.Id).ToHashSet();
        clipNodes = selNodes.Select(n => n.Clone()).ToList();
        clipEdges = doc.Edges.Where(e => ids.Contains(e.FromNode) && ids.Contains(e.ToNode)).Select(e => e.Clone()).ToList();
        try { Clipboard.SetText(CanvasStore.ToJson(new CanvasDoc { Nodes = clipNodes, Edges = clipEdges })); } catch { } // 다른 캔버스 · 다른 프로그램에도
    }

    public void Paste()
    {
        List<CanvasNode>? nodes = null; List<CanvasEdge>? edges = null;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() is { } t && t.TrimStart().StartsWith("{") && t.Contains("\"nodes\"") && CanvasStore.Parse(t) is { } d)
            { nodes = d.Nodes; edges = d.Edges; }
        }
        catch { }
        if (nodes == null && clipNodes != null) { nodes = clipNodes.Select(n => n.Clone()).ToList(); edges = clipEdges?.Select(e => e.Clone()).ToList() ?? new(); }
        if (nodes == null || nodes.Count == 0) return;
        PushUndo();
        var b = nodes.Select(n => n.Bounds).Aggregate(RectangleF.Union);
        var mouse = PointToClient(Cursor.Position);
        var at = ClientRectangle.Contains(mouse) ? S2W(mouse) : new PointF(b.X + 30, b.Y + 30);
        float ox = at.X - b.X, oy = at.Y - b.Y;
        var map = new Dictionary<string, string>();
        selNodes.Clear(); selEdges.Clear();
        foreach (var n in nodes)
        {
            string nid = CanvasNode.NewId();
            map[n.Id] = nid;
            n.Id = nid; n.X = (int)(n.X + ox); n.Y = (int)(n.Y + oy);
            doc.Nodes.Add(n);
            selNodes.Add(n);
        }
        foreach (var e in edges ?? new())
        {
            if (!map.TryGetValue(e.FromNode, out var f) || !map.TryGetValue(e.ToNode, out var t2)) continue;
            e.Id = CanvasNode.NewId(); e.FromNode = f; e.ToNode = t2;
            doc.Edges.Add(e);
        }
        Changed();
    }

    // ───────── 노드 만들기 · 고치기 ─────────

    private PointF ViewCenter() => S2W(new PointF(Width / 2f, Height / 2f));

    /// <summary>새 노드 자리: 화면 가운데에서 이미 놓인 것과 겹치지 않게 조금씩 비켜 놓는다.</summary>
    private PointF FreeSpot(int w, int h)
    {
        var c = ViewCenter();
        var at = new PointF(c.X - w / 2f, c.Y - h / 2f);
        for (int i = 0; i < 30 && doc.Nodes.Any(n => n.Type != "group" && n.Bounds.IntersectsWith(new RectangleF(at.X, at.Y, w, h))); i++) at = new PointF(at.X + 30, at.Y + 30);
        return at;
    }

    public CanvasNode AddMemo(PointF? at = null, bool edit = true)
    {
        PushUndo();
        var spot = at ?? FreeSpot(240, 140);
        var n = new CanvasNode { Type = "text", Text = "", X = (int)spot.X, Y = (int)spot.Y, Width = 240, Height = 140 };
        doc.Nodes.Add(n);
        selNodes.Clear(); selEdges.Clear(); selNodes.Add(n);
        Changed();
        if (edit) BeginEdit(n);
        return n;
    }

    /// <summary>에피소드 · 위키 문서를 가리키는 노드를 놓는다(같은 문서를 여러 번 놓아도 된다).</summary>
    public void AddFile(string fullPath)
    {
        PushUndo();
        var spot = FreeSpot(260, 130);
        var n = new CanvasNode { Type = "file", File = CanvasStore.ToRel(novelDir, fullPath), X = (int)spot.X, Y = (int)spot.Y, Width = 260, Height = 130 };
        doc.Nodes.Add(n);
        selNodes.Clear(); selEdges.Clear(); selNodes.Add(n);
        Changed();
    }

    public void AddGroupBox()
    {
        PushUndo();
        RectangleF r = selNodes.Count > 0 ? selNodes.Select(n => n.Bounds).Aggregate(RectangleF.Union) : new RectangleF(FreeSpot(420, 300), new SizeF(420, 300));
        if (selNodes.Count > 0) r.Inflate(30, 30);
        var g = new CanvasNode { Type = "group", Label = "묶음", X = (int)r.X, Y = (int)r.Y, Width = (int)r.Width, Height = (int)r.Height };
        doc.Nodes.Insert(0, g); // 묶음 상자는 다른 노드 밑에
        selNodes.Clear(); selEdges.Clear(); selNodes.Add(g);
        Changed();
        RenameGroupNode(g);
    }

    private void RenameGroupNode(CanvasNode g)
    {
        using var dlg = new NovelInputDialog("묶음 이름", "이름", g.Label ?? "");
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        PushUndo();
        g.Label = dlg.FirstValue.Trim();
        Changed();
    }

    private void SetColor(string key)
    {
        if (selNodes.Count == 0 && selEdges.Count == 0) return;
        PushUndo();
        foreach (var n in selNodes) n.Color = key.Length == 0 ? null : key;
        foreach (var e in selEdges) e.Color = key.Length == 0 ? null : key;
        Changed();
    }

    private void BeginEdit(CanvasNode n)
    {
        CommitEditor();
        editing = n;
        var p = Pal;
        var r = Rectangle.Round(W2S(n.Bounds));
        int pad = (int)(12 * ZoomPx);
        editor = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None,
            Font = new Font(Font.FontFamily, Math.Max(6f, 10.5f * zoom)), Text = (n.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            BackColor = ParseColor(n.Color) is Color cc ? Themes.Blend(p.Card, cc, p.Dark ? 0.30f : 0.22f) : Themes.Blend(p.Main, p.Card, 0.6f),
            ForeColor = p.Fore,
        };
        editor.SetBounds(r.X + pad, r.Y + pad, Math.Max(20, r.Width - pad * 2), Math.Max(20, r.Height - pad * 2));
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Enter && e.Control)) { e.SuppressKeyPress = true; BeginInvoke(() => { CommitEditor(); Focus(); }); }
        };
        editor.LostFocus += (_, _) => BeginInvoke(CommitEditor);
        Controls.Add(editor);
        editor.Focus();
        editor.SelectionStart = editor.TextLength;
        Invalidate();
    }

    private void CommitEditor()
    {
        if (editor == null || editing == null) return;
        var ed = editor; var n = editing;
        editor = null; editing = null;
        string t = ed.Text.Replace("\r\n", "\n");
        if (t != (n.Text ?? "")) { PushUndo(); n.Text = t; dirty = true; saveTimer.Stop(); saveTimer.Start(); }
        Controls.Remove(ed);
        ed.Dispose();
        Invalidate();
    }

    // ───────── 메뉴 ─────────

    /// <summary>[에피소드 · 위키 추가]를 띄워 달라(툴바가 검색 목록을 연다).</summary>
    public event Action<Point>? AddDocRequested;

    private void ShowContextMenu(Point at)
    {
        var w = S2W(at);
        var n = NodeAt(w);
        var e = n == null ? EdgeAt(at) : null;
        if (n != null && !selNodes.Contains(n)) { selNodes.Clear(); selEdges.Clear(); selNodes.Add(n); }
        if (e != null && !selEdges.Contains(e)) { selNodes.Clear(); selEdges.Clear(); selEdges.Add(e); }
        Invalidate();
        var m = new ContextMenuStrip { Font = new Font(NovelStore.UiFontFamily, 9.5f) };
        if (n == null && e == null)
        {
            m.Items.Add("메모 추가", null, (_, _) => AddMemo(new PointF(w.X - 120, w.Y - 70)));
            m.Items.Add("에피소드 · 위키 추가…", null, (_, _) => AddDocRequested?.Invoke(PointToScreen(at)));
            m.Items.Add("묶음 상자 추가", null, (_, _) => AddGroupBox());
            if (clipNodes != null) m.Items.Add("붙여넣기", null, (_, _) => Paste());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("전체 보기", null, (_, _) => FitAll());
        }
        else
        {
            if (n?.Type == "file" && n.File != null) m.Items.Add("원본 열기", null, (_, _) => OpenRequested?.Invoke(CanvasStore.ToFull(novelDir, n.File)));
            if (n?.Type == "text") m.Items.Add("메모 고치기", null, (_, _) => BeginEdit(n));
            if (n?.Type == "group") m.Items.Add("이름 바꾸기…", null, (_, _) => RenameGroupNode(n));
            if (e != null)
            {
                m.Items.Add("이름표 달기…", null, (_, _) =>
                {
                    using var dlg = new NovelInputDialog("연결선 이름표", "이름표", e.Label ?? "");
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    PushUndo(); e.Label = dlg.FirstValue.Trim().Length == 0 ? null : dlg.FirstValue.Trim(); Changed();
                });
                m.Items.Add("방향 뒤집기", null, (_, _) =>
                {
                    PushUndo();
                    (e.FromNode, e.ToNode) = (e.ToNode, e.FromNode);
                    (e.FromSide, e.ToSide) = (e.ToSide, e.FromSide);
                    Changed();
                });
            }
            var colors = new ToolStripMenuItem("색");
            foreach (var (key, name) in ColorNames) { string k = key; colors.DropDownItems.Add(name, null, (_, _) => SetColor(k)); }
            m.Items.Add(colors);
            if (n != null) { m.Items.Add("복사", null, (_, _) => Copy()); }
            if (selNodes.Count > 1) m.Items.Add("묶음 상자로 감싸기", null, (_, _) => AddGroupBox());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("삭제", null, (_, _) => DeleteSelection());
        }
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(this, at);
    }

    // ───────── 내보내기 · 가져오기 ─────────

    /// <summary>캔버스를 그림(PNG · JPG) · SVG · JSON으로 내보낸다.</summary>
    public void Export(string format)
    {
        Flush();
        if (doc.Nodes.Count == 0 && format != "json") { MessageBox.Show(FindForm(), "캔버스가 비어 있어요.", "캔버스 내보내기"); return; }
        string ext = format switch { "jpg" => "jpg", "svg" => "svg", "json" => "canvas", _ => "png" };
        string filter = format switch
        {
            "jpg" => "JPG 그림 (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            "svg" => "SVG 그림 (*.svg)|*.svg",
            "json" => "JSON Canvas (*.canvas)|*.canvas|JSON (*.json)|*.json",
            _ => "PNG 그림 (*.png)|*.png",
        };
        using var dlg = new SaveFileDialog { Filter = filter, FileName = Path.GetFileNameWithoutExtension(path) + "." + ext, Title = "캔버스 내보내기" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            switch (format)
            {
                case "json": File.WriteAllText(dlg.FileName, CanvasStore.ToJson(doc), new UTF8Encoding(false)); break;
                case "svg": File.WriteAllText(dlg.FileName, ToSvg(), new UTF8Encoding(false)); break;
                default:
                    using (var bmp = ToBitmap(format == "jpg"))
                        bmp.Save(dlg.FileName, format == "jpg" ? ImageFormat.Jpeg : ImageFormat.Png);
                    break;
            }
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"내보내지 못했어요: {ex.Message}", "캔버스 내보내기", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private RectangleF ContentBounds()
    {
        var b = doc.Nodes.Select(n => n.Type == "group" ? RectangleF.FromLTRB(n.X, n.Y - 28, n.X + n.Width, n.Y + n.Height) : n.Bounds).Aggregate(RectangleF.Union);
        b.Inflate(40, 40);
        return b;
    }

    private Bitmap ToBitmap(bool opaque)
    {
        var b = ContentBounds();
        float scale = Math.Min(2f, Math.Max(0.25f, 8000f / Math.Max(b.Width, b.Height))); // 선명하게 2배(너무 크면 줄인다)
        int w = Math.Max(1, (int)(b.Width * scale)), h = Math.Max(1, (int)(b.Height * scale));
        var bmp = new Bitmap(w, h, opaque ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Pal.Main);
        Render(g, scale, new PointF(-b.X * scale, -b.Y * scale), export: true);
        return bmp;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    /// <summary>벡터 그림(SVG): 상자 · 선 · 글(넘치는 글은 줄여서).</summary>
    private string ToSvg()
    {
        var p = Pal;
        var b = ContentBounds();
        string ff = Esc(Font.FontFamily.Name);
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{b.X:0} {b.Y:0} {b.Width:0} {b.Height:0}\" width=\"{b.Width:0}\" height=\"{b.Height:0}\" font-family=\"{ff}, sans-serif\">");
        sb.AppendLine($"<rect x=\"{b.X:0}\" y=\"{b.Y:0}\" width=\"{b.Width:0}\" height=\"{b.Height:0}\" fill=\"{Hex(p.Main)}\"/>");
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        foreach (var n in doc.Nodes.Where(n => n.Type == "group"))
        {
            var col = ParseColor(n.Color) ?? p.Dim;
            sb.AppendLine($"<rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"{n.Height}\" rx=\"12\" fill=\"{Hex(col)}\" fill-opacity=\"0.09\" stroke=\"{Hex(col)}\" stroke-opacity=\"0.6\"/>");
            if (!string.IsNullOrEmpty(n.Label)) sb.AppendLine($"<text x=\"{n.X + 10}\" y=\"{n.Y - 8}\" font-size=\"15\" font-weight=\"bold\" fill=\"{Hex(p.Fore)}\">{Esc(n.Label)}</text>");
        }
        foreach (var e in doc.Edges)
        {
            var a = Node(e.FromNode); var c = Node(e.ToNode);
            if (a == null || c == null) continue;
            var (p0, c1, c2, p3) = Curve(e, a, c);
            var col = Hex(ParseColor(e.Color) ?? Themes.Blend(p.Main, p.Fore, 0.5f));
            sb.AppendLine($"<path d=\"M{p0.X:0.#},{p0.Y:0.#} C{c1.X:0.#},{c1.Y:0.#} {c2.X:0.#},{c2.Y:0.#} {p3.X:0.#},{p3.Y:0.#}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"2\"/>");
            if (e.ToEnd != "none")
            {
                float dx = p3.X - c2.X, dy = p3.Y - c2.Y, len = MathF.Max(0.01f, MathF.Sqrt(dx * dx + dy * dy)); dx /= len; dy /= len;
                float s = 11, w = s * 0.55f; var bp = new PointF(p3.X - dx * s, p3.Y - dy * s);
                sb.AppendLine($"<polygon points=\"{p3.X:0.#},{p3.Y:0.#} {bp.X - dy * w:0.#},{bp.Y + dx * w:0.#} {bp.X + dy * w:0.#},{bp.Y - dx * w:0.#}\" fill=\"{col}\"/>");
            }
            if (!string.IsNullOrEmpty(e.Label)) { var mid = BezierAt(p0, c1, c2, p3, 0.5f); sb.AppendLine($"<text x=\"{mid.X:0.#}\" y=\"{mid.Y:0.#}\" font-size=\"12\" text-anchor=\"middle\" fill=\"{Hex(p.Fore)}\">{Esc(e.Label)}</text>"); }
        }
        foreach (var n in doc.Nodes.Where(n => n.Type != "group"))
        {
            var col = ParseColor(n.Color);
            bool isFile = n.Type == "file";
            var fill = col is Color cc ? Themes.Blend(p.Card, cc, p.Dark ? 0.30f : 0.22f) : (isFile ? p.Card : Themes.Blend(p.Main, p.Card, 0.6f));
            sb.AppendLine($"<rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"{n.Height}\" rx=\"10\" fill=\"{Hex(fill)}\" stroke=\"{Hex(col ?? p.Line)}\"/>");
            var lines = new List<(string Text, int Size, bool Bold, Color Color)>();
            if (isFile)
            {
                var (title, sub, preview, _, missing) = Info(n);
                lines.Add((sub, 11, false, p.Dim));
                lines.Add((title, 15, true, missing ? p.Dim : p.Fore));
                foreach (var l in Wrap(measure, preview, 12, n.Width - 24)) lines.Add((l, 12, false, p.Dim));
            }
            else
                foreach (var raw in (n.Type == "link" ? n.Url ?? "" : n.Text ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    bool h = raw.StartsWith("#");
                    string t = h ? raw.TrimStart('#').TrimStart() : raw;
                    foreach (var l in Wrap(measure, t, h ? 16 : 14, n.Width - 24, h)) lines.Add((l, h ? 16 : 14, h, p.Fore));
                }
            float y = n.Y + 12;
            foreach (var (t, size, bold, c) in lines)
            {
                y += size * 1.35f;
                if (y > n.Y + n.Height - 8) break;
                sb.AppendLine($"<text x=\"{n.X + 12}\" y=\"{y:0.#}\" font-size=\"{size}\"{(bold ? " font-weight=\"bold\"" : "")} fill=\"{Hex(c)}\">{Esc(t)}</text>");
            }
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private List<string> Wrap(Graphics g, string text, int px, int width, bool bold = false)
    {
        var res = new List<string>();
        if (text.Length == 0) { res.Add(""); return res; }
        using var f = new Font(Font.FontFamily, px * 72f / 96f, bold ? FontStyle.Bold : FontStyle.Regular);
        var line = new StringBuilder();
        foreach (char ch in text)
        {
            line.Append(ch);
            if (TextRenderer.MeasureText(g, line.ToString(), f, Size.Empty, TextFormatFlags.NoPadding).Width * 96f / g.DpiX > width && line.Length > 1)
            {
                line.Length--;
                res.Add(line.ToString());
                line.Clear().Append(ch);
                if (res.Count > 40) break;
            }
        }
        if (line.Length > 0) res.Add(line.ToString());
        return res;
    }

    /// <summary>JSON Canvas(옵시디언 .canvas 포함)를 지금 캔버스 오른쪽에 덧붙인다.</summary>
    public void Import()
    {
        using var dlg = new OpenFileDialog { Filter = "JSON Canvas (*.canvas;*.json)|*.canvas;*.json|모든 파일|*.*", Title = "캔버스 가져오기" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        CanvasDoc? d = null;
        try { d = CanvasStore.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8)); } catch { }
        if (d == null || d.Nodes.Count == 0) { MessageBox.Show(FindForm(), "가져올 노드가 없거나 JSON Canvas 형식이 아니에요.", "캔버스 가져오기", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        PushUndo();
        float ox = 0, oy = 0;
        var nb = d.Nodes.Select(n => n.Bounds).Aggregate(RectangleF.Union);
        if (doc.Nodes.Count > 0) { var cb = doc.Nodes.Select(n => n.Bounds).Aggregate(RectangleF.Union); ox = cb.Right + 120 - nb.X; oy = cb.Y - nb.Y; }
        var map = new Dictionary<string, string>();
        foreach (var n in d.Nodes)
        {
            map[n.Id] = n.Id = CanvasNode.NewId();
            n.X = (int)(n.X + ox); n.Y = (int)(n.Y + oy);
            if (n.Type is not ("text" or "file" or "link" or "group")) n.Type = "text";
            doc.Nodes.Add(n);
        }
        foreach (var e in d.Edges) { e.Id = CanvasNode.NewId(); e.FromNode = map[e.FromNode]; e.ToNode = map[e.ToNode]; doc.Edges.Add(e); }
        Changed();
        FitAll();
        int missing = d.Nodes.Count(n => n.Type == "file" && Info(n).Missing);
        if (missing > 0) MessageBox.Show(FindForm(), $"가져왔어요. 다만 이 작품에서 찾을 수 없는 문서를 가리키는 노드가 {missing}개 있어요.\n문서 연결 · 내용 · 연결선을 한번 확인해 주세요.", "캔버스 가져오기");
    }
}

/// <summary>문서 칸에 열리는 캔버스 화면: 위쪽 도구 줄([에피소드 · 위키 추가] [메모] [묶음] ··· [−] 100% [+] [전체 보기]) + 캔버스 판.</summary>
internal sealed class PlotCanvasPage : Panel
{
    private readonly string novelDir;
    private readonly Panel bar = new();
    private readonly PillButton addDoc, addMemo, addGroup, fit, zoomOut, zoomIn;
    private readonly Label zoomLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private LibraryPalette? palette;
    private ToolStripDropDown? picker;

    public PlotCanvasView View { get; }
    public event Action<string>? OpenRequested;

    public PlotCanvasPage(string path, string novelDir)
    {
        this.novelDir = novelDir;
        string ff = NovelStore.UiFontFamily;
        View = new PlotCanvasView(path, novelDir) { Dock = DockStyle.Fill };
        View.OpenRequested += p => OpenRequested?.Invoke(p);
        View.ZoomChanged += SyncZoom;
        View.AddDocRequested += at => ShowPicker(at);

        var f = new Font(ff, 9.5f);
        addDoc = new PillButton("에피소드 · 위키 추가") { Glyph = "", Font = f };
        addMemo = new PillButton("메모") { Glyph = "", Font = f };
        addGroup = new PillButton("묶음") { Glyph = "", Font = f };
        zoomOut = new PillButton("−") { Font = f, Compact = true };
        zoomIn = new PillButton("+") { Font = f, Compact = true };
        fit = new PillButton("전체 보기") { Font = f, Compact = true };
        zoomLabel.Font = f;
        addDoc.Click += (_, _) => ShowPicker(addDoc.PointToScreen(new Point(0, addDoc.Height + 4)));
        addMemo.Click += (_, _) => { View.AddMemo(); };
        addGroup.Click += (_, _) => View.AddGroupBox();
        zoomOut.Click += (_, _) => View.ZoomBy(1 / 1.2f);
        zoomIn.Click += (_, _) => View.ZoomBy(1.2f);
        fit.Click += (_, _) => View.FitAll();
        zoomLabel.Click += (_, _) => View.ResetZoom();
        bar.Dock = DockStyle.Top;
        bar.Controls.AddRange(new Control[] { addDoc, addMemo, addGroup, zoomOut, zoomLabel, zoomIn, fit });
        bar.Resize += (_, _) => LayoutBar();
        bar.Paint += (_, e) => { if (palette != null) using (var pen = new Pen(palette.Line)) e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
        Controls.Add(View);
        Controls.Add(bar);
        SyncZoom();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); bar.Height = S(48); LayoutBar(); }

    private void LayoutBar()
    {
        int x = S(12), h = S(32), y = (bar.Height - h) / 2;
        foreach (var b in new[] { addDoc, addMemo, addGroup }) { var ps = b.GetPreferredSize(Size.Empty); b.SetBounds(x, y, ps.Width, h); x += ps.Width + S(6); }
        int r = bar.Width - S(12);
        var fs = fit.GetPreferredSize(Size.Empty); fit.SetBounds(r - fs.Width, y, fs.Width, h); r -= fs.Width + S(10);
        zoomIn.SetBounds(r - h, y, h, h); r -= h + S(2);
        zoomLabel.SetBounds(r - S(52), y, S(52), h); r -= S(52) + S(2);
        zoomOut.SetBounds(r - h, y, h, h);
        bool room = r - h > x; // 좁으면 확대 버튼들을 숨긴다
        zoomOut.Visible = zoomIn.Visible = zoomLabel.Visible = room;
        fit.Visible = bar.Width - S(12) - fs.Width > x;
    }

    private void SyncZoom() => zoomLabel.Text = $"{Math.Round(View.Zoom * 100)}%";

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = bar.BackColor = p.Main;
        zoomLabel.BackColor = p.Main; zoomLabel.ForeColor = p.Dim;
        foreach (var b in new[] { addDoc, addMemo, addGroup, fit, zoomOut, zoomIn }) b.SetPalette(p);
        View.ApplyTheme(p);
        bar.Invalidate();
    }

    public void Save() => View.Flush();

    /// <summary>칸 머리의 ··· : 내보내기 · 가져오기 · 되돌리기 · 설정.</summary>
    public void ShowMenuAt(Control anchor, Point at)
    {
        var m = new ContextMenuStrip { Font = new Font(NovelStore.UiFontFamily, 9.5f) };
        var export = new ToolStripMenuItem("캔버스 내보내기");
        export.DropDownItems.Add("PNG 그림", null, (_, _) => View.Export("png"));
        export.DropDownItems.Add("JPG 그림 (작은 파일)", null, (_, _) => View.Export("jpg"));
        export.DropDownItems.Add("SVG (확대해도 선명)", null, (_, _) => View.Export("svg"));
        export.DropDownItems.Add("JSON Canvas (.canvas)", null, (_, _) => View.Export("json"));
        m.Items.Add(export);
        m.Items.Add("캔버스 가져오기 (JSON Canvas · 옵시디언 .canvas)…", null, (_, _) => View.Import());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("되돌리기 (Ctrl+Z)", null, (_, _) => View.Undo());
        m.Items.Add("다시 하기 (Ctrl+Y)", null, (_, _) => View.Redo());
        m.Items.Add("전체 보기", null, (_, _) => View.FitAll());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("정렬 가이드 스냅", null, (_, _) => CanvasStore.Snap = !CanvasStore.Snap) { Checked = CanvasStore.Snap });
        m.Items.Add(new ToolStripMenuItem("클릭으로 드래그 (빈 곳을 끌면 화면 이동)", null, (_, _) => CanvasStore.ClickDrag = !CanvasStore.ClickDrag) { Checked = CanvasStore.ClickDrag });
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(anchor, at);
    }

    /// <summary>에피소드 · 위키를 찾아 캔버스에 놓는 목록(검색칸 + 목록, Enter · 두 번 누르기로 추가).</summary>
    private void ShowPicker(Point screen)
    {
        picker?.Dispose();
        var p = palette ?? LibraryPalette.From(Themes.All[0]);
        var items = new List<(string Label, string Path)>();
        try { foreach (var ep in NovelStore.Episodes(novelDir)) items.Add(("에피소드 · " + Path.GetFileNameWithoutExtension(ep), ep)); } catch { }
        try { foreach (var w in WikiStore.List(novelDir)) items.Add(($"위키 · {WikiCategory.Find(w.Category).Label} · {w.Title}", w.Path)); } catch { }

        var font = new Font(NovelStore.UiFontFamily, 10f);
        var host = new Panel { Size = new Size(S(340), S(360)), BackColor = p.Card, Padding = new Padding(S(8)) };
        var box = new TextBox { Dock = DockStyle.Top, Font = font, PlaceholderText = "에피소드 · 위키 검색", BackColor = p.Main, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle };
        var list = new ListBox { Dock = DockStyle.Fill, Font = font, BorderStyle = BorderStyle.None, BackColor = p.Card, ForeColor = p.Fore, IntegralHeight = false, ItemHeight = S(26), DrawMode = DrawMode.OwnerDrawFixed };
        var gap = new Panel { Dock = DockStyle.Top, Height = S(6), BackColor = p.Card };
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bb = new SolidBrush(sel ? p.AccentSoft : p.Card)) e.Graphics.FillRectangle(bb, e.Bounds);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]!.ToString(), font, Rectangle.Inflate(e.Bounds, -S(6), 0), p.Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
        List<(string Label, string Path)> shown = items;
        void Fill()
        {
            string q = box.Text.Trim();
            shown = items.Where(i => q.Length == 0 || i.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            list.BeginUpdate(); list.Items.Clear();
            foreach (var i in shown) list.Items.Add(i.Label);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
        }
        var dd = picker = new ToolStripDropDown { Padding = Padding.Empty, AutoSize = true, BackColor = p.Card };
        void Choose()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
            View.AddFile(shown[list.SelectedIndex].Path);
            dd.Close();
            View.Focus();
        }
        box.TextChanged += (_, _) => Fill();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && list.SelectedIndex < list.Items.Count - 1) { list.SelectedIndex++; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up && list.SelectedIndex > 0) { list.SelectedIndex--; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Choose(); }
        };
        list.DoubleClick += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Choose(); } };
        host.Controls.Add(list);
        host.Controls.Add(gap);
        host.Controls.Add(box);
        Fill();
        dd.Items.Add(new ToolStripControlHost(host) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = host.Size });
        dd.Closed += (_, _) => BeginInvoke(() => { if (picker == dd) picker = null; dd.Dispose(); });
        dd.Show(screen);
        box.Focus();
    }
}
