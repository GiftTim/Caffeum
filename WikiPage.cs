using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CoffeeViewer;

/// <summary>
/// 위키 문서 화면(Muvel의 위키 문서처럼):
/// [‹ 돌아가기]                                         [⋯]
/// 작품 › 등장인물 › 이민혁
/// (●) 이민혁                     ← 카테고리 아이콘이 든 동그라미 + 큰 제목
/// ⌄ 문서 속성 펼치기             ← 카테고리 · 중요도 · 설명 · 분류 · 다른 이름
/// [목차 ─ ## 제목들]  [속성 ─ 키 값 · + 속성 추가하기]
/// 본문(## · ### 로 시작하는 줄이 목차가 된다)
/// 고치면 잠깐 뒤 저절로 저장한다.
/// </summary>
internal sealed class WikiPage : Panel
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;

    private readonly Panel topBar, head;
    private readonly PillButton backButton, moreButton;
    private readonly Label crumbLabel, titleLabel, propsToggle;
    private readonly TableLayoutPanel props;
    private readonly ComboBox categoryBox;
    private readonly NumericUpDown importanceBox;
    private readonly TextBox summaryBox, tagsBox, aliasBox;
    private readonly TocBox toc;
    private readonly AttrBox attrs;
    private readonly HeadingEditor body;
    private readonly Label bodyHint;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1000 };
    private LibraryPalette? palette;
    private WikiDoc? doc;
    private string novelName = "";
    private bool loading, propsOpen;

    public event Action? BackRequested;
    /// <summary>[‹ 돌아가기] · [⋯] 줄을 보일지(워크스페이스 칸 안에서는 칸 머리가 그 일을 한다).</summary>
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowTopBar { get => topBar.Visible; set => topBar.Visible = value; }
    /// <summary>칸 머리의 ⋯에서 부르는 문서 메뉴(이름 바꾸기 · 탐색기 · 휴지통).</summary>
    public void ShowMenuAt(Control anchor, Point at) { if (doc == null) return; ShowMoreMenu(); moreMenu?.Show(anchor, at); }
    /// <summary>저장했거나 이름 · 카테고리가 바뀌었다(목록을 다시 그리라고).</summary>
    public event Action? Changed;
    public string? DocPath => doc?.Path;

    public WikiPage()
    {
        string ff = NovelStore.UiFontFamily;
        DoubleBuffered = true;

        backButton = new PillButton("‹  돌아가기") { Compact = true, Font = new Font(ff, 9.5f) };
        backButton.Click += (_, _) => BackRequested?.Invoke();
        moreButton = new PillButton("⋯") { Compact = true, Font = new Font(ff, 10f, FontStyle.Bold) };
        moreButton.Click += (_, _) => ShowMoreMenu();
        topBar = new Panel { Dock = DockStyle.Top };
        topBar.Controls.AddRange(new Control[] { backButton, moreButton });
        topBar.Layout += (_, _) =>
        {
            topBar.Height = backButton.Height + S(20);
            backButton.Location = new Point(S(16), S(10));
            moreButton.Location = new Point(topBar.ClientSize.Width - moreButton.Width - S(16), S(10));
        };

        crumbLabel = new Label { AutoSize = true, Font = new Font(ff, 9.5f), UseMnemonic = false };
        titleLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = new Font(ff, 24f, FontStyle.Bold), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        titleLabel.DoubleClick += (_, _) => Rename();
        propsToggle = new Label { AutoSize = true, Font = new Font(ff, 9.5f), Cursor = Cursors.Hand, UseMnemonic = false };
        propsToggle.Click += (_, _) => { propsOpen = !propsOpen; SyncPropsToggle(); head.PerformLayout(); };

        categoryBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Font = new Font(ff, 9.5f) };
        foreach (var c in WikiCategory.All) categoryBox.Items.Add(c.Label);
        importanceBox = new NumericUpDown { Minimum = 1, Maximum = 5, Width = 64, Font = new Font(ff, 9.5f) };
        summaryBox = new TextBox { Font = new Font(ff, 9.5f), Dock = DockStyle.Fill };
        tagsBox = new TextBox { Font = new Font(ff, 9.5f), Dock = DockStyle.Fill, PlaceholderText = "쉼표로 여러 개" };
        aliasBox = new TextBox { Font = new Font(ff, 9.5f), Dock = DockStyle.Fill, PlaceholderText = "별명 · 다른 표기, 쉼표로 여러 개" };
        props = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Visible = false };
        props.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        props.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Label PL(string s) => new() { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6), Font = new Font(ff, 9.5f), UseMnemonic = false };
        props.Controls.Add(PL("카테고리"), 0, 0); props.Controls.Add(categoryBox, 1, 0);
        props.Controls.Add(PL("중요도"), 2, 0); props.Controls.Add(importanceBox, 3, 0);
        props.Controls.Add(PL("설명"), 0, 1); props.Controls.Add(summaryBox, 1, 1); props.SetColumnSpan(summaryBox, 3);
        props.Controls.Add(PL("분류"), 0, 2); props.Controls.Add(tagsBox, 1, 2); props.SetColumnSpan(tagsBox, 3);
        props.Controls.Add(PL("다른 이름"), 0, 3); props.Controls.Add(aliasBox, 1, 3); props.SetColumnSpan(aliasBox, 3);
        categoryBox.SelectedIndexChanged += (_, _) => { if (!loading && doc != null) { doc.Category = WikiCategory.All[Math.Max(0, categoryBox.SelectedIndex)].Key; SyncCrumb(); head.Invalidate(); SaveSoon(); } };
        importanceBox.ValueChanged += (_, _) => { if (!loading && doc != null) { doc.Importance = (int)importanceBox.Value; SaveSoon(); } };
        summaryBox.TextChanged += (_, _) => { if (!loading && doc != null) { doc.Summary = summaryBox.Text.Trim(); SaveSoon(); } };
        tagsBox.TextChanged += (_, _) => { if (!loading && doc != null) { doc.Tags = Split(tagsBox.Text); SaveSoon(); } };
        aliasBox.TextChanged += (_, _) => { if (!loading && doc != null) { doc.Aliases = Split(aliasBox.Text); SaveSoon(); } };

        toc = new TocBox();
        toc.HeadingChosen += offset => { body.Focus(); body.Select(Math.Min(offset, body.TextLength), 0); body.ScrollToCaret(); };
        attrs = new AttrBox();
        attrs.AddRequested += () => EditAttribute(-1);
        attrs.EditRequested += EditAttribute;
        attrs.RemoveRequested += i => { if (doc == null) return; doc.Attributes.RemoveAt(i); attrs.SetItems(doc.Attributes); head.PerformLayout(); SaveSoon(); };

        head = new Panel { Dock = DockStyle.Top };
        head.Controls.AddRange(new Control[] { crumbLabel, titleLabel, propsToggle, props, toc, attrs });
        head.Layout += (_, _) => LayoutHead();
        head.Paint += PaintHead;

        // 본문: "## " · "### "를 치면 그 줄이 바로 제목으로 바뀌는 글 상자(파일에는 ## · ###로 저장)
        body = new HeadingEditor { Dock = DockStyle.Fill };
        body.HeadingsChanged += heads => { if (!loading) SetToc(heads); }; // 항목을 넣고 빼면 목차 번호도 바로
        body.ContentChanged += () => { if (loading || doc == null) return; bodyHint.Visible = body.TextLength == 0; SaveSoon(); };
        body.HandleCreated += (_, _) => ApplyBodyMargin();
        body.SizeChanged += (_, _) => ApplyBodyMargin();
        bodyHint = new Label { Text = "## 나 ### 를 치고 띄어 쓰면 항목 · 부항목이 돼요. 위의 목차에 번호와 함께 모여요.", AutoSize = true, UseMnemonic = false, Cursor = Cursors.IBeam, Font = new Font(ff, 11f) };
        bodyHint.Click += (_, _) => body.Focus();
        body.Controls.Add(bodyHint);

        Controls.Add(body);
        Controls.Add(head);
        Controls.Add(topBar);

        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        Disposed += (_, _) => saveTimer.Dispose();
        VisibleChanged += (_, _) => { if (!Visible) Save(); };
        SyncPropsToggle();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int ColW => Math.Min(ClientSize.Width - S(48) * 2, S(800));
    private int ColLeft => Math.Max(S(24), (ClientSize.Width - ColW) / 2);

    private static List<string> Split(string s) => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private void SyncPropsToggle() { propsToggle.Text = propsOpen ? "⌃  문서 속성 접기" : "⌄  문서 속성 펼치기"; props.Visible = propsOpen; }

    private void SyncCrumb()
    {
        if (doc == null) return;
        crumbLabel.Text = $"{novelName}  ›  {WikiCategory.Find(doc.Category).Label}  ›  {doc.Title}";
    }

    private void LayoutHead()
    {
        int x = ColLeft, w = Math.Max(S(200), ColW), y = S(14);
        crumbLabel.Location = new Point(x, y);
        y = crumbLabel.Bottom + S(14);
        int circle = S(56);
        titleLabel.SetBounds(x + circle + S(18), y, w - circle - S(18), circle);
        y += circle + S(16);
        propsToggle.Location = new Point(x, y);
        y = propsToggle.Bottom + S(10);
        if (props.Visible) { props.SetBounds(x, y, w, props.PreferredSize.Height); props.Width = w; y = props.Bottom + S(10); }
        y += S(16);
        int half = (w - S(24)) / 2;
        if (half >= S(240))
        {
            toc.SetBounds(x, y, half, toc.PreferredHeight(half));
            attrs.SetBounds(x + half + S(24), y, half, attrs.PreferredHeight(half));
            y = Math.Max(toc.Bottom, attrs.Bottom) + S(24);
        }
        else
        {
            // 칸이 좁으면(워크스페이스에서 나눠 볼 때) 목차 밑에 속성 상자를 쌓는다
            toc.SetBounds(x, y, w, toc.PreferredHeight(w));
            attrs.SetBounds(x, toc.Bottom + S(12), w, attrs.PreferredHeight(w));
            y = attrs.Bottom + S(24);
        }
        if (head.Height != y) head.Height = y;
    }

    private void PaintHead(object? sender, PaintEventArgs e)
    {
        var p = palette;
        if (p == null || doc == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int circle = S(56);
        var r = new Rectangle(ColLeft, titleLabel.Top, circle, circle);
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.14f : 0.08f))) g.FillEllipse(b, r);
        using var f = new Font(MainForm.IconFontName(), 18f);
        TextRenderer.DrawText(g, WikiCategory.Find(doc.Category).Glyph, f, r, Themes.Blend(p.Main, p.Fore, 0.6f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void ApplyBodyMargin()
    {
        body.SetColumnWidth(ColW);
        bodyHint.Location = new Point(body.SideMargin, 2);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); head?.PerformLayout(); head?.Invalidate(); }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = topBar.BackColor = head.BackColor = p.Main;
        foreach (var l in new[] { crumbLabel, titleLabel, propsToggle }) l.BackColor = p.Main;
        props.BackColor = p.Main;
        foreach (Control c in props.Controls) if (c is Label pl) { pl.BackColor = p.Main; pl.ForeColor = p.Dim; }
        crumbLabel.ForeColor = propsToggle.ForeColor = p.Dim;
        titleLabel.ForeColor = p.Fore;
        bool recolor = body.ForeColor != p.Fore && doc != null;
        body.BackColor = bodyHint.BackColor = p.Main;
        body.ForeColor = p.Fore;
        if (recolor) { loading = true; try { body.LoadText(body.ToText(out _)); } finally { loading = false; } } // 이미 쓴 글자 색도 새 테마로
        bodyHint.ForeColor = Themes.Blend(p.Main, p.Fore, 0.4f);
        backButton.SetPalette(p);
        moreButton.SetPalette(p);
        toc.SetPalette(p);
        attrs.SetPalette(p);
        head.Invalidate();
    }

    /// <summary>위키 문서를 연다(고치던 문서가 있으면 먼저 저장).</summary>
    public void Open(string path, string novelDir)
    {
        Save();
        try { doc = WikiStore.Load(path); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"위키 문서를 열지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); doc = null; return; }
        novelName = Path.GetFileName(novelDir);
        loading = true;
        try
        {
            titleLabel.Text = doc.Title;
            SyncCrumb();
            categoryBox.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == doc.Category);
            importanceBox.Value = Math.Clamp(doc.Importance, 1, 5);
            summaryBox.Text = doc.Summary;
            tagsBox.Text = string.Join(", ", doc.Tags);
            aliasBox.Text = string.Join(", ", doc.Aliases);
            body.LoadText(doc.Body);
            bodyHint.Visible = body.TextLength == 0;
            attrs.SetItems(doc.Attributes);
            body.ToText(out var heads);
            SetToc(heads);
        }
        finally { loading = false; }
        head.PerformLayout();
        head.Invalidate();
        body.ScrollToTop();
        body.Focus();
        // 화면에 자리 잡은 뒤에도 한 번 더(처음 열 때 맨 아래로 내려가 있던 것). 창이 아직 없으면 생길 때.
        // 화면 배치(목차 · 속성 상자 높이)가 다 잡힌 뒤 글 상자가 아래로 밀려 있는 일이 있어, 잠깐 뒤에 한 번 더 맨 위로(그새 커서를 옮겼으면 그대로 둔다).
        var settle = new System.Windows.Forms.Timer { Interval = 300 };
        int ticks = 0;
        settle.Tick += (_, _) => { if (++ticks >= 3 || body.SelectionStart != 0) { settle.Dispose(); return; } if (body.IsHandleCreated) body.ScrollToTop(); };
        settle.Start();
    }

    private void SetToc(List<(int, string, int)> heads)
    {
        int before = toc.Count;
        toc.SetItems(heads);
        if (toc.Count != before) head.PerformLayout();
    }

    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    /// <summary>고친 내용을 파일에 쓴다.</summary>
    public void Save()
    {
        saveTimer.Stop();
        if (doc == null || loading) return;
        try
        {
            doc.Body = body.ToText(out var heads);
            SetToc(heads);
            string now = doc.Serialize();
            string? old = File.Exists(doc.Path) ? File.ReadAllText(doc.Path) : null;
            if (old == now) return;
            WikiStore.Save(doc);
            Changed?.Invoke();
        }
        catch { }
    }

    private void EditAttribute(int index)
    {
        if (doc == null) return;
        var (k, v) = index >= 0 ? doc.Attributes[index] : ("", "");
        using var dlg = new NovelInputDialog(index >= 0 ? "속성 고치기" : "속성 추가하기", "이름 (예: 나이, 소속, 능력)", k, "값", v, firstIsName: false);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.FirstValue.Length == 0) return;
        if (index >= 0) doc.Attributes[index] = (dlg.FirstValue, dlg.SecondValue.Replace("\r", "").Replace("\n", " "));
        else doc.Attributes.Add((dlg.FirstValue, dlg.SecondValue.Replace("\r", "").Replace("\n", " ")));
        attrs.SetItems(doc.Attributes);
        head.PerformLayout();
        SaveSoon();
    }

    private ContextMenuStrip? moreMenu;

    private void ShowMoreMenu()
    {
        if (doc == null) return;
        moreMenu?.Dispose();
        var m = moreMenu = new ContextMenuStrip();
        m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(Rename));
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{doc.Path}\""));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(Delete));
        if (topBar.Visible) m.Show(moreButton, new Point(moreButton.Width - m.PreferredSize.Width, moreButton.Height)); // 칸 안에서는 ShowMenuAt이 칸 머리 ⋯ 밑에 띄운다
    }

    private void Rename()
    {
        if (doc == null) return;
        Save();
        using var dlg = new NovelInputDialog("위키 문서 이름 바꾸기", "제목", doc.Title);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { WikiStore.Rename(doc, dlg.FirstValue); titleLabel.Text = doc.Title; SyncCrumb(); Changed?.Invoke(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"이름을 바꾸지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Delete()
    {
        if (doc == null) return;
        if (MessageBox.Show(FindForm(), $"'{doc.Title}' 위키 문서를 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        saveTimer.Stop();
        try { WikiStore.Delete(doc.Path); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"삭제하지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        doc = null;
        Changed?.Invoke();
        BackRequested?.Invoke();
    }

    /// <summary>둥근 테두리 상자(목차 · 속성)의 바탕 그리기.</summary>
    private abstract class BoxBase : Control
    {
        protected LibraryPalette? palette;
        protected BoxBase()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font(NovelStore.UiFontFamily, 9.5f);
        }
        public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
        protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        protected int RowH => Font.Height + S(10);
        protected void PaintFrame(Graphics g, LibraryPalette p, bool dashed)
        {
            g.Clear(Parent?.BackColor ?? p.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(10));
            using (var b = new SolidBrush(p.Dark ? Themes.Blend(p.Main, Color.White, 0.03f) : Color.White)) g.FillPath(b, path);
            using var pen = new Pen(dashed ? Themes.Blend(p.Line, p.Accent, 0.5f) : p.Line) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>목차 상자: "목차" 머리 밑에 ## · ### 제목들. 누르면 본문의 그 줄로.</summary>
    private sealed class TocBox : BoxBase
    {
        private List<(int Level, string Text, int Offset)> items = new();
        private int hover = -1;
        public event Action<int>? HeadingChosen;
        public int Count => items.Count;

        public TocBox() { }
        public void SetItems(List<(int, string, int)> list)
        {
            bool same = list.Count == items.Count && list.Zip(items).All(z => z.First.Equals(z.Second));
            if (same) return;
            items = list; Invalidate();
        }
        public int PreferredHeight(int width) => S(16) + RowH + Math.Max(2, items.Count) * RowH + S(10);

        private int IndexAt(int y) { int i = (y - S(16) - RowH) / RowH; return y < S(16) + RowH || i >= items.Count ? -1 : i; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Y); if (i != hover) { hover = i; Invalidate(); } Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = IndexAt(e.Y); if (i >= 0) HeadingChosen?.Invoke(items[i].Offset); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            if (p == null) return;
            var g = e.Graphics;
            PaintFrame(g, p, false);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int x = S(16), y = S(12), w = Width - S(32);
            using (var bf = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(g, "목차", bf, new Rectangle(x, y, w, RowH), p.Fore, flags);
            y += RowH + S(4);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "문서에 제목이 없어요", Font, new Rectangle(x, y, w, RowH), p.Dim, flags);
                using var sf = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
                TextRenderer.DrawText(g, "ⓘ  ## 제목, ### 소제목으로 나눠 보세요", sf, new Rectangle(x, y + RowH, w, RowH), p.Accent, flags);
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var (lvl, text, _) = items[i];
                int ix = x + (lvl == 3 ? S(16) : 0);
                TextRenderer.DrawText(g, text, Font, new Rectangle(ix, y, w - (ix - x), RowH), i == hover ? p.Accent : lvl == 3 ? p.Dim : p.Fore, flags);
                y += RowH;
            }
        }
    }

    /// <summary>속성 상자: "이름  값" 줄들 + [+ 속성 추가하기]. 줄을 누르면 고치기, 우클릭하면 지우기.</summary>
    private sealed class AttrBox : BoxBase
    {
        private List<(string Key, string Value)> items = new();
        private int hover = -1; // items.Count = "추가하기" 줄
        public event Action? AddRequested;
        public event Action<int>? EditRequested;
        public event Action<int>? RemoveRequested;

        public void SetItems(List<(string, string)> list) { items = list.ToList(); Invalidate(); }
        public int PreferredHeight(int width) => items.Count == 0 ? S(16) + RowH * 3 + S(10) : S(16) + RowH + items.Count * RowH + RowH + S(10);

        private int IndexAt(int y)
        {
            if (items.Count == 0) return items.Count; // 빈 상자는 어디를 눌러도 추가
            int i = (y - S(16) - RowH) / RowH;
            return y < S(16) + RowH ? -1 : Math.Min(i, items.Count);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Y); if (i != hover) { hover = i; Invalidate(); } Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int i = IndexAt(e.Y);
            if (i < 0) return;
            if (i == items.Count) { if (e.Button == MouseButtons.Left) AddRequested?.Invoke(); return; }
            if (e.Button == MouseButtons.Left) EditRequested?.Invoke(i);
            else if (e.Button == MouseButtons.Right && MessageBox.Show(FindForm(), $"'{items[i].Key}' 속성을 지울까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) RemoveRequested?.Invoke(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            if (p == null) return;
            var g = e.Graphics;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (items.Count == 0)
            {
                PaintFrame(g, p, true);
                using var big = new Font(Font.FontFamily, 14f);
                TextRenderer.DrawText(g, "+", big, new Rectangle(0, Height / 2 - S(26), Width, S(24)), p.Accent, flags | TextFormatFlags.HorizontalCenter);
                using var bf = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, "속성 추가하기", bf, new Rectangle(0, Height / 2, Width, S(22)), p.Accent, flags | TextFormatFlags.HorizontalCenter);
                return;
            }
            PaintFrame(g, p, false);
            int x = S(16), y = S(12), w = Width - S(32);
            using (var bf = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(g, "속성", bf, new Rectangle(x, y, w, RowH), p.Fore, flags);
            y += RowH + S(4);
            int keyW = Math.Min(w / 3, items.Max(it => TextRenderer.MeasureText(it.Key, Font, Size.Empty, TextFormatFlags.NoPadding).Width) + S(12));
            for (int i = 0; i < items.Count; i++)
            {
                if (i == hover)
                {
                    using var hb = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.05f));
                    using var hp = LibraryPalette.Rounded(new Rectangle(x - S(6), y, w + S(12), RowH), S(6));
                    g.FillPath(hb, hp);
                }
                TextRenderer.DrawText(g, items[i].Key, Font, new Rectangle(x, y, keyW, RowH), p.Dim, flags);
                TextRenderer.DrawText(g, items[i].Value, Font, new Rectangle(x + keyW, y, w - keyW, RowH), p.Fore, flags);
                y += RowH;
            }
            TextRenderer.DrawText(g, "+  속성 추가하기", Font, new Rectangle(x, y, w, RowH), hover == items.Count ? p.Accent : Themes.Blend(p.Main, p.Accent, 0.75f), flags);
        }
    }
}
