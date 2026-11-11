using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// Muvel식 글쓰기 화면: 볼트 창 안에서 바로 고치는 txt(회차)는 글을 창 가운데 일정한 폭의 칸에 놓고,
/// 그 위에 작은 "N편"과 큰 회차 제목을 단다. 글이 비어 있으면 "이야기를 시작해 보세요…"가 흐리게 보이고,
/// 오른쪽 아래에는 목표 글자 수까지의 진행을 보여 주는 "글자 수 세기" 위젯이 떠 있다.
/// 편집 용지(PaperSetup)를 고른 문서는 용지 폭을 그대로 따른다.
/// </summary>
public partial class MainForm
{
    /// <summary>글 칸의 폭(96 DPI · 100% 기준 px). 한 줄에 한글 40자 남짓.</summary>
    private const int WriterColumnWidth = 720;
    private WriterHeader? writerTitle;
    /// <summary>제목을 직접 고쳤을 때(파일 이름 바꾸기는 파일 트리 쪽에서 한다).</summary>
    public event Action<string, string>? TitleRenameRequested;
    /// <summary>유형 · 상태를 바꿨을 때(파일 트리 · 소설 메인 목록을 다시 읽게).</summary>
    public event Action? EpisodeMetaChanged;
    private bool writerPlaceholder; // 빈 글 안내("이야기를 시작해 보세요…")를 쓸 준비가 됐다 — 글 상자가 직접 그린다(PageLockTextBox.Placeholder)
    private CharGoalWidget? goalWidget;
    private string? writerKeyPath;
    private TallScroller? tall;

    private bool WriterLayout => embedded && directEdit && shownKind == ContentKind.Txt && editorHost.Visible && EffPaper == null;

    private void InitWriter()
    {
        if (!embedded) return;
        writerTitle = new WriterHeader { Dock = DockStyle.Top, Visible = false };
        tall = new TallScroller { Dock = DockStyle.Fill, Visible = false }; // 머리 + 글 상자를 한 장으로 같이 스크롤시킨다
        editorHost.Controls.Add(tall);
        goalWidget = new CharGoalWidget { Visible = false }; // 글자 수 세기 위젯: 사이드바의 아이콘으로 켜고 끈다
        editorHost.Controls.Add(goalWidget);
        goalWidget.SettingsRequested += OpenGoalSettings;
        goalWidget.CloseRequested += () => { goalShown = false; settings.WriterGoalWidget = false; settings.Save(); SyncGoalWidget(); };
        goalShown = settings.WriterGoalWidget; // 에피소드를 열면 위젯이 자동으로 켜진다(마지막에 닫아 두었다면 꺼진 채로)
        goalWidget.Moved += () => { goalMoved = true; goalPos = goalWidget.Location; };
        goalWidget.ExpandedChanged += () => { goalMoved = true; ResizeGoalWidget(); };
        editorHost.SizeChanged += (_, _) => { if (goalWidget.Visible) PlaceGoalWidget(); };
        FormClosed += (_, _) => { goalWidget.Parent?.Controls.Remove(goalWidget); };
        writerTitle.TypeChosen += OnEpisodeTypeChosen;
        writerTitle.StatusChosen += OnEpisodeStatusChosen;
        writerTitle.TitleChanged += t => { if (currentPath != null) TitleRenameRequested?.Invoke(currentPath, t); };
        writerTitle.PropsChanged += p => { if (currentPath != null) NovelStore.SaveProps(currentPath, p); };
        writerTitle.WikiLister = () => WikiStore.List(currentPath == null ? "" : Path.GetDirectoryName(currentPath)!).Select(d => (WikiCategory.Find(d.Category).Label, d.Title)).ToList();

        writerPlaceholder = true;
        mainEditor.TextChanged += (_, _) => SyncPlaceholder();

        // 오른쪽 아래 "글자 수 세기" 위젯은 뺐다(사용자 요청, 2026-11-05). 글자 수는 맨 아래 상태 줄에 그대로 보인다.

        mainEditor.SizeChanged += (_, _) => { if (WriterLayout) ApplySideMargin(); };
    }

    /// <summary>글 칸이 가운데 오도록 하는 좌우 여백(설정의 여백보다 작아지지는 않는다).</summary>
    private int WriterSideMargin(int baseMargin)
    {
        int col = LogicalToDeviceUnits(WriterColumnWidth) * ViewZoom / 100;
        return Math.Clamp(Math.Max(baseMargin, (mainEditor.ClientSize.Width - col) / 2), 0, 4000);
    }

    /// <summary>제목 줄 · 빈 글 안내 · 글자 수 위젯을 지금 상태(보이기 · 글자 · 색 · 글 칸 여백)에 맞춘다.</summary>
    private void SyncWriterTitle(int sideMargin)
    {
        if (writerTitle == null) return;
        bool on = WriterLayout;
        if (tall != null && (tall.Visible != on))
        {
            bool hadFocus = mainEditor.ContainsFocus;
            if (on)
            {
                editorHost.Controls.Remove(mainEditor);
                tall.Visible = true;
                tall.Attach(writerTitle, mainEditor);
            }
            else
            {
                tall.Detach();
                tall.Visible = false;
                mainEditor.Dock = DockStyle.Fill;
                mainEditor.ScrollBars = ScrollBars.Vertical;
                editorHost.Controls.Add(mainEditor);
                mainEditor.BringToFront();
            }
            if (hadFocus) mainEditor.Focus();
        }
        if (writerTitle.Visible != on) writerTitle.Visible = on;
        SyncPlaceholder(sideMargin);
        SyncGoalWidget();
        if (!on) return;
        if (!string.Equals(writerKeyPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            writerKeyPath = currentPath;
            RefreshWriterProps();
        }
        writerTitle.SetColors(mainEditor.BackColor, mainEditor.ForeColor);
        writerTitle.SetMargin(sideMargin);
        UpdateSavedDot();
    }

    /// <summary>제목 위 작은 글(유형이 일반이면 "N편", 아니면 유형 이름)과 상세 정보의 유형 · 상태를 파일에 저장된 값으로 맞춘다.</summary>
    private void RefreshWriterProps()
    {
        if (writerTitle == null || currentPath == null) return;
        string type = NovelStore.GetEpisodeType(currentPath);
        string? number = null; // 유형과 상관없이 늘 "N편"
        if (Path.GetDirectoryName(currentPath) is string dir)
        {
            // 목록과 같은 순서(그룹 밖 회차 먼저, 그다음 그룹 순서대로)로 000부터 센다: 000.EP · 001.EP …
            var eps = NovelStore.Episodes(dir);
            var order = eps.Where(p => NovelStore.GroupOfEpisode(p) == null).ToList();
            foreach (var g in NovelStore.Groups(dir)) order.AddRange(eps.Where(p => NovelStore.GroupOfEpisode(p) == g));
            int i = order.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) number = $"{i:000}.EP";
        }
        writerTitle.SetText(number, Path.GetFileNameWithoutExtension(currentPath));
        writerTitle.SetProps(type, NovelStore.GetWriteStatus(currentPath), NovelStore.GetProps(currentPath));
    }

