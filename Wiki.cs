using System.Drawing.Drawing2D;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace CoffeeViewer;

/// <summary>
/// 위키 문서 한 개 = 작품 폴더 안의 <c>wiki\제목.txt</c>. 메모장으로 열어도 그대로 읽히는 평범한 글이다.
/// 구분선(────) 위는 속성(카테고리 · 설명 · 분류 · 중요도 · 다른 이름, 그 밖의 "이름: 값"은 모두 속성 상자 항목),
/// 아래는 본문. 본문의 항목은 "1. 제목", 부항목은 "1-1. 제목"으로 적는다(저장할 때 번호를 다시 매긴다).
/// <code>
/// 카테고리: 등장인물
/// 설명: 카페 주인. 밤에만 문을 연다
/// 분류: 주연
/// 다른 이름: 사장님
/// 나이: 29
/// ────────────
/// 1. 성격
/// 말수가 적다.
///
/// 1-1. 버릇
/// 컵을 세 번 닦는다.
/// </code>
/// </summary>
internal sealed class WikiDoc
{
    public const string Separator = "────────────";
    private static readonly string[] FixedKeys = { "카테고리", "설명", "분류", "중요도", "다른 이름" };

    public string Path { get; set; } = "";
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Category { get; set; } = "none";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Importance { get; set; } = 1;
    public List<string> Aliases { get; set; } = new();
    public List<(string Key, string Value)> Attributes { get; set; } = new();
    /// <summary>본문(번호 붙은 항목 줄 포함, 파일에 적히는 그대로). 줄바꿈은 \r\n.</summary>
    public string Body { get; set; } = "";

    private static bool IsSeparator(string line) { string l = line.Trim(); return l.Length >= 3 && l.All(c => c == '─' || c == '━'); }

    public static WikiDoc Parse(string path, string text)
    {
        var d = new WikiDoc { Path = path };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int sep = Array.FindIndex(lines, IsSeparator);
        if (sep >= 0)
        {
            for (int i = 0; i < sep; i++)
            {
                string raw = lines[i];
                int c = raw.IndexOfAny(new[] { ':', '：' });
                if (c <= 0) continue;
                string key = raw[..c].Trim(), val = raw[(c + 1)..].Trim();
                switch (key)
                {
                    case "카테고리": d.Category = WikiCategory.Find(val).Key; break;
                    case "설명": d.Summary = val.Replace("\\n", "\n"); break;
                    case "분류": d.Tags = SplitList(val); break;
                    case "중요도": if (int.TryParse(val, out int imp)) d.Importance = Math.Clamp(imp, 1, 5); break;
                    case "다른 이름": d.Aliases = SplitList(val); break;
                    default: d.Attributes.Add((key, val)); break;
                }
            }
            d.Body = string.Join("\r\n", lines[(sep + 1)..]);
        }
        else d.Body = string.Join("\r\n", lines);
        return d;
    }