    private void OnEpisodeTypeChosen(string type)
    {
        if (currentPath == null) return;
        NovelStore.SaveEpisodeType(currentPath, type);
        RefreshWriterProps();
        EpisodeMetaChanged?.Invoke();
    }

    private void OnEpisodeStatusChosen(string status)
    {
        if (currentPath == null) return;
        NovelStore.SaveWriteStatus(currentPath, status);
        EpisodeMetaChanged?.Invoke();
        ApplyManuscriptProtection();
        RefreshWriterProps();
        if (status == "집필 완료") ShowToast("원고 보호 — 집필 완료한 원고는 고칠 수 없어요. 상태를 바꾸면 다시 고칠 수 있어요.");
    }

    /// <summary>집필 상태가 [집필 완료]인 회차는 원고 보호로 글 상자를 잠근다(상태를 바꾸면 풀린다). 참고 자료 · 에피소드가 아닌 문서는 해당 없다.</summary>
    private void ApplyManuscriptProtection()
    {
        if (!embedded || !directEdit || currentPath == null || shownKind != ContentKind.Txt || IsReferenceFile(currentPath)) return;
        bool protect = NovelStore.GetWriteStatus(currentPath) == "집필 완료";
        if (mainEditor.ReadOnly == protect) return;
        if (protect) FlushAutoSave(); // 잠그기 전에 고친 글부터 저장
        mainEditor.ReadOnly = protect;
    }

    private int lastPlaceholderMargin;

    private void SyncPlaceholder(int? sideMargin = null)
    {
        if (!writerPlaceholder) return;
        if (sideMargin is int m) lastPlaceholderMargin = m;
        bool show = WriterLayout && mainEditor.TextLength == 0;
        string? text = show ? "이야기를 시작해 보세요…" : null;
        var color = Themes.Blend(mainEditor.BackColor, mainEditor.ForeColor, 0.4f);
        var at = new Point(lastPlaceholderMargin, 1);
        if (text == mainEditor.Placeholder && (!show || (color == mainEditor.PlaceholderColor && at == mainEditor.PlaceholderAt))) return;
        mainEditor.Placeholder = text;
        mainEditor.PlaceholderColor = color;
        mainEditor.PlaceholderAt = at;
        mainEditor.Invalidate(); // 나타나거나 지워지게 다시 그린다
    }

    // ───────── 글자 수 세기 위젯 ─────────

    private bool goalShown;  // 글자 수 위젯이 켜져 있는가(창을 열 때마다 꺼진 채로 시작)
    private bool goalMoved;  // 끌어서 옮겨 놓았는가
    private Point goalPos;     // 옮겨 놓은 자리(창 좌표). 위젯이 숨겨졌다 돌아올 때 이 값으로 되돌린다
    /// <summary>사이드바의 글자 수 세기 아이콘: 위젯을 켜고 끈다.</summary>
    internal void ToggleCountWidget() => ToggleGoalWidget();

    private void SyncGoalWidget()
    {
        if (goalWidget == null) return;
        bool show = WriterLayout && goalShown;
        if (goalWidget.Visible != show) { goalWidget.Visible = show; if (show) { PlaceGoalWidget(); goalWidget.BringToFront(); } }
        if (show) { goalWidget.SetColors(mainEditor.BackColor, mainEditor.ForeColor, theme.Link); UpdateGoalWidget(charsAll, charsSolid); }
    }

    /// <summary>상태 줄이 글자 수를 새로 셀 때마다 위젯에도 알린다.</summary>
    private void UpdateGoalWidget(int all, int solid)
    {
        if (goalWidget == null) return;
        string text = pageMode || txtWeb ? fullText : mainEditor.Text;
        goalWidget.SetCount(GoalCounter.Count(text, settings), Math.Max(1, settings.WriterGoalChars), GoalCounter.UnitLabel(settings));
    }

    /// <summary>위젯의 설정(톱니바퀴): 글자 수 세기 설정 창.</summary>
    private void OpenGoalSettings()
    {
        using var dlg = new GoalSettingsDialog(settings);
        dlg.ShowDialog(HostForm);
        UpdateGoalWidget(0, 0);
        EpisodeMetaChanged?.Invoke(); // 목록 글자 수 방식이 바뀌었을 수 있으니 목록을 다시 읽게
    }

    /// <summary>접거나 펼칠 때 폭을 바꾼다(오른쪽 끝은 그대로 두고 왼쪽이 늘고 줄게).</summary>
    private void ResizeGoalWidget()
    {
        if (goalWidget == null) return;
        int w = LogicalToDeviceUnits(goalWidget.Expanded ? 360 : 262);
        int right = goalWidget.Right;
        goalWidget.Bounds = new Rectangle(Math.Max(0, right - w), goalWidget.Top, w, goalWidget.Height);
        goalPos = goalWidget.Location;
    }

    /// <summary>위젯이 얹히는 곳: 창 전체(그래서 글 칸뿐 아니라 파일 트리 위로도 끌어 옮길 수 있다).</summary>
    private Control GoalHost => TopLevelControl ?? (Control)editorHost;

    private void EnsureGoalParent()
    {
        if (goalWidget == null) return;
        var host = GoalHost;
        if (goalWidget.Parent == host) return;
        Point? screen = goalWidget.Parent != null ? goalWidget.Parent.PointToScreen(goalWidget.Location) : null;
        goalWidget.Parent?.Controls.Remove(goalWidget);
        host.Controls.Add(goalWidget);
        if (screen is Point sp) goalWidget.Location = host.PointToClient(sp);
        goalWidget.BringToFront();
    }

    private void PlaceGoalWidget()
    {
        if (goalWidget == null) return;
        EnsureGoalParent();
        var host = GoalHost;
        var size = new Size(LogicalToDeviceUnits(goalWidget.Expanded ? 360 : 262), LogicalToDeviceUnits(38));
        // 창이 아직 숨어 있거나 크기가 잡히기 전이면 자리를 건드리지 않는다(그때 계산하면 왼쪽 위(0,0)로 눌려 버린다)
        if (host.ClientSize.Width < size.Width || host.ClientSize.Height < size.Height || !host.IsHandleCreated) return;
        if (goalMoved)
        {
            // 옮겨 놓은 자리(기억해 둔 값)를 지키되 창 밖으로 나가지 않게
            int x = Math.Clamp(goalPos.X, 0, Math.Max(0, host.ClientSize.Width - size.Width));
            int y = Math.Clamp(goalPos.Y, 0, Math.Max(0, host.ClientSize.Height - size.Height));
            goalWidget.Bounds = new Rectangle(x, y, size.Width, size.Height);
            goalWidget.BringToFront();
            return;
        }
        // 처음 자리: 글 칸 오른쪽 아래(창 좌표로 바꿔서)
        int right = editorHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(24);
        int bottom = editorHost.ClientSize.Height - editorHost.Padding.Bottom - LogicalToDeviceUnits(20);
        var at = host.PointToClient(editorHost.PointToScreen(new Point(Math.Max(0, right - size.Width), Math.Max(0, bottom - size.Height))));
        goalWidget.Bounds = new Rectangle(at, size);
        goalWidget.BringToFront();
    }

    /// <summary>상태 줄의 글자 수 칸을 누르면 위젯을 켜고 끈다.</summary>
    private void ToggleGoalWidget()
    {
        goalShown = !goalShown;
        settings.WriterGoalWidget = goalShown; // 켜고 끈 상태를 기억해서 다음 에피소드에도 이어간다
        settings.Save();
        SyncGoalWidget();
    }

    private ContextMenuStrip? goalMenu;

    private void ShowGoalMenu(Point screen)
    {
        goalMenu?.Dispose();
        var m = goalMenu = new ContextMenuStrip();
        m.Items.Add($"목표 글자 수 바꾸기… (지금 {settings.WriterGoalChars:N0}자)", null, (_, _) => BeginInvoke(AskGoal));
        var spaces = new ToolStripMenuItem("공백 포함해서 세기") { Checked = settings.WriterGoalWithSpaces };
        spaces.Click += (_, _) => { settings.WriterGoalWithSpaces = !settings.WriterGoalWithSpaces; settings.Save(); UpdateGoalWidget(charsAll, charsSolid); };
        m.Items.Add(spaces);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("위젯 닫기 (사이드바의 글자 수 세기 아이콘으로 다시 열어요)", null, (_, _) => { goalShown = false; settings.WriterGoalWidget = false; settings.Save(); SyncGoalWidget(); });
        m.Show(screen);
    }