    private static List<string> SplitList(string s) => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("카테고리: ").Append(WikiCategory.Find(Category).Label).Append("\r\n");
        if (Summary.Length > 0) sb.Append("설명: ").Append(Summary.Replace("\r", "").Replace("\n", "\\n")).Append("\r\n");
        if (Tags.Count > 0) sb.Append("분류: ").Append(string.Join(", ", Tags)).Append("\r\n");
        if (Importance != 1) sb.Append("중요도: ").Append(Importance).Append("\r\n");
        if (Aliases.Count > 0) sb.Append("다른 이름: ").Append(string.Join(", ", Aliases)).Append("\r\n");
        foreach (var (k, v) in Attributes)
        {
            string key = k.Replace(":", " ").Replace("：", " ").Trim();
            if (key.Length == 0) continue;
            if (FixedKeys.Contains(key)) key += " "; // 고정 속성 이름과 겹치면 속성 상자 항목으로 남도록
            sb.Append(key).Append(": ").Append(v.Replace("\r", "").Replace("\n", " ")).Append("\r\n");
        }
        sb.Append(Separator).Append("\r\n");
        sb.Append(Body);
        return sb.ToString();
    }

    /// <summary>본문 한 줄이 항목 · 부항목인지: "3. 제목" → (2, "제목"), "3-1. 제목" → (3, "제목"), 아니면 (0, 줄 그대로).</summary>
    public static (int Level, string Text) HeadingOf(string line)
    {
        var m = HeadingRegex.Match(line);
        if (!m.Success) return (0, line);
        return (m.Groups[2].Success ? 3 : 2, m.Groups[3].Value);
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingRegex =
        new(@"^(\d{1,3})(-\d{1,3})?\.\s+(\S.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>위키 카테고리(Muvel과 같은 여덟 가지). 글리프는 Segoe Fluent/MDL2 아이콘.</summary>
internal sealed record WikiCategory(string Key, string Label, string Glyph)
{
    public static readonly WikiCategory[] All =
    {
        new("character", "등장인물", "\uE77B"),
        new("event", "사건", "\uE823"),
        new("group", "단체", "\uE716"),
        new("place", "장소", "\uE707"),
        new("item", "아이템", "\uE7B8"),
        new("world", "세계관 설정", "\uE909"),
        new("story", "스토리", "\uE736"),
        new("none", "미분류", "\uE712"),
    };

    public static WikiCategory Find(string? keyOrLabel) =>
        All.FirstOrDefault(c => string.Equals(c.Key, keyOrLabel, StringComparison.OrdinalIgnoreCase) || c.Label == keyOrLabel) ?? All[^1];
}

internal static class WikiStore
{
    public static string Dir(string novelDir) => Path.Combine(novelDir, "wiki");

    public static List<WikiDoc> List(string novelDir)
    {
        var dir = Dir(novelDir);
        if (!Directory.Exists(dir)) return new();
        var docs = new List<WikiDoc>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.txt"))
            {
                if (Path.GetFileName(f).StartsWith('.')) continue;
                try { docs.Add(Load(f)); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return docs.OrderBy(d => d.Importance).ThenBy(d => d.Title, NaturalStringComparer.Instance).ToList();
    }

    public static WikiDoc Load(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) text = sr.ReadToEnd();
        return WikiDoc.Parse(path, text);
    }

    public static void Save(WikiDoc d) => File.WriteAllText(d.Path, d.Serialize(), new UTF8Encoding(false));

    public static WikiDoc Create(string novelDir, string title, string category, string summary, List<string> tags)
    {
        string dir = Dir(novelDir);
        Directory.CreateDirectory(dir);
        string name = title.Trim();
        string path = Path.Combine(dir, name + ".txt");
        if (File.Exists(path)) throw new IOException($"같은 이름의 위키 문서가 이미 있어요: {name}");
        var d = new WikiDoc { Path = path, Category = category, Summary = summary.Trim(), Tags = tags };
        Save(d);
        return d;
    }

    public static string Rename(WikiDoc d, string newTitle)
    {
        string target = Path.Combine(Path.GetDirectoryName(d.Path)!, newTitle.Trim() + ".txt");
        if (string.Equals(target, d.Path, StringComparison.Ordinal)) return d.Path;
        if (!string.Equals(target, d.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) throw new IOException($"같은 이름의 위키 문서가 이미 있어요: {newTitle}");
        File.Move(d.Path, target);
        d.Path = target;
        return target;
    }

    public static void Delete(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    /// <summary>[+ 새 위키 문서] 창을 띄워 만든다. 만든 문서 경로(취소하면 null).</summary>
    public static string? AskCreate(IWin32Window? owner, string novelDir)
    {
        using var dlg = new WikiCreateDialog();
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        try { return Create(novelDir, dlg.TitleText, dlg.CategoryKey, dlg.SummaryText, dlg.Tags).Path; }
        catch (Exception ex) { MessageBox.Show(owner, $"위키 문서를 만들지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
    }
}

/// <summary>Muvel의 "새 위키 문서" 창: 제목 · 설명 · 카테고리 · 분류 · [만들기].</summary>
internal sealed class WikiCreateDialog : Form
{
    private readonly TextBox title, summary, tags;
    private readonly ComboBox category;

    public string TitleText => title.Text.Trim();
    public string SummaryText => summary.Text.Trim();
    public string CategoryKey => WikiCategory.All[Math.Max(0, category.SelectedIndex)].Key;
    public List<string> Tags => tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    public WikiCreateDialog(string? initialCategory = null)
    {
        Text = "새 위키 문서";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        TopMost = true; // 앱 뒤로 숨어 앱이 먹통처럼 보이지 않게 늘 앱 위에 뜬다
        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill }; // Dock해야 폼 Padding(여백)이 왼쪽 · 위에도 적용된다
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(0, 0, 0, 4), Font = new Font(Font, FontStyle.Bold) };

        title = new TextBox { Width = 440, PlaceholderText = "문서 제목 (예: 등장인물 이름)", Margin = new Padding(0, 0, 0, 12) };
        summary = new TextBox { Width = 440, Height = 70, Multiline = true, AcceptsReturn = true, PlaceholderText = "어떤 문서인지 한두 줄로 적어 주세요", Margin = new Padding(0, 0, 0, 12) };
        category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 0, 16, 12) };
        foreach (var c in WikiCategory.All) category.Items.Add(c.Label);
        category.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == (initialCategory ?? "none"));
        tags = new TextBox { Width = 224, PlaceholderText = "쉼표로 여러 개", Margin = new Padding(0, 0, 0, 12) };

        root.Controls.Add(L("제목"), 0, 0); root.SetColumnSpan(root.GetControlFromPosition(0, 0)!, 2);
        root.Controls.Add(title, 0, 1); root.SetColumnSpan(title, 2);
        root.Controls.Add(L("설명"), 0, 2); root.SetColumnSpan(root.GetControlFromPosition(0, 2)!, 2);
        root.Controls.Add(summary, 0, 3); root.SetColumnSpan(summary, 2);
        root.Controls.Add(L("카테고리"), 0, 4);
        root.Controls.Add(L("분류"), 1, 4);
        root.Controls.Add(category, 0, 5);
        root.Controls.Add(tags, 1, 5);

        var ok = new Button { Text = "✓  만들기", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 32) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(84, 32) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (!NovelStore.ValidName(title.Text))
            {
                MessageBox.Show(this, "제목이 비어 있거나 쓸 수 없는 글자(\\ / : * ? \" < > |)가 들어 있어요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        Shown += (_, _) => title.Focus();
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}

/// <summary>
/// 위키 목록(Muvel의 "카테고리별 보기"): 카테고리 머리 줄(아이콘 · 이름 · 개수) 밑에 문서 줄(제목 · 설명).
/// 문서 줄을 누르면 DocChosen, 우클릭하면 MenuRequested.
/// </summary>
internal sealed class WikiList : Control
{
    private readonly List<(WikiCategory? Header, WikiDoc? Doc, int Count)> rows = new();
    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, Visible = false };
    private LibraryPalette? palette;
    private int hover = -1;
    private string? current;

    public event Action<string>? DocChosen;
    public event Action<string, Point>? MenuRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OnMain { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Current { get => current; set { current = value; Invalidate(); } }

    public WikiList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Controls.Add(scroll);
        scroll.ValueChanged += (_, _) => Invalidate();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private Color Surface => palette == null ? BackColor : OnMain ? palette.Main : palette.Side;
    private int RowH(int i) => rows[i].Header != null ? Font.Height + S(i == 0 ? 14 : 24) : Font.Height + S(OnMain ? 24 : 16);
    private int Offset => scroll.Visible ? scroll.Value : 0;

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = Surface; Invalidate(); }

    public void SetDocs(List<WikiDoc> docs)
    {
        rows.Clear();
        foreach (var cat in WikiCategory.All)
        {
            var inCat = docs.Where(d => d.Category == cat.Key).ToList();
            if (inCat.Count == 0) continue;
            rows.Add((cat, null, inCat.Count));
            foreach (var d in inCat) rows.Add((null, d, 0));
        }
        SyncScroll();
        Invalidate();
    }

    private int ContentHeight() { int h = S(4); for (int i = 0; i < rows.Count; i++) h += RowH(i); return h + S(8); }
    /// <summary>목록을 스크롤 없이 다 펼치는 데 필요한 높이(쪽 전체가 스크롤될 때).</summary>
    public int PreferredHeight => ContentHeight() + S(8);

    private void SyncScroll()
    {
        int content = ContentHeight();
        bool need = content > ClientSize.Height && ClientSize.Height > 0;
        scroll.Visible = need;
        if (need) { scroll.Maximum = content; scroll.LargeChange = Math.Max(1, ClientSize.Height); scroll.SmallChange = S(30); }
        else if (scroll.Value != 0) scroll.Value = 0;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    private int IndexAt(int y)
    {
        int top = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++) { int h = RowH(i); if (y >= top && y < top + h) return i; top += h; }
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!scroll.Visible) return;
        scroll.Value = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * S(90), 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i >= 0 && rows[i].Doc == null) i = -1;
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Y);
        if (i < 0 || rows[i].Doc is not WikiDoc d) return;
        if (e.Button == MouseButtons.Left) DocChosen?.Invoke(d.Path);
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(d.Path, PointToScreen(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Surface);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width - (scroll.Visible ? scroll.Width : 0), pad = S(10);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using var iconFont = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.95f);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var bold = new Font(Font, FontStyle.Bold);
        int y = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++)
        {
            int h = RowH(i);
            if (y + h >= 0 && y <= ClientSize.Height)
            {
                var (header, doc, count) = rows[i];
                if (header != null)
                {
                    var r = new Rectangle(pad + S(6), y + h - Font.Height - S(6), w - pad * 2, Font.Height + S(4));
                    TextRenderer.DrawText(g, header.Glyph, iconFont, new Rectangle(r.X, r.Y, S(20), r.Height), p.Accent, flags);
                    string label = $"{header.Label}";
                    int lw = TextRenderer.MeasureText(label, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, label, small, new Rectangle(r.X + S(24), r.Y, lw + 2, r.Height), p.Dim, flags);
                    TextRenderer.DrawText(g, count.ToString(), small, new Rectangle(r.X + S(24) + lw + S(6), r.Y, S(40), r.Height), Themes.Blend(Surface, p.Dim, 0.6f), flags);
                }
                else if (doc != null)
                {
                    var r = new Rectangle(pad, y + S(1), w - pad * 2, h - S(2));
                    bool cur = string.Equals(doc.Path, current, StringComparison.OrdinalIgnoreCase);
                    if (cur || i == hover)
                    {
                        using var path = LibraryPalette.Rounded(r, S(8));
                        using var b = new SolidBrush(cur ? p.AccentSoft : OnMain ? Themes.Blend(p.Main, p.Fore, 0.05f) : p.CardHover);
                        g.FillPath(b, path);
                    }
                    int tx = r.X + S(16);
                    int tw = Math.Min(TextRenderer.MeasureText(doc.Title, bold, Size.Empty, TextFormatFlags.NoPadding).Width, r.Right - tx - S(8));
                    TextRenderer.DrawText(g, doc.Title, cur ? bold : Font, new Rectangle(tx, r.Y, tw + S(4), r.Height), p.Fore, flags);
                    if (doc.Summary.Length > 0 && r.Right - (tx + tw + S(14)) > S(30))
                        TextRenderer.DrawText(g, doc.Summary.Replace("\n", " "), small, new Rectangle(tx + tw + S(14), r.Y, r.Right - (tx + tw + S(14)) - S(8), r.Height), p.Dim, flags);
                }
            }
            y += h;
        }
        if (rows.Count == 0)
            TextRenderer.DrawText(g, "아직 위키 문서가 없어요.\n[+ 새 위키 문서]로 인물 · 장소 · 설정을 적어 두세요.", Font, new Rectangle(0, S(16), w, S(80)), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