    private void AskGoal()
    {
        using var dlg = new NovelInputDialog("목표 글자 수", "한 회차의 목표 글자 수", settings.WriterGoalChars.ToString(), firstIsName: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (int.TryParse(dlg.FirstValue.Replace(",", ""), out int n) && n > 0)
        {
            settings.WriterGoalChars = Math.Min(n, 10_000_000);
            settings.Save();
            UpdateGoalWidget(charsAll, charsSolid);
        }
    }

    /// <summary>
    /// <summary>
    /// 글 칸 위의 머리: 작은 [N편 / 유형]과 큰 회차 제목, 그 밑에 [상세 정보 펼치기].
    /// 펼치면 상세 정보가 열린다: [요약] [작가의 말](추가한 것만) / [유형 · 상태 · 수위 · 등장] | [줄바꿈 · 태그 · 시점] / [+ 속성 추가하기].
    /// 유형 · 상태는 늘 있고, 나머지 속성은 [속성 추가하기]로 추가한다. 값은 눌러서 고른다(목록 · 입력 창 · 태그 칸).
    /// </summary>
    private sealed class WriterHeader : Control
    {
        private enum CellKind { Text, Button, Chips }
        private sealed class Cell
        {
            public string Key = "", Label = "", Glyph = "";
            public CellKind Kind;
            public Rectangle LabelRect, ValueRect, DelRect;
            public int ColRight;
            public string ValueText = "";
            public bool IsPlaceholder;
            public List<(Rectangle Rect, string Text)> Chips = new();
            public Rectangle PlusRect;
            public string PlusText = "";
        }

        private string? number;
        private string title = "";
        private int margin;
        private Color back = Color.White, fore = Color.Black;
        private readonly Font numFont = new(NovelStore.UiFontFamily, 10f);
        private readonly Font titleFont = new(NovelStore.UiFontFamily, 20f, FontStyle.Bold);
        private readonly Font labelFont = new(NovelStore.UiFontFamily, 10.5f);
        private readonly Font chipFont = new(NovelStore.UiFontFamily, 9.5f);
        private readonly Font iconFont = new("Segoe MDL2 Assets", 11f);
        private bool expanded;
        private string type = "일반", status = "집필 예정";
        private EpisodeProps props = new();
        private readonly List<Cell> cells = new();
        private readonly List<(Rectangle Rect, string Id)> hits = new();
        private Rectangle toggleRect, addRect;
        private string? hoverId;
        private string? hoverCell;
        private ContextMenuStrip? menu;
        private readonly TextBox tagEditor;
        private bool tagEditing;
        private bool saved;

        public Func<List<(string Category, string Title)>>? WikiLister;
        public event Action<string>? TypeChosen, StatusChosen;
        public event Action<EpisodeProps>? PropsChanged;
        public event Action<string>? TitleChanged;
        private readonly TextBox titleEditor;
        private bool titleEditing;
        private Rectangle titleRect;

        public WriterHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            tagEditor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, PlaceholderText = "새 태그", Font = chipFont };
            tagEditor.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string t = tagEditor.Text.Replace("\r", " ").Replace('\n', ' ').Trim().TrimStart('#').Trim();
                    if (t.Length == 0) { EndTagEdit(); return; }
                    tagEditor.Clear();
                    Change(p => { p.Tags ??= new(); if (!p.Tags.Contains(t)) p.Tags.Add(t); });
                    tagEditor.Focus();
                }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndTagEdit(); }
            };
            tagEditor.LostFocus += (_, _) => { if (tagEditing) EndTagEdit(); };
            Controls.Add(tagEditor);
            titleEditor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, Font = titleFont };
            titleEditor.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndTitleEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndTitleEdit(false); }
            };
            titleEditor.LostFocus += (_, _) => EndTitleEdit(true);
            Controls.Add(titleEditor);
        }

        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        /// <summary>저장된 상태면 제목 옆에 보라색 동그라미를 보인다.</summary>
        public void SetSaved(bool value) { if (saved == value) return; saved = value; Invalidate(); }

        public void SetText(string? num, string t) { number = num; title = t; Fit(); Invalidate(); }
        public void SetProps(string typeName, string statusName, EpisodeProps p) { type = typeName; status = statusName; props = p; Fit(); Invalidate(); }
        public void SetColors(Color b, Color f) { if (b == back && f == fore) return; back = b; fore = f; tagEditor.BackColor = Themes.Blend(b, f, 0.06f); tagEditor.ForeColor = f; titleEditor.BackColor = b; titleEditor.ForeColor = f; Invalidate(); }
        public void SetMargin(int m) { if (m == margin) return; margin = m; Fit(); Invalidate(); }

        private void Change(Action<EpisodeProps> edit)
        {
            edit(props);
            PropsChanged?.Invoke(props);
            Fit();
            Invalidate();
        }

        private bool Has(string key) => props.Added?.Contains(key) == true;

        // ───────── 배치 ─────────

        private int TextWidth(string text, Font f) => TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        /// <summary>칸(속성 하나)을 정하고 전체 높이를 돌려준다.</summary>
        private int LayoutAll()
        {
            cells.Clear();
            hits.Clear();
            int x = margin, w = Math.Max(0, Width - margin * 2);
            int y = S(28);
            if (number != null) y += numFont.Height + S(4);
            y += titleFont.Height + S(10);
            toggleRect = addRect = Rectangle.Empty;
            titleRect = new Rectangle(x, y - titleFont.Height - S(10), w, titleFont.Height + S(4));
            hits.Add((titleRect, "title"));
            if (titleEditing) titleEditor.SetBounds(x, titleRect.Y + S(2), w, titleFont.Height);
            tagEditor.Visible = false;
            if (expanded)
            {
                int rowH = S(40), btnH = S(28), valueDx = S(110);
                // 두 칸 격자의 폭: 오른쪽 칸 끝(= 시점 · 줄바꿈의 x 자리)에 요약 · 작가의 말 · 등장 · 태그의 x도 맞춘다
                bool two = w >= S(640);
                int col2 = x + S(340);
                int rightW = Math.Min(S(300), Math.Max(S(120), x + w - col2));
                int fullRight = two ? col2 + rightW : x + w;
                // 한 줄 전체를 쓰는 글 속성: 요약 · 작가의 말
                foreach (var key in new[] { "summary", "note" })
                {
                    if (!Has(key)) continue;
                    string? v = key == "summary" ? props.Summary : props.AuthorNote;
                    var def = EpisodeProps.Defs.First(d => d.Key == key);
                    var cell = new Cell { Key = key, Label = def.Label, Glyph = def.Glyph, ColRight = fullRight, Kind = CellKind.Text, ValueText = string.IsNullOrWhiteSpace(v) ? "비어 있음" : v!.Replace("\r", "").Replace('\n', ' '), IsPlaceholder = string.IsNullOrWhiteSpace(v) };
                    cell.LabelRect = new Rectangle(x, y, valueDx - S(8), rowH);
                    cell.ValueRect = new Rectangle(x + valueDx, y, Math.Max(S(60), fullRight - x - valueDx), rowH);
                    cells.Add(cell);
                    hits.Add((cell.ValueRect, key));
                    hits.Add((cell.LabelRect, "label:" + key));
                    y += rowH;
                }
                // 격자: [유형 | 상태] · [수위 | 시점] 두 칸, 그 아래로 [등장] · [태그]는 한 줄씩
                int cw2 = two ? S(300) : w;
                void Pair(string a, string? b)
                {
                    cells.Add(BuildGridCell(a, x, y, rowH, btnH, valueDx, cw2));
                    if (b != null) cells.Add(BuildGridCell(b, two ? col2 : x, two ? y : y + rowH, rowH, btnH, valueDx, two ? rightW : w));
                    y += (b != null && !two) ? rowH * 2 : rowH;
                }
                Pair("type", "status");
                var mid = new[] { "rating", "pov" }.Where(Has).ToList();
                if (mid.Count > 0) Pair(mid[0], mid.Count > 1 ? mid[1] : null);
                foreach (var k in new[] { "appear", "tags" })
                    if (Has(k)) { cells.Add(BuildGridCell(k, x, y, rowH, btnH, valueDx, fullRight - x)); y += rowH; }
                if (EpisodeProps.Defs.Any(d => !Has(d.Key)))
                {
                    addRect = new Rectangle(x, y, S(180), S(36));
                    hits.Add((addRect, "add"));
                    y += S(40);
                }
            }
            foreach (var c in cells) // 유형 · 상태 말고는 지울 수 있다: 줄에 마우스를 올리면 끝에 x가 나온다
            {
                if (c.Key is "type" or "status") continue;
                if (c.Kind == CellKind.Text) c.ValueRect = new Rectangle(c.ValueRect.X, c.ValueRect.Y, Math.Max(S(40), c.ValueRect.Width - S(30)), c.ValueRect.Height);
                int dx = c.ColRight - S(22);
                c.DelRect = new Rectangle(dx, c.LabelRect.Y + (c.LabelRect.Height - S(22)) / 2, S(22), S(22));
                hits.Add((c.DelRect, "del:" + c.Key));
            }
            toggleRect = new Rectangle(x, y, Math.Min(Math.Max(w, S(120)), S(220)), S(32));
            hits.Add((toggleRect, "toggle"));
            y += S(32);
            return y + S(16);
        }

        /// <summary>격자 칸 하나(유형 · 상태 · 수위 · 시점 · 줄바꿈은 버튼, 태그 · 등장은 칩 줄).</summary>
        private Cell BuildGridCell(string key, int cx, int y, int rowH, int btnH, int valueDx, int cellW)
        {
            string label = key switch { "type" => "유형", "status" => "상태", _ => EpisodeProps.Defs.First(d => d.Key == key).Label };
            string glyph = key switch { "type" => "", "status" => "", _ => EpisodeProps.Defs.First(d => d.Key == key).Glyph };
            var cell = new Cell { Key = key, Label = label, Glyph = glyph };
            cell.ColRight = cx + cellW;
            cell.LabelRect = new Rectangle(cx, y, valueDx - S(8), rowH);
            if (key is "tags" or "appear") hits.Add((cell.LabelRect, "label:" + key)); // 오른쪽 눌러 속성 지우기
            else if (key is "rating" or "pov") hits.Add((cell.LabelRect, "label:" + key));
            int vx = cx + valueDx;
            if (key is "tags" or "appear")
            {
                cell.Kind = CellKind.Chips;
                var list = (key == "tags" ? props.Tags : props.Appear) ?? new();
                int chipH = btnH - S(2), cy = y + (rowH - chipH) / 2, x = vx, right = cx + cellW;
                for (int i = 0; i < list.Count; i++)
                {
                    int cw = TextWidth(list[i], chipFont) + S(32);
                    if (x + cw > right - S(86)) break; // 자리가 모자라면 거기서 멈춘다
                    var rect = new Rectangle(x, cy, cw, chipH);
                    cell.Chips.Add((rect, list[i]));
                    hits.Add((new Rectangle(rect.Right - S(22), rect.Y, S(22), rect.Height), key + ":x:" + i));
                    x += cw + S(6);
                }
                cell.PlusText = key == "tags" ? "새 태그 추가" : "등장한 캐릭터 · 설정";
                bool editingHere = key == "tags" && tagEditing;
                int pw = editingHere ? S(130) : TextWidth(cell.PlusText, chipFont) + S(36);
                cell.PlusRect = new Rectangle(x, cy, pw, chipH);
                if (editingHere)
                {
                    tagEditor.SetBounds(cell.PlusRect.X + S(8), cell.PlusRect.Y + (chipH - tagEditor.Height) / 2, cell.PlusRect.Width - S(16), tagEditor.Height);
                    tagEditor.Visible = true;
                }
                else hits.Add((cell.PlusRect, key + ":add"));
                cell.ValueRect = new Rectangle(vx, cy, Math.Max(0, cell.PlusRect.Right - vx), chipH);
                return cell;
            }
            cell.Kind = CellKind.Button;
            cell.ValueText = key switch
            {
                "type" => type,
                "status" => status,
                "rating" => string.IsNullOrEmpty(props.Rating) ? "수위 선택" : props.Rating!,
                "pov" => string.IsNullOrEmpty(props.Pov) ? "미지정" : props.Pov!,
                _ => string.IsNullOrEmpty(props.LineBreak) ? EpisodeProps.LineBreakOptions[0] : props.LineBreak!,
            };
            int bw = TextWidth(cell.ValueText, labelFont) + S(key == "status" ? 62 : 42);
            cell.ValueRect = new Rectangle(vx, y + (rowH - btnH) / 2, bw, btnH);
            hits.Add((cell.ValueRect, key));
            return cell;
        }

        private void Fit()
        {
            int h = LayoutAll();
            if (Height != h) Height = h;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        // ───────── 마우스 ─────────

        private string? HitId(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Rect.Contains(p)) return hits[i].Id;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string? hc = null;
            foreach (var c in cells) if (c.DelRect != Rectangle.Empty && new Rectangle(c.LabelRect.X, c.LabelRect.Y, c.DelRect.Right - c.LabelRect.X, c.LabelRect.Height).Contains(e.Location)) hc = c.Key;
            if (hc != hoverCell) { hoverCell = hc; Invalidate(); }
            string? id = HitId(e.Location);
            if (id != hoverId) { hoverId = id; Cursor = id != null && !id.StartsWith("label:") ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverCell = null; if (hoverId != null) { hoverId = null; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string? id = HitId(e.Location);
            if (id == null) return;
            if (e.Button == MouseButtons.Right)
            {
                if (id.StartsWith("label:")) ShowRemoveMenu(id[6..], e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left || id.StartsWith("label:")) return;
            if (id.StartsWith("del:")) { RemoveProp(id[4..]); return; }
            switch (id)
            {
                case "title": BeginTitleEdit(); break;
                case "toggle": expanded = !expanded; EndTagEdit(); Fit(); Invalidate(); break;
                case "type": ShowChoiceMenu(id, type, NovelStore.EpisodeTypes, TypeLabel, v => TypeChosen?.Invoke(v)); break;
                case "status": ShowChoiceMenu(id, status, NovelStore.WriteStatusNames, v => v == "집필 완료" ? "집필 완료      (원고 보호)" : v, v => StatusChosen?.Invoke(v)); break;
                case "rating": ShowChoiceMenu(id, props.Rating ?? "", EpisodeProps.RatingOptions, v => v, v => Change(p => p.Rating = v), clearText: "수위 선택 안 함", onClear: () => Change(p => p.Rating = null), icon: v => GlyphImage("", RatingColor(v))); break;
                case "pov": ShowChoiceMenu(id, props.Pov ?? "", EpisodeProps.PovOptions, v => v, v => Change(p => p.Pov = v), clearText: "미지정", onClear: () => Change(p => p.Pov = null), icon: v => GlyphImage(PovGlyph(v), Color.FromArgb(150, 110, 230))); break;
                case "summary": EditText("요약", props.Summary, v => Change(p => p.Summary = v)); break;
                case "note": EditText("작가의 말", props.AuthorNote, v => Change(p => p.AuthorNote = v)); break;
                case "tags:add": BeginTagEdit(); break;
                case "appear:add": ShowAppearMenu(); break;
                case "add": ShowAddMenu(); break;
                default:
                    if (id.StartsWith("tags:x:") && int.TryParse(id[7..], out int ti)) Change(p => { if (p.Tags != null && ti < p.Tags.Count) p.Tags.RemoveAt(ti); });
                    else if (id.StartsWith("appear:x:") && int.TryParse(id[9..], out int ai)) Change(p => { if (p.Appear != null && ai < p.Appear.Count) p.Appear.RemoveAt(ai); });
                    break;
            }
        }

        private static Color RatingColor(string v) => v switch
        {
            "전체 이용가" => Color.FromArgb(30, 160, 80),
            "12세 이상" => Color.FromArgb(50, 110, 230),
            "15세 이상" => Color.FromArgb(235, 190, 30),
            "19세 이상" => Color.FromArgb(235, 110, 30),
            "제한 관람가" => Color.FromArgb(220, 50, 50),
            _ => Color.FromArgb(40, 40, 40),
        };

        private static string PovGlyph(string v) => v switch
        {
            "1인칭 주인공 시점" => "\uE77B",
            "1인칭 관찰자 시점" => "\uE721",
            "2인칭 관찰자 시점" => "\uE7C9",
            "3인칭 관찰자 시점" => "\uE7B3",
            "전지적 작가 시점" => "\uE774",
            "선택적 시점" => "\uE8B1",
            "복합 시점" => "\uE716",
            "의식의 흐름" => "\uE9D9",
            _ => "\uE897",
        };

        private Image GlyphImage(string glyph, Color color)
        {
            int sz = S(18);
            var bmp = new Bitmap(sz, sz);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(0, 0, sz, sz), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return bmp;
        }

        private static string TypeLabel(string t) => t switch
        {
            "일반" => "일반 에피소드로 지정",
            "프롤로그" => "프롤로그로 지정",
            "에필로그" => "에필로그로 지정",
            "특별편" => "특별편으로 지정",
            _ => "단편으로 지정",
        };

        private Rectangle RectOf(string id) => hits.LastOrDefault(h => h.Id == id).Rect;

        private void ShowMenuAt(ContextMenuStrip m, Rectangle anchor) { menu?.Dispose(); menu = m; m.Show(this, new Point(anchor.Left, anchor.Bottom + S(4))); }

        private ContextMenuStrip NewMenu() => new() { Font = new Font(NovelStore.UiFontFamily, 10.5f), ShowImageMargin = false };

        private void ShowChoiceMenu(string id, string current, string[] values, Func<string, string> label, Action<string> chosen, string? clearText = null, Action? onClear = null, Func<string, Image?>? icon = null)
        {
            var m = NewMenu();
            if (icon != null) m.ShowImageMargin = true;
            foreach (var v in values)
            {
                string value = v;
                m.Items.Add(new ToolStripMenuItem(label(v), icon?.Invoke(v), (_, _) => chosen(value)) { Enabled = !string.Equals(v, current, StringComparison.Ordinal), Padding = new Padding(4, 6, 4, 6) });
            }
            if (clearText != null && onClear != null && !string.IsNullOrEmpty(current))
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem(clearText, null, (_, _) => onClear()) { Padding = new Padding(4, 6, 4, 6) });
            }
            ShowMenuAt(m, RectOf(id));
        }

        private void EditText(string caption, string? current, Action<string?> save)
        {
            using var dlg = new TextEditDialog(caption, current ?? "");
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            save(string.IsNullOrWhiteSpace(dlg.Value) ? null : dlg.Value);
        }

        /// <summary>[속성 추가하기]: 아직 안 넣은 속성들을 목록으로 보여 주고, 고르면 상세 정보에 그 속성 줄이 생긴다.</summary>
        private void ShowAddMenu()
        {
            var m = NewMenu();
            foreach (var d in EpisodeProps.Defs.Where(d => !Has(d.Key)))
            {
                string key = d.Key;
                m.Items.Add(new ToolStripMenuItem(d.Label, null, (_, _) => Change(p => { p.Added ??= new(); if (!p.Added.Contains(key)) p.Added.Add(key); })) { Padding = new Padding(4, 6, 4, 6) });
            }
            ShowMenuAt(m, addRect);
        }

        /// <summary>속성 이름을 오른쪽 누르면 [이 속성 지우기](값도 함께 비운다).</summary>
        private void RemoveProp(string key) => Change(p => p.Added?.Remove(key)); // 값은 두고 숨기기만 한다: [속성 추가하기]로 다시 넣으면 그대로 돌아온다

        private void ShowRemoveMenu(string key, Point at)
        {
            var m = NewMenu();
            m.Items.Add(new ToolStripMenuItem("이 속성 빼기", null, (_, _) => RemoveProp(key)));
            menu?.Dispose();
            menu = m;
            m.Show(this, at);
        }

        /// <summary>[등장]: 이 소설의 위키 문서(인물 · 장소 · 설정 …)를 목록으로 보여 주고 체크로 고른다.</summary>
        private void ShowAppearMenu()
        {
            var m = NewMenu();
            var docs = WikiLister?.Invoke() ?? new();
            if (docs.Count == 0) m.Items.Add(new ToolStripMenuItem("아직 위키 문서가 없어요") { Enabled = false });
            foreach (var (cat, name) in docs)
            {
                string n = name;
                bool on = props.Appear?.Contains(n) == true;
                m.Items.Add(new ToolStripMenuItem($"{n}   ·   {cat}", null, (_, _) => Change(p =>
                {
                    p.Appear ??= new();
                    if (!p.Appear.Remove(n)) p.Appear.Add(n);
                })) { Checked = on, Padding = new Padding(4, 6, 4, 6) });
            }
            ShowMenuAt(m, RectOf("appear:add"));
        }

        private void BeginTitleEdit()
        {
            titleEditing = true;
            titleEditor.Text = title;
            LayoutAll();
            titleEditor.Visible = true;
            titleEditor.Focus();
            titleEditor.SelectAll();
            Invalidate();
        }

        private void EndTitleEdit(bool commit)
        {
            if (!titleEditing) return;
            titleEditing = false;
            titleEditor.Visible = false;
            string t = titleEditor.Text.Trim();
            Invalidate();
            if (commit && t.Length > 0 && t != title) TitleChanged?.Invoke(t);
        }

        private void BeginTagEdit()
        {
            tagEditing = true;
            Fit();
            Invalidate();
            tagEditor.Clear();
            tagEditor.Visible = true;
            tagEditor.Focus();
        }

        private void EndTagEdit()
        {
            if (!tagEditing) return;
            tagEditing = false;
            tagEditor.Visible = false;
            tagEditor.Clear();
            Fit();
            Invalidate();
        }

        // ───────── 그리기 ─────────

        private void DrawChevron(Graphics g, Point c, int dir, Color color) // dir: 0 아래 · 1 오른쪽
        {
            using var cp = new Pen(color, Math.Max(1.3f, DeviceDpi / 96f * 1.3f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            float d = S(4);
            if (dir == 0) { g.DrawLine(cp, c.X - d, c.Y - d * 0.5f, c.X, c.Y + d * 0.5f); g.DrawLine(cp, c.X, c.Y + d * 0.5f, c.X + d, c.Y - d * 0.5f); }
            else { g.DrawLine(cp, c.X - d * 0.5f, c.Y - d, c.X + d * 0.5f, c.Y); g.DrawLine(cp, c.X + d * 0.5f, c.Y, c.X - d * 0.5f, c.Y + d); }
        }

        private void DrawButton(Graphics g, Rectangle r, string text, bool hot, string? leadGlyph, int chevronDir)
        {
            using (var path = LibraryPalette.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(6)))
            {
                using (var b = new SolidBrush(Themes.Blend(back, fore, hot ? 0.10f : 0.05f))) g.FillPath(b, path);
                using (var pen = new Pen(Themes.Blend(back, fore, 0.16f))) g.DrawPath(pen, path);
            }
            int tx = r.X + S(10);
            var vc = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            if (leadGlyph != null)
            {
                TextRenderer.DrawText(g, leadGlyph, iconFont, new Rectangle(tx - S(2), r.Y, S(20), r.Height), fore, vc | TextFormatFlags.HorizontalCenter);
                tx += S(22);
            }
            TextRenderer.DrawText(g, text, labelFont, new Rectangle(tx, r.Y, r.Right - tx - S(22), r.Height), fore, vc);
            DrawChevron(g, new Point(r.Right - S(15), r.Y + r.Height / 2), chevronDir, fore);
        }

        private void DrawChip(Graphics g, Rectangle r, string text, bool removable, bool xHot)
        {
            using (var path = LibraryPalette.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(5)))
            {
                using (var b = new SolidBrush(Themes.Blend(back, fore, 0.06f))) g.FillPath(b, path);
                using (var pen = new Pen(Themes.Blend(back, fore, 0.12f))) g.DrawPath(pen, path);
            }
            var vc = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, chipFont, new Rectangle(r.X + S(8), r.Y, r.Width - S(30), r.Height), Themes.Blend(back, fore, 0.75f), vc);
            if (!removable) return;
            using var xp = new Pen(xHot ? fore : Themes.Blend(back, fore, 0.5f), Math.Max(1.2f, DeviceDpi / 96f * 1.2f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            float cx = r.Right - S(11), cy = r.Y + r.Height / 2f, d = S(3);
            g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
        }

        private void DrawPlus(Graphics g, Rectangle r, string text, bool hot)
        {
            if (hot) using (var hp = LibraryPalette.Rounded(r, S(5))) using (var hb = new SolidBrush(Themes.Blend(back, fore, 0.07f))) g.FillPath(hb, hp);
            var vc = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, chipFont, new Rectangle(r.X + S(8), r.Y, r.Width - S(28), r.Height), Themes.Blend(back, fore, 0.55f), vc);
            using var pen = new Pen(hot ? fore : Themes.Blend(back, fore, 0.55f), Math.Max(1.2f, DeviceDpi / 96f * 1.2f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            float cx = r.Right - S(13), cy = r.Y + r.Height / 2f, d = S(4);
            g.DrawLine(pen, cx - d, cy, cx + d, cy);
            g.DrawLine(pen, cx, cy - d, cx, cy + d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            LayoutAll();
            int x = margin, y = S(28), w = Math.Max(0, Width - margin * 2);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            var vc = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            Color dim = Themes.Blend(back, fore, 0.55f);
            if (number != null)
            {
                TextRenderer.DrawText(g, number, numFont, new Rectangle(x, y, w, numFont.Height), Themes.Blend(back, fore, 0.5f), flags);
                y += numFont.Height + S(4);
            }
            if (!titleEditing) TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, y, w, titleFont.Height), fore, flags);

            foreach (var c in cells)
            {
                // 속성 이름: [아이콘] 이름
                bool pilcrow = c.Glyph == "¶";
                TextRenderer.DrawText(g, c.Glyph, pilcrow ? labelFont : iconFont, new Rectangle(c.LabelRect.X, c.LabelRect.Y, S(22), c.LabelRect.Height), dim, vc | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, c.Label, labelFont, new Rectangle(c.LabelRect.X + S(30), c.LabelRect.Y, c.LabelRect.Width - S(30), c.LabelRect.Height), Themes.Blend(back, fore, 0.7f), vc | TextFormatFlags.EndEllipsis);
                if (hoverCell == c.Key && c.DelRect != Rectangle.Empty)
                {
                    using var xp = new Pen(hoverId == "del:" + c.Key ? fore : dim, Math.Max(1.3f, DeviceDpi / 96f * 1.3f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                    float cx = c.DelRect.X + c.DelRect.Width / 2f, cy = c.DelRect.Y + c.DelRect.Height / 2f, d = S(4);
                    g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
                }
                switch (c.Kind)
                {
                    case CellKind.Text:
                        TextRenderer.DrawText(g, c.ValueText, labelFont, c.ValueRect, c.IsPlaceholder ? Themes.Blend(back, fore, 0.45f) : fore, vc | TextFormatFlags.EndEllipsis);
                        if (hoverId == c.Key) using (var hp = LibraryPalette.Rounded(Rectangle.Inflate(c.ValueRect, S(4), -S(4)), S(6))) using (var hpen = new Pen(Themes.Blend(back, fore, 0.14f))) g.DrawPath(hpen, hp);
                        break;
                    case CellKind.Button:
                        DrawButton(g, c.ValueRect, c.ValueText, hoverId == c.Key, c.Key == "status" ? "" : null, c.Key == "linebreak" ? 1 : 0);
                        break;
                    case CellKind.Chips:
                        for (int i = 0; i < c.Chips.Count; i++) DrawChip(g, c.Chips[i].Rect, c.Chips[i].Text, true, hoverId == c.Key + ":x:" + i);
                        if (c.Key == "tags" && tagEditing)
                        {
                            using var ep = LibraryPalette.Rounded(new RectangleF(c.PlusRect.X + 0.5f, c.PlusRect.Y + 0.5f, c.PlusRect.Width - 1, c.PlusRect.Height - 1), S(5));
                            using var eb = new SolidBrush(Themes.Blend(back, fore, 0.06f));
                            g.FillPath(eb, ep);
                            using var epen = new Pen(fore, 1.2f);
                            g.DrawPath(epen, ep);
                        }
                        else DrawPlus(g, c.PlusRect, c.PlusText, hoverId == c.Key + ":add");
                        break;
                }
            }

            if (expanded && addRect != Rectangle.Empty)
            {
                using (var pen = new Pen(hoverId == "add" ? fore : dim, Math.Max(1.3f, DeviceDpi / 96f * 1.3f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    float px = addRect.X + S(11), py = addRect.Y + addRect.Height / 2f, pd = S(5);
                    g.DrawLine(pen, px - pd, py, px + pd, py);
                    g.DrawLine(pen, px, py - pd, px, py + pd);
                }
                TextRenderer.DrawText(g, "속성 추가하기", labelFont, new Rectangle(addRect.X + S(30), addRect.Y, addRect.Width - S(30), addRect.Height), hoverId == "add" ? fore : dim, vc);
            }

            // [상세 정보 펼치기 / 접기]
            using (var pen = new Pen(hoverId == "toggle" ? fore : dim, Math.Max(1.4f, DeviceDpi / 96f * 1.4f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                float cx = toggleRect.X + S(11), cy = toggleRect.Y + toggleRect.Height / 2f, d = S(4);
                float s = expanded ? -1 : 1;
                g.DrawLine(pen, cx - d, cy - d * 0.5f * s, cx, cy + d * 0.5f * s);
                g.DrawLine(pen, cx, cy + d * 0.5f * s, cx + d, cy - d * 0.5f * s);
            }
            TextRenderer.DrawText(g, expanded ? "상세 정보 접기" : "상세 정보 펼치기", labelFont, new Rectangle(toggleRect.X + S(30), toggleRect.Y, toggleRect.Width - S(30), toggleRect.Height), hoverId == "toggle" ? fore : dim, vc);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { numFont.Dispose(); titleFont.Dispose(); labelFont.Dispose(); chipFont.Dispose(); iconFont.Dispose(); menu?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// 글자 수 세기 위젯(알약 모양, 두 가지 크기):
///  · 작은 모양: [현재 / 목표] [›] [압정] [설정] [닫기]
///  · 넓은 모양(› 를 누르면): [현재] [진행 막대] [목표 + 단위] [‹] [압정] [설정] [닫기]
/// 사이드바의 글자 수 세기 아이콘을 누르면 나타나고, 압정을 켜지 않았을 때는 배경을 끌어서 옮길 수 있다.
/// </summary>
internal sealed class CharGoalWidget : Control
{
    private long count;
    private int goal = 3000;
    private string unit = "자";
    private Color back = Color.White, fore = Color.Black, accent = Color.SteelBlue;
    private Rectangle foldRect, pinRect, gearRect, closeRect;
    private int hot = -1; // 0 펼치기/접기 · 1 압정 · 2 설정 · 3 닫기
    private bool pinned, expanded;
    private Point dragFrom;
    private bool dragging;

    public event Action? SettingsRequested;
    public event Action? CloseRequested;
    /// <summary>끌어서 옮겨 놓았다(그 뒤로는 창 크기가 바뀌어도 그 자리를 지킨다).</summary>
    public event Action? Moved;
    /// <summary>작은 모양 ↔ 넓은 모양이 바뀌었다(위젯 폭은 놓은 쪽이 다시 정한다).</summary>
    public event Action? ExpandedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned { get => pinned; set { if (pinned == value) return; pinned = value; Invalidate(); } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(); } }

    public CharGoalWidget()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        Font = new Font(NovelStore.UiFontFamily, 10f);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // 알약 모양 밖은 창 자체를 잘라 내서, 뒤에 있는 칸이 그대로 보이게(네모 모서리 잔상 방지)
        if (Width > 2 && Height > 2)
        {
            using var path = new GraphicsPath();
            float d = Height;
            path.AddArc(0, 0, d, d, 90, 180);
            path.AddArc(Width - d, 0, d, d, 270, 180);
            path.CloseFigure();
            Region?.Dispose();
            Region = new Region(path);
        }
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetCount(long c, int g, string unitText) { if (c == count && g == goal && unitText == unit) return; count = c; goal = g; unit = unitText; Invalidate(); }
    public void SetColors(Color b, Color f, Color a) { if (b == back && f == fore && a == accent) return; back = b; fore = f; accent = a; Invalidate(); }

    private int HitAt(Point p) => foldRect.Contains(p) ? 0 : pinRect.Contains(p) ? 1 : gearRect.Contains(p) ? 2 : closeRect.Contains(p) ? 3 : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && Parent != null)
        {
            var np = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
            np.X = Math.Clamp(np.X, 0, Math.Max(0, Parent.ClientSize.Width - Width));
            np.Y = Math.Clamp(np.Y, 0, Math.Max(0, Parent.ClientSize.Height - Height));
            var oldBounds = Bounds;
            Location = np;
            // 지나간 자리와 새 자리 밑의 다른 칸이 바로 다시 그려지게(안 그러면 잠깐 잔상이 남는다)
            Parent.Invalidate(oldBounds, true);
            Parent.Update();
            return;
        }
        int h = HitAt(e.Location);
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : pinned ? Cursors.Default : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitAt(e.Location) < 0 && !pinned) { dragging = true; dragFrom = e.Location; Capture = true; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging)
        {
            bool moved = Math.Abs(e.X - dragFrom.X) + Math.Abs(e.Y - dragFrom.Y) > S(2);
            dragging = false; Capture = false;
            if (moved) Moved?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Right) { SettingsRequested?.Invoke(); return; }
        if (e.Button != MouseButtons.Left) return;
        switch (HitAt(e.Location))
        {
            case 0: Expanded = !expanded; break;   // › 넓게 · ‹ 작게
            case 1: Pinned = !pinned; break;       // 압정: 켜면 그 자리에 고정
            case 2: SettingsRequested?.Invoke(); break;
            case 3: CloseRequested?.Invoke(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = back.GetBrightness() < 0.4f;
        Color card = dark ? Themes.Blend(back, Color.White, 0.06f) : Color.White;
        Color dim = Themes.Blend(back, fore, 0.50f);
        Color ink = Themes.Blend(back, fore, 0.82f);

        // 알약: 양끝이 완전히 둥글고 얇은 테두리
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = LibraryPalette.Rounded(r, r.Height / 2f))
        {
            using (var b = new SolidBrush(card)) g.FillPath(b, path);
            using (var p = new Pen(Themes.Blend(back, fore, 0.55f), Math.Max(1f, DeviceDpi / 96f))) g.DrawPath(p, path);
        }

        int btn = S(26), pad = S(12), gap = 0;
        closeRect = new Rectangle(Width - pad - btn + S(2), (Height - btn) / 2, btn, btn);
        gearRect = new Rectangle(closeRect.Left - gap - btn, closeRect.Top, btn, btn);
        pinRect = new Rectangle(gearRect.Left - gap - btn, closeRect.Top, btn, btn);
        foldRect = new Rectangle(pinRect.Left - gap - btn, closeRect.Top, btn, btn);

        void Hover(Rectangle rc, int idx)
        {
            if (hot != idx) return;
            using var hb = new SolidBrush(Themes.Blend(card, fore, 0.08f));
            using var hp = LibraryPalette.Rounded(rc, rc.Height / 2f);
            g.FillPath(hb, hp);
        }
        Hover(foldRect, 0); Hover(pinRect, 1); Hover(gearRect, 2); Hover(closeRect, 3);

        var center = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter;
        var left = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        Color tc = count >= goal ? Color.FromArgb(0x3C, 0xA0, 0x6B) : dim;

        if (!expanded)
        {
            // 작은 모양: 왼쪽 끝과 › 사이의 가운데에 "현재 / 목표"
            TextRenderer.DrawText(g, $"{count:N0} / {goal:N0}", Font, new Rectangle(pad, 0, Math.Max(0, foldRect.Left - pad), Height), tc, center);
        }
        else
        {
            // 넓은 모양: [현재] ─────── [목표 + 단위]
            string cur = count.ToString("N0");
            string goalText = $"{goal:N0}{unit}";
            int cw = Math.Max(S(20), TextRenderer.MeasureText(cur, Font, Size.Empty, TextFormatFlags.NoPadding).Width);
            int gw = TextRenderer.MeasureText(goalText, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, cur, Font, new Rectangle(pad + S(4), 0, cw + S(4), Height), tc, left);
            int gx = foldRect.Left - S(6) - gw;
            TextRenderer.DrawText(g, goalText, Font, new Rectangle(gx, 0, gw + S(2), Height), dim, left);
            int bx = pad + S(4) + cw + S(16), bw = Math.Max(S(30), gx - S(14) - bx), bh = S(3);
            var bar = new RectangleF(bx, (Height - bh) / 2f, bw, bh);
            using (var bb = new SolidBrush(Themes.Blend(card, fore, 0.10f))) using (var bp = LibraryPalette.Rounded(bar, bh / 2f)) g.FillPath(bb, bp);
            float fw = bar.Width * Math.Min(1f, count / (float)Math.Max(1, goal));
            if (fw >= 1)
                using (var fb = new SolidBrush(count >= goal ? Color.FromArgb(0x3C, 0xA0, 0x6B) : accent))
                using (var fp = LibraryPalette.Rounded(new RectangleF(bar.X, bar.Y, Math.Max(fw, bh), bh), bh / 2f)) g.FillPath(fb, fp);
        }

        // 아이콘은 모두 아이콘 글꼴의 깔끔한 글자로: 펼치기 ›/‹ · 압정 · 설정 · 닫기
        using var icon = new Font(MainForm.IconFontName(), 11f);
        void Glyph(Rectangle rc, string glyph) => TextRenderer.DrawText(g, glyph, icon, rc, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var chev = new Font(MainForm.IconFontName(), 8.5f)) // › ‹ 는 다른 아이콘보다 조금 작게
            TextRenderer.DrawText(g, expanded ? "\uE76B" : "\uE76C", chev, foldRect, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        Glyph(pinRect, pinned ? "" : "");
        Glyph(gearRect, "");
        Glyph(closeRect, "");
    }
}
