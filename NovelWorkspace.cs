using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>
/// 작품 안의 작업 화면(Muvel의 워크스페이스). 작품에 들어가면 늘 이 화면이다.
/// [아이콘 막대] | [파일 트리 칸] | [문서 칸] (| [옆 문서 칸])
///  · 아이콘 막대: 로고(작품 목록으로) · 파일 트리 · 볼트 파일 · 검색 … 아래쪽 도움말 · 설정 · 전체 화면 · 항상 위.
///  · 파일 트리 칸: EpisodePanel(작품 카드 · 에피소드 · 위키 문서).
///  · 문서 칸: 머리(아이콘 · 이름 · ⋯ · ×) + 회차(MainForm) · 위키(WikiPage) · 비어 있으면 작품 화면(NovelOverview).
///    파일 트리에서 누르면 지금 고른 칸에, Ctrl+누르면 옆 칸에 나눠 연다. 칸마다 문서 하나(탭 없음).
/// 열어 둔 칸들은 작품마다 기억했다가(.caffeum.json의 Panes) 다음에 들어오면 그대로 연다.
/// </summary>
internal sealed class NovelWorkspace : Panel
{
    private readonly WorkspaceRail rail;
    private readonly MainHeaderBar mainHeader;
    private readonly SplitContainer outer, inner;
    private readonly PaneFrame treeFrame;
    private readonly StickyListPanel memoList; // 사이드바 [메모]: 파일 트리 자리에 대신 뜨는 메모 목록
    private readonly string treeGlyph;
    private bool memoShown;
    private readonly DocPane left, right;
    private DocPane focused;
    private LibraryPalette? palette;
    private bool chromeHidden;
    private bool restoring;
    private bool centerPending; // 나눈 칸을 반반으로 — 아직 폭이 안 잡혔으면 잡히는 대로

    public EpisodePanel Tree { get; }

    // ───────── 뽀모도로 타이머 위젯 ─────────
    private PomodoroWidget? pomo;
    private readonly System.Windows.Forms.Timer markTimer = new() { Interval = 300 };

    /// <summary>떠 있는 위젯(타이머 · 글자 수 세기)의 사이드바 아이콘을 눌린 모양으로 맞춘다.</summary>
    private void SyncWidgetMarks()
    {
        rail.SetActive("timer", pomo is { Visible: true });
        rail.SetActive("count", focused.CountWidgetOn);
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (IsHandleCreated) BeginInvoke(SyncPomodoro); }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke(SyncPomodoro);
        markTimer.Tick -= MarkTick; markTimer.Tick += MarkTick; markTimer.Start();
    }
    private void MarkTick(object? s, EventArgs e) { if (!IsDisposed) SyncWidgetMarks(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) markTimer.Dispose();
        if (disposing && pomo != null) { pomo.Parent?.Controls.Remove(pomo); pomo.Dispose(); pomo = null; }
        base.Dispose(disposing);
    }

    private void TogglePomodoro()
    {
        Pomodoro.Init();
        Pomodoro.S.WidgetShown = !Pomodoro.S.WidgetShown;
        Pomodoro.Save();
        SyncPomodoro();
    }

    private void SyncPomodoro()
    {
        if (IsDisposed) return;
        var host = TopLevelControl;
        bool onMain = inner.Panel2Collapsed && left.DocPath == null; // 소설 메인에서는 위젯을 숨긴다(문서에 들어가면 다시 나타남)
        bool show = Pomodoro.S.WidgetShown && Visible && !onMain && host != null && host.IsHandleCreated;
        if (!show)
        {
            // 에피소드(문서)에서 나가면 타이머도 끝낸다: 도는 중이던 시간과 소리가 멈추고 처음으로 돌아간다
            if (pomo is { Visible: true })
            {
                if (Pomodoro.State != PomoState.Idle) Pomodoro.Reset();
                Pomodoro.S.WidgetShown = false; // 다시 들어와도 저절로 뜨지 않는다(아이콘을 눌러야 켜진다)
            }
            if (pomo != null) pomo.Visible = false;
            return;
        }
        Pomodoro.Init();
        var pal = palette ?? LibraryPalette.From(AppShared.LibraryTheme);
        if (pomo == null)
        {
            pomo = new PomodoroWidget(pal) { Visible = false };
            pomo.CloseRequested += () => { Pomodoro.S.WidgetShown = false; Pomodoro.Save(); SyncPomodoro(); };
            pomo.ModeChanged += PlacePomodoro;
            pomo.Moved += () => { Pomodoro.S.PosX = pomo.Left; Pomodoro.S.PosY = pomo.Top; Pomodoro.Save(); };
            host!.SizeChanged += (_, _) => { if (pomo is { Visible: true }) PlacePomodoro(); };
        }
        else pomo.SetPalette(pal);
        if (pomo.Parent != host) { pomo.Parent?.Controls.Remove(pomo); host!.Controls.Add(pomo); }
        PlacePomodoro();
        pomo.Visible = true;
        pomo.BringToFront();
        SyncWidgetMarks();
    }

    private void PlacePomodoro()
    {
        var host = TopLevelControl;
        if (pomo == null || host == null) return;
        var size = PomodoroWidget.SizeFor(DeviceDpi);
        if (host.ClientSize.Width < size.Width || host.ClientSize.Height < size.Height) return;
        int x, y;
        if (Pomodoro.S.PosX >= 0) { x = Pomodoro.S.PosX; y = Pomodoro.S.PosY; }
        else { x = host.ClientSize.Width - size.Width - LogicalToDeviceUnits(24); y = LogicalToDeviceUnits(80); }
        x = Math.Clamp(x, 0, host.ClientSize.Width - size.Width);
        y = Math.Clamp(y, 0, host.ClientSize.Height - size.Height);
        pomo.Bounds = new Rectangle(new Point(x, y), size);
    }
    public string? NovelPath => Tree.NovelPath;
    public DocPane Focused => focused;

    public event Action? HomeRequested;
    public event Action? HelpRequested;
    public event Action? SettingsRequested;
    internal void RaiseSettings() => SettingsRequested?.Invoke();
    public event Action? FullScreenRequested;
    public event Action? PinRequested;
    public event Action? SearchRequested;
    /// <summary>몰입 모드로 아이콘 막대 · 칸 머리를 숨기거나 되돌릴 때(창 제목줄도 따라 숨기려고).</summary>
    public event Action<bool>? ChromeVisibilityChanged;
    /// <summary>창 제목을 바꿀 때(지금 고른 칸의 문서 이름).</summary>
    public event Action<string>? TitleChanged;

    public NovelWorkspace(Control vaultFiles)
    {
        DoubleBuffered = true;
        Tree = new EpisodePanel(vaultFiles) { Dock = DockStyle.Fill };
        Tree.BackRequested += () => HomeRequested?.Invoke();
        Tree.OverviewRequested += () => { focused.ShowOverview(); BackToMain(); FocusPane(focused); SaveLayout(); }; // 작품 카드를 누르면 소설 메인 화면으로
        Tree.OpenRequested += p => Open(p, split: false);
        Tree.OpenSplitRequested += p => Open(p, split: true);
        Tree.PipRequested += p => OpenPip(p);
        Tree.WikiOpenRequested += p => Open(p, split: false);
        Tree.WikiOpenSplitRequested += p => Open(p, split: true);
        Tree.CanvasRequested += g => { if (NovelPath is string d) Open(CanvasStore.PathFor(d, g), split: false); }; // 에피소드 그룹의 캔버스
        Tree.SnapDragging += ShowSnap;
        Tree.SnapDropped += DropSnap;
        Tree.WikiAboutToChange += p => CloseDocsUnder(p);
        var renamedPanes = new List<DocPane>();
        Tree.PathAboutToChange += p => { renamedPanes.Clear(); renamedPanes.AddRange(CloseDocsUnder(p)); };
        Tree.EpisodeRenamed += (_, np) => { foreach (var pane in renamedPanes) pane.Open(np); renamedPanes.Clear(); SaveLayout(); };

        treeFrame = new PaneFrame("", "파일 트리") { Dock = DockStyle.Fill };
        treeFrame.Body.Controls.Add(Tree);
        treeFrame.CloseRequested += () => SetTreeVisible(false);
        treeGlyph = treeFrame.Glyph;
        memoList = new StickyListPanel { Dock = DockStyle.Fill, Visible = false };
        treeFrame.Body.Controls.Add(memoList);
        StickyNotes.ListRequested += OnMemoListRequested;
        Disposed += (_, _) => StickyNotes.ListRequested -= OnMemoListRequested;

        left = new DocPane(this) { Dock = DockStyle.Fill };
        right = new DocPane(this) { Dock = DockStyle.Fill };
        focused = left;
        foreach (var pane in new[] { left, right })
        {
            pane.FocusRequested += FocusPane;
            pane.CloseRequested += ClosePane;
            pane.ContentChanged += () =>
            {
                bool has = left.DocPath != null;
                if (!restoring && leftHadDoc && !has) BackToMain(); // 문서가 닫혀 소설 메인으로 돌아왔다(위키의 [뒤로] · 문서 창 닫힘 등)
                leftHadDoc = has;
                SyncFocusVisuals();
                SaveLayout();
            };
            pane.HomeRequested += () => HomeRequested?.Invoke();
        }

        inner = new SplitContainer { Dock = DockStyle.Fill, Panel2Collapsed = true, SplitterWidth = 4 };
        inner.SizeChanged += (_, _) => { if (centerPending && inner.Width > 200) { centerPending = false; inner.SplitterDistance = inner.Width / 2; } };
        inner.Panel1.Controls.Add(left);
        inner.Panel2.Controls.Add(right);
        outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 4 };
        outer.Panel1.Controls.Add(treeFrame);
        outer.Panel2.Controls.Add(inner);

        rail = new WorkspaceRail { Dock = DockStyle.Left };
        rail.Clicked += OnRail;
        mainHeader = new MainHeaderBar { Dock = DockStyle.Top, Visible = false };
        mainHeader.Clicked += OnHeader;

        Controls.Add(outer);
        Controls.Add(rail);
        Controls.Add(mainHeader);
        // 핸들이 생길 때는 아직 폭이 작아서 SplitterDistance가 반토막으로 눌린다 — 폭이 잡힌 뒤 한 번만 정한다.
        HandleCreated += (_, _) => outer.SplitterWidth = inner.SplitterWidth = Math.Max(3, LogicalToDeviceUnits(4));
        outer.SizeChanged += (_, _) => ApplyTreeWidth();
        SyncRail();
    }

    // ───────── 작품 열기 · 닫기 ─────────

    /// <summary>이 작품을 연다. 지난번에 열어 둔 칸들을 되살린다.</summary>
    public void LoadNovel(string novelDir)
    {
        CloseAll();
        Tree.LoadNovel(novelDir);
        memoList.SetNovel(novelDir);
        ShowMemo(false);
        restoring = true;
        try
        {
            left.ShowOverview();
            right.ShowOverview();
            inner.Panel2Collapsed = true;
            // 작품을 열면 늘 소설 메인에서 시작한다(마지막에 열어 둔 회차를 되살리지 않는다).
            focused = left;
            SetTreeVisible(left.DocPath != null); // 소설 메인으로 들어갈 때는 파일 트리를 닫아 둔다(문서가 열려 있던 작품은 그대로 연다)
            leftHadDoc = left.DocPath != null;
        }
        finally { restoring = false; }
        SyncFocusVisuals();
    }

    /// <summary>열린 문서를 모두 저장하고 닫는다(작품 목록으로 나갈 때 · 창을 닫을 때).</summary>
    public void CloseAll()
    {
        SaveLayout();
        restoring = true;
        try { left.CloseContent(); right.CloseContent(); }
        finally { restoring = false; }
    }

    private void SaveLayout()
    {
        if (restoring || NovelPath is not string dir) return;
        var list = new List<string>();
        foreach (var pane in inner.Panel2Collapsed ? new[] { left } : new[] { left, right })
            if (pane.DocPath is string p && !NovelStore.IsReferencePath(p)) list.Add(Path.GetRelativePath(dir, p));
        var meta = NovelStore.LoadMeta(dir);
        if ((meta.Panes ?? new()).SequenceEqual(list)) return;
        meta.Panes = list;
        NovelStore.SaveMeta(dir, meta);
    }

    /// <summary>문서를 연다. 이미 어느 칸에 열려 있으면 그 칸을 고른다. split이면 옆 칸에.</summary>
    public void Open(string path, bool split)
    {
        string full = Path.GetFullPath(path);
        if (outer.Panel1Collapsed && !chromeHidden && !restoring) { ShowMemo(false); SetTreeVisible(true); Tree.ShowPage(0); treeFrame.Title = "파일 트리"; } // 닫혀 있던 파일 트리는 문서를 열면 자동으로 켠다
        var already = new[] { left, right }.FirstOrDefault(p => (p == left || !inner.Panel2Collapsed) && p.HasOpen(full));
        if (already != null)
        {
            if (!string.Equals(already.DocPath, full, StringComparison.OrdinalIgnoreCase)) already.Open(full); // 숨어 있던 참고 자료 탭이면 그 탭으로
            FocusPane(already);
            return;
        }
        DocPane target = focused;
        if (split)
        {
            if (inner.Panel2Collapsed) { inner.Panel2Collapsed = false; CenterSplit(); right.ShowOverview(); }
            target = focused == left ? right : left;
        }
        target.Open(full);
        FocusPane(target);
        SaveLayout();
    }

    // ───────── 끌어서 열기(창 스냅처럼) ─────────

    private SnapPreview? snapPreview;

    /// <summary>
    /// 파일 트리에서 끌고 온 문서를 놓을 칸. 나뉘어 있으면 마우스 밑 칸, 한 칸뿐이면 왼쪽 반 · 오른쪽 반
    /// (소설 메인뿐이면 그 칸 전체). area는 미리보기 자리(화면 좌표). 문서 칸 밖이면 null.
    /// </summary>
    private DocPane? SnapTarget(Point screen, out Rectangle area)
    {
        area = Rectangle.Empty;
        var pt = inner.PointToClient(screen);
        if (!inner.ClientRectangle.Contains(pt)) return null;
        if (!inner.Panel2Collapsed)
        {
            bool r = pt.X >= inner.Panel2.Left;
            area = inner.RectangleToScreen((r ? inner.Panel2 : inner.Panel1).Bounds);
            return r ? right : left;
        }
        if (left.DocPath == null) { area = inner.RectangleToScreen(inner.ClientRectangle); return left; }
        int half = inner.Width / 2;
        bool onRight = pt.X >= half;
        area = inner.RectangleToScreen(onRight ? new Rectangle(half, 0, inner.Width - half, inner.Height) : new Rectangle(0, 0, half, inner.Height));
        return onRight ? right : left;
    }

    private void ShowSnap(Point screen)
    {
        if (SnapTarget(screen, out var area) == null) { snapPreview?.Hide(); return; }
        int pad = LogicalToDeviceUnits(6);
        area.Inflate(-pad, -pad);
        snapPreview ??= new SnapPreview();
        snapPreview.BackColor = palette?.Accent ?? SystemColors.Highlight;
        if (snapPreview.Bounds != area) snapPreview.Bounds = area;
        if (!snapPreview.Visible) snapPreview.Show(FindForm());
    }

    private void DropSnap(string path, Point? screen)
    {
        snapPreview?.Hide();
        if (screen is not Point pt || SnapTarget(pt, out _) is not DocPane target) return;
        string full = Path.GetFullPath(path);
        bool Same(string? p) => p != null && string.Equals(p, full, StringComparison.OrdinalIgnoreCase);
        if (!inner.Panel2Collapsed || left.DocPath == null)
        {
            var other = target == left ? right : left;
            if (!inner.Panel2Collapsed && Same(other.DocPath) && target.DocPath is string mine && !NovelStore.IsReferencePath(mine))
            {
                // 옆 칸에 열려 있던 문서를 이쪽으로 끌었다: 두 칸이 자리를 바꾼다
                restoring = true;
                try { other.CloseContent(); target.Open(full); other.Open(mine); }
                finally { restoring = false; }
                leftHadDoc = left.DocPath != null;
                SyncFocusVisuals();
                FocusPane(target);
                SaveLayout();
                return;
            }
            if (!inner.Panel2Collapsed && Same(other.DocPath)) { FocusPane(other); return; }
            if (!Same(target.DocPath)) target.Open(full);
            FocusPane(target);
            SaveLayout();
            return;
        }
        // 한 칸에 문서가 열려 있다: 둘로 나눠 놓은 쪽에 열고, 있던 문서는 반대쪽으로
        if (Same(left.DocPath)) { FocusPane(left); return; }
        inner.Panel2Collapsed = false;
        CenterSplit();
        if (target == left && left.DocPath is string moving && !NovelStore.IsReferencePath(moving))
        {
            restoring = true;
            try { left.CloseContent(); right.Open(moving); left.Open(full); }
            finally { restoring = false; }
            leftHadDoc = true;
        }
        else
        {
            target = right; // 참고 자료 탭 묶음은 옮기지 않고 새 문서를 오른쪽에
            right.Open(full);
        }
        SyncFocusVisuals();
        FocusPane(target);
        SaveLayout();
    }

    private void CenterSplit()
    {
        if (inner.Width > 200) inner.SplitterDistance = inner.Width / 2;
        else centerPending = true;
    }

    /// <summary>이 경로(폴더면 그 안 전부)를 연 칸을 닫는다. 닫은 칸들을 돌려준다(이름 바꾼 뒤 다시 열 때).</summary>
    public List<DocPane> CloseDocsUnder(string path)
    {
        var closed = new List<DocPane>();
        foreach (var pane in new[] { left, right })
        {
            if (pane.CloseRefsUnder(path)) closed.Add(pane); // 참고 자료 탭은 그 탭만 닫는다
            string? p = pane.DocPath;
            if (p != null && (string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                pane.ShowOverview();
                closed.Add(pane);
            }
        }
        return closed;
    }

    private void FocusPane(DocPane pane)
    {
        if (pane == right && inner.Panel2Collapsed) pane = left;
        focused = pane;
        SyncFocusVisuals();
        Tree.MarkCurrent(pane.DocPath);
        TitleChanged?.Invoke(pane.DocPath is string p ? Path.GetFileNameWithoutExtension(p) : Path.GetFileName(NovelPath ?? ""));
    }

    private void SyncFocusVisuals()
    {
        bool two = !inner.Panel2Collapsed;
        left.Focused = two && focused == left;
        right.Focused = two && focused == right;
        UpdateChrome(); // 소설 메인이면 막대 대신 위쪽 머리 줄, 문서 안이면 왼쪽 아이콘 막대
        // 소설 메인 화면 한 칸뿐이면 [×]는 할 일이 없으니 숨긴다(문서가 열려 있거나 칸이 둘이면 보인다)
        left.ShowClose = two || left.DocPath != null;
        right.ShowClose = true;
        Tree.MarkCurrent(focused.DocPath);
    }

    /// <summary>칸의 × : 옆 칸이 있으면 이 칸을 없애고(오른쪽 칸 문서는 왼쪽으로), 하나뿐이면 작품 화면으로.</summary>
    private void ClosePane(DocPane pane)
    {
        if (inner.Panel2Collapsed) { left.ShowOverview(); BackToMain(); FocusPane(left); SaveLayout(); return; }
        if (pane == left)
        {
            string? moving = right.DocPath;
            right.CloseContent();
            if (moving != null) left.Open(moving); else left.ShowOverview();
        }
        else right.CloseContent();
        inner.Panel2Collapsed = true;
        FocusPane(left);
        SaveLayout();
    }

    /// <summary>참고 자료를 항상 위에 뜨는 작은 창(PIP)으로 옮긴다. 칸의 탭은 닫는다.</summary>
    public void OpenPip(string path)
    {
        BeginInvoke(() => // 눌린 버튼이 든 문서를 그 자리에서 닫지 않게 한 박자 늦춘다
        {
            var held = new List<DocPane>();
            foreach (var pane in new[] { left, right }) if (pane.BeginPip(path)) held.Add(pane); // 참고 자료 탭은 칸을 그대로 두고 안내 글만 남긴다
            if (held.Count == 0) CloseDocsUnder(path); // 회차 · 위키는 같은 문서를 두 곳에서 고치지 않게 칸을 닫는다
            var pip = new PipForm(path);
            pip.FormClosed += (_, _) => { foreach (var pane in held) if (!pane.IsDisposed) pane.EndPip(path); };
            pip.Show(); // 소유 창으로 두면 작업표시줄에 따로 안 뜬다
        });
    }

    /// <summary>창 제목을 지금 고른 칸 기준으로 다시 알린다(작품 이름을 바꾼 뒤).</summary>
    public void RefreshTitle() => FocusPane(focused);

    /// <summary>창의 항상 위 상태를 머리 줄의 압정 아이콘에 알린다.</summary>
    public void SetPinned(bool on) => mainHeader.Pinned = on;

    /// <summary>창의 전체 화면 상태를 머리 줄의 아이콘에 알린다.</summary>
    public void SetFullScreenIcon(bool on) => mainHeader.FullOn = on;

    /// <summary>Ctrl+W: 지금 고른 칸을 닫는다.</summary>
    public void CloseFocused() => ClosePane(focused);

    /// <summary>Ctrl+Tab: 옆 칸으로.</summary>
    public void CycleFocus() { if (!inner.Panel2Collapsed) FocusPane(focused == left ? right : left); }

    // ───────── 아이콘 막대 ─────────

    private void OnRail(string key)
    {
        switch (key)
        {
            case "library": HomeRequested?.Invoke(); break; // 서재 아이콘: 작품 목록으로
            case "novel": focused.ShowOverview(); BackToMain(); FocusPane(focused); SaveLayout(); break; // 책 아이콘: 소설 메인으로
            case "back": HomeRequested?.Invoke(); break;
            case "memo": // 파일 트리를 닫고 그 자리에 메모 목록을 띄운다(다시 누르면 닫는다)
                if (outer.Panel1Collapsed || !memoShown) { ShowMemo(true); SetTreeVisible(true); }
                else SetTreeVisible(false);
                break;
            case "tree":
                if (outer.Panel1Collapsed || memoShown || Tree.ShowingFiles) { ShowMemo(false); SetTreeVisible(true); Tree.ShowPage(0); treeFrame.Title = "파일 트리"; }
                else SetTreeVisible(false);
                break;
            case "files":
                if (outer.Panel1Collapsed || memoShown || !Tree.ShowingFiles) { ShowMemo(false); SetTreeVisible(true); Tree.ShowPage(2); treeFrame.Title = "볼트 파일"; }
                else SetTreeVisible(false);
                break;
            case "search": NotReady("검색 보드"); break;
            case "timer": TogglePomodoro(); break; // 뽀모도로 타이머 위젯 켜고 끄기
            case "count": focused.ToggleCountWidget(); break; // 글자 수 세기 위젯 켜고 끄기
            case "help": HelpRequested?.Invoke(); break;
            case "settings": SettingsRequested?.Invoke(); break;
            case "full": FullScreenRequested?.Invoke(); break;
            case "pin": PinRequested?.Invoke(); break;
        }
        SyncRail();
    }

    /// <summary>소설 메인(칸이 하나뿐이고 문서가 없을 때)에는 왼쪽 막대 대신 위쪽 머리 줄을, 문서 안에서는 왼쪽 막대를 보인다. 몰입 모드에서는 둘 다 숨긴다.</summary>
    private void UpdateChrome()
    {
        bool onMain = inner.Panel2Collapsed && left.DocPath == null;
        rail.Visible = !chromeHidden && !onMain;
        rail.DocMode = !onMain;
        if (IsHandleCreated) BeginInvoke(SyncPomodoro);
        mainHeader.Visible = !chromeHidden && onMain;
        left.HeaderVisible = !chromeHidden && !onMain; // 소설 메인에는 칸 머리 줄(이름 · ×)도 없다
        right.HeaderVisible = !chromeHidden;
    }

    /// <summary>소설 메인 머리 줄의 로고 · 버튼.</summary>
    private void OnHeader(string key)
    {
        switch (key)
        {
            case "logo": HomeRequested?.Invoke(); break;
            case "help": HelpRequested?.Invoke(); break;
            case "pin": PinRequested?.Invoke(); break;
            case "full": FullScreenRequested?.Invoke(); break;
            case "settings": SettingsRequested?.Invoke(); break;
        }
    }

    /// <summary>아직 기능을 넣지 않은 아이콘(검색 보드).</summary>
    private void NotReady(string what) =>
        MessageBox.Show(FindForm(), $"{what}는 아직 준비 중이에요. 아이콘 자리만 먼저 만들어 두었어요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>설정 > 아이콘 막대의 순서 · 켜고 끄기를 반영한다.</summary>
    public void ApplyRailSettings() { rail.ApplySettings(AppShared.Settings); SyncRail(); }

    private bool leftHadDoc;

    /// <summary>에피소드나 위키에 들어갔다가 소설 메인으로 돌아올 때: 칸이 하나뿐이면 파일 트리를 다시 닫는다.</summary>
    private void BackToMain()
    {
        leftHadDoc = false;
        if (inner.Panel2Collapsed && !chromeHidden) SetTreeVisible(false);
    }

    private bool treeWidthPending = true;

    /// <summary>파일 트리 칸 폭(340)을 폭이 잡힌 뒤 한 번만 정한다. 닫혀 있으면 열릴 때 정한다.</summary>
    private void ApplyTreeWidth()
    {
        if (!treeWidthPending || outer.Panel1Collapsed || outer.Width < LogicalToDeviceUnits(700)) return;
        treeWidthPending = false;
        outer.SplitterDistance = LogicalToDeviceUnits(340);
    }

    private void SetTreeVisible(bool v) { outer.Panel1Collapsed = !v; ApplyTreeWidth(); SyncRail(); }

    private void SyncRail()
    {
        rail.Selected = outer.Panel1Collapsed ? null : memoShown ? "memo" : Tree.ShowingFiles ? "files" : "tree";
        SyncWidgetMarks();
    }

    /// <summary>왼쪽 칸에 메모 목록(true) 또는 파일 트리(false)를 보인다.</summary>
    private void ShowMemo(bool on)
    {
        memoShown = on;
        memoList.Visible = on;
        Tree.Visible = !on;
        if (on) { treeFrame.Title = "메모"; treeFrame.Glyph = WorkspaceRail.Catalog.First(c => c.Key == "memo").Glyph; memoList.BringToFront(); }
        else { treeFrame.Title = Tree.ShowingFiles ? "볼트 파일" : "파일 트리"; treeFrame.Glyph = treeGlyph; }
    }

    /// <summary>메모 창의 [메모 목록]: 이 작품의 메모면 왼쪽 칸에 목록을 띄운다.</summary>
    private void OnMemoListRequested(string dir)
    {
        if (!string.Equals(dir, NovelPath, StringComparison.OrdinalIgnoreCase)) return;
        ShowMemo(true);
        SetTreeVisible(true);
        FindForm()?.Activate();
    }

    /// <summary>몰입 모드: 아이콘 막대 · 파일 트리 · 칸 머리를 숨긴다(문서의 툴바와 함께).</summary>
    public void SetChromeVisible(bool v)
    {
        if (chromeHidden == !v) return;
        chromeHidden = !v;
        UpdateChrome();
        ChromeVisibilityChanged?.Invoke(v);
        outer.Panel1Collapsed = !v || outer.Panel1Collapsed && !v;
        if (v) SyncRail();
    }

    // ───────── 테마 ─────────

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = outer.BackColor = inner.BackColor = p.Line;
        outer.Panel1.BackColor = p.Side;
        rail.SetPalette(p);
        mainHeader.SetPalette(p);
        treeFrame.SetPalette(p);
        Tree.ApplyTheme(p);
        memoList.ApplyTheme(p);
        left.ApplyTheme(p);
        right.ApplyTheme(p);
    }

    public void ApplyFont(Font f) => Tree.ApplyFont(f);
}

/// <summary>
/// 왼쪽 끝 아이콘 막대(문서 안에서만 보인다 — 소설 메인에는 위쪽 머리 줄이 대신한다). 위에서부터 [서재] [책 = 소설 메인] [파일 트리] [메모] [검색], 맨 아래 [설정].
/// 항목의 순서 · 켜고 끄기는 설정 > 아이콘 막대에서 정한다. "spacer"(── 여기부터 아래쪽 ──) 뒤의 항목은 막대 아래쪽에 붙는다.
/// </summary>
internal sealed class WorkspaceRail : Control
{
    /// <summary>아이콘 막대에 둘 수 있는 항목(키, 아이콘, 이름). 기본 순서이기도 하다.</summary>
    public static readonly (string Key, string Glyph, string Label)[] Catalog =
    {
        ("library", "", "서재로 돌아가기"),
        ("novel", "", "소설 메인으로"),
        ("tree", "", "파일 트리"),
        ("memo", "", "메모"),
        ("search", "", "검색 보드"),
        ("timer", "\uE916", "뽀모도로 타이머"),
        ("count", "", "글자 수 세기"), // 아이콘은 직접 그린다(WorkspaceRail.OnPaint)
        ("spacer", "", "── 여기부터 아래쪽 ──"),
        ("settings", "", "설정 (Ctrl+,)"),
    };

    /// <summary>설정에 저장된 순서(모르는 키는 빼고, 빠진 항목은 기본 자리 근처에 끼워 넣는다).</summary>
    /// <summary>사용자가 직접 넣은 항목: 구분선(sepX1 …) · 빈 아이콘(blankX1 …).</summary>
    public static bool IsCustom(string k) => k.StartsWith("sepX") || k.StartsWith("blankX");

    public static List<string> OrderFor(AppSettings s)
    {
        var known = Catalog.Select(c => c.Key).ToList();
        var order = s.RailOrder.Where(k => known.Contains(k) || IsCustom(k)).Distinct().ToList();
        foreach (var k in known.Where(k => !order.Contains(k)))
        {
            int at = known.IndexOf(k);
            string? before = known.Take(at).LastOrDefault(order.Contains);
            order.Insert(before == null ? 0 : order.IndexOf(before) + 1, k);
        }
        return order;
    }

    public static string LabelOf(string key) => key.StartsWith("sepX") ? "구분선" : key.StartsWith("blankX") ? "빈 아이콘" : Catalog.FirstOrDefault(c => c.Key == key).Label ?? key;

    private LibraryPalette? palette;
    private int hover = -1;
    private string? selected;
    private readonly ToolTip tip = new();
    private List<(string Key, string Glyph, string Label, bool Bottom)> items = new();
    public event Action<string>? Clicked;

    public WorkspaceRail()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ApplySettings(AppShared.Settings);
    }

    /// <summary>설정의 순서 · 숨김을 반영한다.</summary>
    private bool docMode;
    /// <summary>문서 안인지(파일 트리 · 메모 · 검색 아이콘은 문서 안에서만 보인다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DocMode { get => docMode; set { if (docMode == value) return; docMode = value; ApplySettings(AppShared.Settings); } }

    public void ApplySettings(AppSettings s)
    {
        var list = new List<(string, string, string, bool)>();
        bool bottom = false;
        foreach (var k in OrderFor(s))
        {
            if (k == "spacer") { bottom = true; continue; }
            if (s.RailHidden.Contains(k)) continue;
            if (k is "tree" or "memo" or "search" or "count" && !docMode) continue;
            if (IsCustom(k)) { list.Add((k, "", LabelOf(k), bottom)); continue; }
            var c = Catalog.First(x => x.Key == k);
            list.Add((c.Key, c.Glyph, c.Label, bottom));
        }
        items = list;
        hover = -1;
        Invalidate();
    }

    private readonly HashSet<string> active = new();
    /// <summary>눌러 둔 채로 켜져 있는 항목(떠 있는 위젯: 타이머 · 글자 수 세기)을 눌린 모양으로 보인다.</summary>
    public void SetActive(string key, bool on) { if (on ? active.Add(key) : active.Remove(key)) Invalidate(); }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Width = S(42); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    private bool IsSep(int i) => items[i].Key.StartsWith("sepX");
    private bool IsBlank(int i) => items[i].Key.StartsWith("blankX");
    private int ItemH(int i) => IsSep(i) ? S(12) : S(34); // 구분선은 얇은 칸

    private Rectangle ItemRect(int i)
    {
        int size = S(34), x = (Width - size) / 2, gap = S(1); // 아이콘 칸 34 · 칸 사이 1(아주 촘촘하게)
        int h = ItemH(i);
        if (!items[i].Bottom)
        {
            int y = S(10);
            for (int k = 0; k < i; k++) if (!items[k].Bottom) y += ItemH(k) + gap;
            return new Rectangle(x, y, size, h);
        }
        int fromBottom = 0; // 아래쪽 묶음에서 이 항목과 그 뒤 항목들의 높이 합
        for (int k = i; k < items.Count; k++) if (items[k].Bottom) fromBottom += ItemH(k) + gap;
        return new Rectangle(x, Height - S(12) - fromBottom + gap, size, h);
    }

    /// <summary>항목 i의 화면 좌표 아래쪽(메뉴를 띄울 자리).</summary>
    public Point MenuAnchor(string key)
    {
        int i = items.FindIndex(it => it.Key == key);
        if (i < 0) return PointToScreen(new Point(Width, S(14)));
        var r = ItemRect(i);
        return PointToScreen(new Point(r.Right + S(4), r.Top));
    }

    private int IndexAt(Point pt)
    {
        for (int i = 0; i < items.Count; i++) if (!IsSep(i) && !IsBlank(i) && ItemRect(i).Contains(pt)) return i; // 구분선 · 빈 아이콘은 누를 수 없다
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i == hover) return;
        hover = i;
        Cursor = i != -1 ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, i >= 0 ? items[i].Label : null);
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = IndexAt(e.Location);
        if (i >= 0) Clicked?.Invoke(items[i].Key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(p?.Side ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var icon = new Font(MainForm.IconFontName(), 12.5f);
        for (int i = 0; i < items.Count; i++)
        {
            var r = ItemRect(i);
            if (IsSep(i)) { using var sp = new Pen(p.Line); g.DrawLine(sp, r.Left + S(6), r.Top + r.Height / 2, r.Right - S(6), r.Top + r.Height / 2); continue; }
            if (IsBlank(i)) continue;
            bool sel = items[i].Key == selected || active.Contains(items[i].Key);
            if (sel || i == hover)
            {
                using var path = LibraryPalette.Rounded(r, S(8));
                using var b = new SolidBrush(sel ? Themes.Blend(p.Side, p.Fore, p.Dark ? 0.12f : 0.08f) : p.CardHover);
                g.FillPath(b, path);
            }
            if (items[i].Key == "count")
            {
                int cs = S(20);
                using var cb = PurpleIcons.CountDoc(cs * 3, sel ? p.Fore : p.Dim);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(cb, new Rectangle(r.X + (r.Width - cs) / 2, r.Y + (r.Height - cs) / 2, cs, cs));
                continue;
            }
            TextRenderer.DrawText(g, items[i].Glyph, icon, r, sel ? p.Fore : p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        using (var pen = new Pen(p.Line)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }
}

/// <summary>칸 하나의 틀: 머리 줄(아이콘 · 이름 · [⋯] · [×]) + 몸통. 고른 칸은 이름 옆에 강조색 점.</summary>
internal class PaneFrame : Panel
{
    private readonly PaneHeader header;
    public Panel Body { get; }
    public event Action? CloseRequested;
    public event Action<Control, Point>? MoreRequested;
    public event Action? HeaderClicked;

    public PaneFrame(string glyph, string title, bool showMore = false)
    {
        header = new PaneHeader { Dock = DockStyle.Top, Glyph = glyph, Title = title, ShowMore = showMore };
        header.CloseClicked += () => CloseRequested?.Invoke();
        header.MoreClicked += pt => MoreRequested?.Invoke(header, pt);
        header.Clicked += () => HeaderClicked?.Invoke();
        Body = new Panel { Dock = DockStyle.Fill };
        Controls.Add(Body);
        Controls.Add(header);
    }

[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title { get => header.Title; set { header.Title = value; header.Invalidate(); } }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => header.Glyph; set { header.Glyph = value; header.Invalidate(); } }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dot { get => header.Dot; set { if (header.Dot == value) return; header.Dot = value; header.Invalidate(); } }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    /// <summary>점 모양: 0 = 이 칸을 골랐다(강조색 점) · 1 = 저장 안 됨(검은 테두리 + 흰색) · 2 = 저장됨(검은 테두리 + 검은색).</summary>
    public int DotStyle { get => header.DotStyle; set { if (header.DotStyle == value) return; header.DotStyle = value; header.Invalidate(); } }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool HeaderVisible { get => header.Visible; set => header.Visible = value; }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowMore { set { header.ShowMore = value; header.Invalidate(); } }
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    /// <summary>머리줄 오른쪽 끝 [×](칸 닫기). 소설 메인 화면 한 칸뿐일 때는 할 일이 없어서 숨긴다.</summary>
    public bool ShowClose { set { header.ShowClose = value; header.Invalidate(); } }

    public virtual void SetPalette(LibraryPalette p) { header.SetPalette(p); BackColor = Body.BackColor = p.Main; }

    private sealed class PaneHeader : Control
    {
        private LibraryPalette? palette;
        private int hover; // 0 없음, 1 ⋯, 2 ×
        public string Glyph = "", Title = "";
        public bool ShowMore, Dot, ShowClose = true;
        public int DotStyle;
        public event Action? CloseClicked, Clicked;
        public event Action<Point>? MoreClicked;

        public PaneHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font(NovelStore.UiFontFamily, 9f);
        }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(34); }
        private Rectangle CloseRect => new(Width - S(30), (Height - S(24)) / 2, S(24), S(24));
        private Rectangle MoreRect => new(Width - S(56), (Height - S(24)) / 2, S(24), S(24));
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = ShowClose && CloseRect.Contains(e.Location) ? 2 : ShowMore && MoreRect.Contains(e.Location) ? 1 : 0;
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h > 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = 0; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (ShowClose && CloseRect.Contains(e.Location)) CloseClicked?.Invoke();
            else if (ShowMore && MoreRect.Contains(e.Location)) MoreClicked?.Invoke(new Point(MoreRect.Left, MoreRect.Bottom));
            else Clicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            var g = e.Graphics;
            g.Clear(p?.Main ?? BackColor);
            if (p == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            using var icon = new Font(MainForm.IconFontName(), 9.5f);
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(S(12), 0, S(18), Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
            int tw = Math.Min(TextRenderer.MeasureText(Title, Font, Size.Empty, TextFormatFlags.NoPadding).Width, Width - S(100));
            TextRenderer.DrawText(g, Title, Font, new Rectangle(S(36), 0, Math.Max(0, tw + 2), Height), p.Dim, flags);
            if (Dot && DotStyle == 0) using (var db = new SolidBrush(p.Accent)) g.FillEllipse(db, S(36) + tw + S(8), Height / 2 - S(3), S(6), S(6));
            else if (Dot)
            {
                // 저장 표시: 늘 회색 테두리가 있고, 저장 안 됨 = 안이 흰색 · 저장됨 = 안이 회색
                float d = 6.5f * DeviceDpi / 96f, dx = S(36) + tw + S(9), dy = Height / 2f - d / 2f;
                var prevSm = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var fb = new SolidBrush(DotStyle == 2 ? Color.FromArgb(150, 148, 146) : Color.White)) g.FillEllipse(fb, dx, dy, d, d);
                using (var op = new Pen(Color.FromArgb(150, 148, 146), Math.Max(1.2f, DeviceDpi / 96f * 1.3f))) g.DrawEllipse(op, dx, dy, d, d);
                g.SmoothingMode = prevSm;
            }
            foreach (var (r, glyph, h) in new[] { (CloseRect, "", 2), (MoreRect, "", 1) })
            {
                if (h == 1 && !ShowMore) continue;
                if (h == 2 && !ShowClose) continue;
                if (hover == h) using (var hb = new SolidBrush(p.CardHover)) using (var hp = LibraryPalette.Rounded(r, S(5))) g.FillPath(hb, hp);
                using var f = new Font(MainForm.IconFontName(), 8f);
                TextRenderer.DrawText(g, glyph, f, r, p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            using var pen = new Pen(p.Line);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}

/// <summary>문서 칸: 회차(MainForm) · 위키 문서(WikiPage) · 비어 있으면 작품 화면(NovelOverview) 가운데 하나를 보여 준다.</summary>
internal sealed class DocPane : PaneFrame
{
    private readonly NovelWorkspace owner;
    private MainForm? doc;                       // 지금 보이는 회차 · 참고 자료 문서(참고 자료 탭 묶음이면 그중 고른 탭)
    private readonly List<(string Path, MainForm? Doc)> refTabs = new(); // 이 칸에 열어 둔 참고 자료들(탭마다 독립된 문서)
    private readonly DocTabStrip tabStrip = new() { Dock = DockStyle.Top, Visible = false };
    // PIP로 내보낸 참고 자료 탭 자리에 남겨 두는 안내(네모는 그대로 두고 가운데에 글만): "PIP 모드에서 진행 중"
    private readonly Label pipNote = new() { Text = "PIP 모드에서 진행 중", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false, UseMnemonic = false, AutoSize = false };
    private WikiPage? wiki;
    private PlotCanvasPage? canvas; // 에피소드 그룹의 플롯 캔버스
    private NovelOverview? overview;
    private LibraryPalette? palette;

    public string? DocPath { get; private set; }
    /// <summary>이 칸에 열린 에피소드의 글자 수 세기 위젯을 켜고 끈다(에피소드가 아니면 아무 일도 없다).</summary>
    public void ToggleCountWidget() => doc?.ToggleCountWidget();
    /// <summary>글자 수 세기 위젯이 지금 떠 있는가.</summary>
    public bool CountWidgetOn => doc?.GoalWidgetOn ?? false;
    public event Action<DocPane>? FocusRequested;
    public event Action<DocPane>? CloseRequested;
    public event Action? ContentChanged;
    public event Action? HomeRequested;

    public DocPane(NovelWorkspace owner) : base("", "작품")
    {
        this.owner = owner;
        base.CloseRequested += () => CloseRequested?.Invoke(this);
        HeaderClicked += () => FocusRequested?.Invoke(this);
        MoreRequested += ShowPaneMenu;
        Body.ControlAdded += (_, e) => Hook(e.Control!);
        tabStrip.Height = LogicalToDeviceUnits(36);
        pipNote.Font = new Font(NovelStore.UiFontFamily, 16f);
        Body.Controls.Add(pipNote);
        tabStrip.TabSelected += key =>
        {
            int i = refTabs.FindIndex(t => t.Path.Equals(key as string, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            ActivateRef(i);
            ContentChanged?.Invoke();
            FocusRequested?.Invoke(this);
        };
        tabStrip.TabCloseRequested += key =>
        {
            int i = refTabs.FindIndex(t => t.Path.Equals(key as string, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) CloseRefTab(i, closeForm: true);
        };
        Body.Controls.Add(tabStrip);
    }

    /// <summary>칸 안 어디를 눌러도(글 상자 · 목록 …) 이 칸을 고른 것으로.</summary>
    private void Hook(Control c)
    {
        c.Enter += (_, _) => FocusRequested?.Invoke(this);
        c.MouseDown += (_, _) => FocusRequested?.Invoke(this);
        foreach (Control child in c.Controls) Hook(child);
        c.ControlAdded += (_, e) => { if (e.Control != null) Hook(e.Control); };
    }

[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new bool Focused { set { focusDot = value; UpdateDot(); } }

    // 칸 머리 제목 옆 보라색 점: 회차를 보고 있으면 "저장됨"을, 그 밖(소설 메인 · 위키 · 참고 자료)에는 이 칸을 골랐다는 뜻을 나타낸다.
    private bool focusDot;
    private bool? savedDot;
    private void UpdateDot()
    {
        DotStyle = savedDot == null ? 0 : savedDot.Value ? 2 : 1;
        Dot = savedDot != null || focusDot;
    }

    public override void SetPalette(LibraryPalette p) => ApplyTheme(p);

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        base.SetPalette(p);
        wiki?.ApplyTheme(p);
        canvas?.ApplyTheme(p);
        overview?.ApplyTheme(p);
        tabStrip.SetPalette(p);
    }

    private static bool IsWiki(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "wiki", StringComparison.OrdinalIgnoreCase);

    /// <summary>문서를 이 칸에 연다(있던 문서는 저장하고 닫는다).</summary>
    public void Open(string path)
    {
        if (IsRefFile(path)) { OpenRef(path); return; } // 참고 자료는 한 칸 안에서 탭으로 여러 개 연다
        CloseContent();
        DocPath = path;
        if (CanvasStore.IsCanvas(path))
        {
            // 플롯 캔버스: 노드를 두 번 누르면 원본을 옆 칸에 연다(캔버스는 그대로 두고)
            canvas = new PlotCanvasPage(path, owner.NovelPath ?? Path.GetDirectoryName(Path.GetDirectoryName(path))!) { Dock = DockStyle.Fill };
            if (palette != null) canvas.ApplyTheme(palette);
            canvas.OpenRequested += p => owner.Open(p, split: true);
            Body.Controls.Add(canvas);
            canvas.BringToFront();
            Glyph = CanvasStore.Glyph;
            Title = Path.GetFileNameWithoutExtension(path) + " 캔버스";
            ShowMore = true;
        }
        else if (IsWiki(path))
        {
            wiki = new WikiPage { Dock = DockStyle.Fill, ShowTopBar = false };
            if (palette != null) wiki.ApplyTheme(palette);
            wiki.Changed += () => owner.Tree.ReloadWiki();
            wiki.BackRequested += () => { ShowOverview(); ContentChanged?.Invoke(); };
            Body.Controls.Add(wiki);
            wiki.Open(path, owner.NovelPath ?? Path.GetDirectoryName(Path.GetDirectoryName(path))!);
            var d = TryLoadWiki(path);
            Glyph = d != null ? WikiCategory.Find(d.Category).Glyph : "";
            Title = Path.GetFileNameWithoutExtension(path);
            ShowMore = true;
        }
        else
        {
            var m = doc = CreateDoc(path);
            savedDot = null; UpdateDot();
            Body.Controls.Add(m);
            m.Show();
            tabStrip.SendToBack(); // 탭 막대는 항상 맨 위(문서 칸보다 먼저 자리를 잡게)
            bool isRef = string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), NovelStore.ReferenceFolder, StringComparison.OrdinalIgnoreCase);
            Glyph = isRef ? NovelStore.ReferenceGlyph(path) : ""; // 참고 자료는 종류별 아이콘, 회차는 책 모양
            Title = Path.GetFileNameWithoutExtension(path);
            ShowMore = true;
        }
        if (overview != null) overview.Visible = false;
        ContentChanged?.Invoke();
    }

    private static bool IsRefFile(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), NovelStore.ReferenceFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>문서(MainForm) 하나를 만든다. 칸 안에 끼워 넣을 모양(TopLevel 없음 · 테두리 없음 · 꽉 채움)으로.</summary>
    private MainForm CreateDoc(string path)
    {
        var m = new MainForm(path, embedded: true) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        m.BackToLibraryRequested += () => HomeRequested?.Invoke();
        m.SavedStateChanged += s => { if (doc == m) { savedDot = s; UpdateDot(); } };
        m.EpisodeMetaChanged += () => m.BeginInvoke(() => owner.Tree.Reload());
        m.TitleRenameRequested += (p, t) => m.BeginInvoke(() => owner.Tree.RenameEpisodeTo(p, t));
        // 몰입 모드(F11): 문서 툴바와 함께 워크스페이스의 아이콘 막대 · 파일 트리 · 칸 머리도 숨긴다.
        m.ToolbarVisibilityHost = v => owner.SetChromeVisible(v);
        if (IsRefFile(path)) m.AddReferenceRequested += () => owner.Tree.AddReferences(); // 참고 자료 서식창의 [파일 추가]
        if (IsRefFile(path)) m.PipRequested += p => owner.OpenPip(p); // 참고 자료 서식창의 [PIP 모드]
        m.FormClosed += (_, _) => OnDocClosed(m);
        AppShared.RegisterReader(m);
        return m;
    }

    /// <summary>문서 스스로 닫혔을 때(문서 안의 닫기 등): 탭이면 그 탭만, 아니면 칸을 작품 화면으로.</summary>
    private void OnDocClosed(MainForm m)
    {
        int ti = refTabs.FindIndex(t => t.Doc == m);
        if (ti >= 0) { CloseRefTab(ti, closeForm: false); return; }
        if (doc == m) { doc = null; DocPath = null; Body.Controls.Remove(m); ShowOverview(); ContentChanged?.Invoke(); }
    }

    /// <summary>이 칸에서 참고 자료를 새 탭으로 연다(이미 열려 있으면 그 탭으로 옮겨 간다). 다른 종류(회차 · 위키)가 열려 있었다면 닫고 참고 자료 탭 묶음으로 바꾼다.</summary>
    private void OpenRef(string path)
    {
        int i = refTabs.FindIndex(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) { ActivateRef(i); ContentChanged?.Invoke(); return; }
        if (refTabs.Count == 0) CloseContent();
        var m = CreateDoc(path);
        refTabs.Add((path, m));
        Body.Controls.Add(m);
        m.Show();
        tabStrip.Add(path, Path.GetFileNameWithoutExtension(path));
        tabStrip.SendToBack();
        ActivateRef(refTabs.Count - 1);
        if (overview != null) overview.Visible = false;
        ContentChanged?.Invoke();
    }

    /// <summary>i번째 참고 자료 탭을 보이고 나머지는 숨긴다(숨긴 탭은 열린 채 그대로 — 탭마다 독립).</summary>
    private void ActivateRef(int i)
    {
        for (int k = 0; k < refTabs.Count; k++) if (refTabs[k].Doc is { } d) d.Visible = k == i;
        doc = refTabs[i].Doc;
        pipNote.Visible = doc == null; // PIP로 나간 탭이면 빈 네모 + 안내 글
        if (doc == null) { if (palette != null) { pipNote.BackColor = palette.Main; pipNote.ForeColor = palette.Dim; } pipNote.BringToFront(); }
        DocPath = refTabs[i].Path;
        tabStrip.SelectTab(DocPath);
        tabStrip.Visible = true;
        tabStrip.SendToBack();
        Glyph = NovelStore.ReferenceGlyph(DocPath);
        savedDot = null; UpdateDot();
        Title = Path.GetFileNameWithoutExtension(DocPath);
        ShowMore = true;
    }

    private void CloseRefTab(int i, bool closeForm)
    {
        var (path, m) = refTabs[i];
        bool wasActive = string.Equals(DocPath, path, StringComparison.OrdinalIgnoreCase);
        if (wasActive) { doc = null; DocPath = null; } // 닫는 동안 FormClosed가 칸 전체를 닫지 않게 먼저 비운다
        refTabs.RemoveAt(i);
        object? next = tabStrip.Remove(path);
        if (m != null)
        {
            Body.Controls.Remove(m);
            if (closeForm) { try { m.Close(); } catch { } }
            m.Dispose();
        }
        if (wasActive) pipNote.Visible = false;
        if (refTabs.Count == 0) { tabStrip.Visible = false; ShowOverview(); }
        else if (wasActive) ActivateRef(next is string np ? Math.Max(0, refTabs.FindIndex(t => t.Path.Equals(np, StringComparison.OrdinalIgnoreCase))) : refTabs.Count - 1);
        ContentChanged?.Invoke();
    }

    /// <summary>이 칸의 참고 자료 탭을 PIP로 내보낸다: 문서는 저장하고 닫되 탭 · 칸은 그대로 두고 가운데에 안내 글만 보인다. 이 칸에 그 탭이 있었으면 true.</summary>
    public bool BeginPip(string path)
    {
        int i = refTabs.FindIndex(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || refTabs[i].Doc is not { } m) return false;
        bool wasActive = doc == m;
        if (wasActive) { doc = null; } // 닫는 동안 FormClosed가 칸 전체를 닫지 않게 먼저 비운다
        refTabs[i] = (refTabs[i].Path, null);
        Body.Controls.Remove(m);
        try { m.Close(); } catch { }
        m.Dispose();
        if (wasActive) ActivateRef(i);
        ContentChanged?.Invoke();
        return true;
    }

    /// <summary>PIP 창이 닫히면 그 탭에 문서를 다시 연다.</summary>
    public void EndPip(string path)
    {
        int i = refTabs.FindIndex(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || refTabs[i].Doc != null || !File.Exists(path)) return;
        var m = CreateDoc(path);
        refTabs[i] = (path, m);
        Body.Controls.Add(m);
        m.Show();
        if (string.Equals(DocPath, path, StringComparison.OrdinalIgnoreCase)) ActivateRef(i);
        else m.Visible = false;
        ContentChanged?.Invoke();
    }

    /// <summary>이 경로의 문서가 이 칸에 열려 있는가(참고 자료는 숨은 탭 포함).</summary>
    public bool HasOpen(string path) =>
        string.Equals(DocPath, path, StringComparison.OrdinalIgnoreCase) || refTabs.Any(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>이 경로(폴더면 그 안 전부)의 참고 자료 탭을 닫는다. 닫은 게 있으면 true.</summary>
    public bool CloseRefsUnder(string path)
    {
        bool any = false;
        for (int i = refTabs.Count - 1; i >= 0; i--)
        {
            string p = refTabs[i].Path;
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) { CloseRefTab(i, closeForm: true); any = true; }
        }
        return any;
    }

    private static WikiDoc? TryLoadWiki(string path) { try { return WikiStore.Load(path); } catch { return null; } }

    /// <summary>열린 문서를 저장하고 닫는다(칸은 그대로).</summary>
    public void CloseContent()
    {
        if (refTabs.Count > 0)
        {
            var open = refTabs.ToList();
            refTabs.Clear();
            doc = null;
            foreach (var key in tabStrip.Keys.ToList()) tabStrip.Remove(key);
            tabStrip.Visible = false;
            pipNote.Visible = false;
            foreach (var (_, m0) in open) { if (m0 == null) continue; m0.Close(); Body.Controls.Remove(m0); m0.Dispose(); }
        }
        if (doc != null)
        {
            var m = doc;
            doc = null;
            m.Close();
            Body.Controls.Remove(m);
            m.Dispose();
        }
        if (wiki != null)
        {
            wiki.Save();
            Body.Controls.Remove(wiki);
            wiki.Dispose();
            wiki = null;
        }
        if (canvas != null)
        {
            canvas.Save();
            Body.Controls.Remove(canvas);
            canvas.Dispose();
            canvas = null;
        }
        DocPath = null;
    }

    /// <summary>비어 있을 때: 작품 화면(제목 · 소개 · [+ 새 편 쓰기] · 에피소드 / 위키 목록).</summary>
    public void ShowOverview()
    {
        CloseContent();
        if (overview == null)
        {
            overview = new NovelOverview(owner.Tree) { Dock = DockStyle.Fill };
            overview.SettingsRequested += () => owner.RaiseSettings();
            overview.OpenRequested += p => owner.Open(p, split: false);
            overview.WikiOpenRequested += p => owner.Open(p, split: false);
            overview.RefOpenRequested += p => owner.Open(p, split: false);
            overview.DeleteRequested += dir =>
            {
                using (var confirm = new NovelDeleteDialog(Path.GetFileName(dir))) // 소설 이름을 그대로 적어야만 지워진다
                    if (confirm.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    owner.CloseDocsUnder(dir);
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) { MessageBox.Show(FindForm(), $"삭제하지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                HomeRequested?.Invoke();
            };
            overview.RenameRequested += (dir, name) =>
            {
                try
                {
                    owner.CloseDocsUnder(dir); // 폴더 이름이 바뀌니 열린 문서는 먼저 닫는다
                    string renamed = NovelStore.RenameNovel(dir, name);
                    owner.LoadNovel(renamed);
                    owner.RefreshTitle(); // 창 제목에 새 이름
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), $"이름을 바꾸지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    owner.LoadNovel(dir);
                }
            };
            if (palette != null) overview.ApplyTheme(palette);
            Body.Controls.Add(overview);
        }
        overview.Visible = true;
        overview.Sync();
        overview.BringToFront();
        savedDot = null; UpdateDot();
        Glyph = "";
        Title = Path.GetFileName(owner.NovelPath ?? "") is { Length: > 0 } n ? n : "작품";
        ShowMore = false;
    }

    private void ShowPaneMenu(Control anchor, Point at)
    {
        FocusRequested?.Invoke(this);
        if (doc != null) { doc.ShowMainMenu(anchor, at); return; } // 회차: 문서 메뉴(파일 · 편집 · 보기 · 서식 …)
        if (canvas != null) { canvas.ShowMenuAt(anchor, at); return; } // 캔버스: 내보내기 · 가져오기 · 설정
        wiki?.ShowMenuAt(anchor, at);
    }

[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new bool HeaderVisible { set => base.HeaderVisible = value; }
}

/// <summary>
/// 소설 메인 화면 맨 위 머리 줄(사이드 막대 대신): 왼쪽에 [로고] Caffeum · 부제(서재 사이드바와 같은 모양), 오른쪽에 [도움말] [항상 위] [전체 화면] [설정].
/// 문서 안에 들어가면 이 줄은 사라지고 왼쪽 아이콘 막대(WorkspaceRail)가 나온다.
/// </summary>
internal sealed class MainHeaderBar : Control
{
    private bool pinned;
    private bool fullOn;
    /// <summary>전체 화면 중인가(켜지면 아이콘이 "나가기" 모양으로 바뀐다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FullOn { get => fullOn; set { if (fullOn == value) return; fullOn = value; Invalidate(); } }
    private Bitmap? pinBmp;
    private Color pinColor;
    private bool pinState;
    /// <summary>항상 위가 켜졌는가(켜지면 압정 아이콘이 기울어진 모양으로 바뀐다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned { get => pinned; set { if (pinned == value) return; pinned = value; Invalidate(); } }
    private static readonly (string Key, string Glyph, string Tip)[] Buttons =
    {
        ("full", "", "전체 화면"),
    };

    private static Bitmap? logo;
    private LibraryPalette? palette;
    private int hover = -1; // -2 = 로고
    private readonly ToolTip tip = new();
    public event Action<string>? Clicked;

    public MainHeaderBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        if (logo == null)
            try { using var s = typeof(MainHeaderBar).Assembly.GetManifestResourceStream("Caffeum.ico"); if (s != null) { using var ic = new Icon(s, 256, 256); logo = ic.ToBitmap(); } } catch { }
    }

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Main; Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(76); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    private Rectangle LogoRect => new(S(20), (Height - S(56)) / 2, S(56), S(56)); // 서재 사이드바 로고와 같은 크기

    /// <summary>로고와 이름 · 부제를 합친 자리 — 어디를 눌러도 서재로 돌아간다.</summary>
    private Rectangle BrandRect => new(LogoRect.X, LogoRect.Y, LogoRect.Width + S(10) + S(260), LogoRect.Height);

    private Rectangle ButtonRect(int i)
    {
        // 서재 오른쪽 위의 전체 화면 버튼과 같은 자리(서재 ↔ 소설 메인을 오가도 그 자리에 그대로 있게)
        int size = S(40), gap = S(8);
        int x = Width - S(24) - (Buttons.Length - i) * size - (Buttons.Length - 1 - i) * gap;
        return new Rectangle(x, S(14), size, size);
    }

    private int IndexAt(Point pt)
    {
        if (BrandRect.Contains(pt)) return -2;
        for (int i = 0; i < Buttons.Length; i++) if (ButtonRect(i).Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i == hover) return;
        hover = i;
        Cursor = i != -1 ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, i >= 0 ? Buttons[i].Tip : i == -2 ? "서재로 돌아가기" : null);
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = IndexAt(e.Location);
        if (i == -2) Clicked?.Invoke("logo");
        else if (i >= 0) Clicked?.Invoke(Buttons[i].Key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(palette?.Main ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // 서재 사이드바의 로고 줄과 같은 크기 · 같은 이름: [로고] Caffeum(필기체) / 부제
        var lr = LogoRect;
        if (logo != null) g.DrawImage(logo, lr);
        using var tf = SidebarBrand.TitleFont(15f);
        using var sf = new Font(NovelStore.UiFontFamily, 8.5f);
        int tx = lr.Right + S(10);
        int th = tf.Height, sh = sf.Height;
        int ty = lr.Y + (lr.Height - th - sh + S(4)) / 2;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, AppShared.AppDisplayName, tf, new Rectangle(tx, ty, S(320), th), p.Fore, flags);
        TextRenderer.DrawText(g, AppShared.AppSubtitle, sf, new Rectangle(tx + S(2), ty + th - S(4), S(320), sh), p.Dim, flags);

        using var icon = new Font(MainForm.IconFontName(), 13f);
        for (int i = 0; i < Buttons.Length; i++)
        {
            var r = ButtonRect(i);
            if (hover == i)
            {
                using var hp = LibraryPalette.Rounded(r, S(8));
                using var hb = new SolidBrush(p.CardHover);
                g.FillPath(hb, hp);
            }
            if (Buttons[i].Key == "pin")
            {
                // 압정은 켜졌든 꺼졌든 같은 크기의 그림으로 그려서 모양이 바뀌어도 크기가 같게 한다
                int ps = S(22);
                Color pc = hover == i ? p.Fore : p.Dim;
                if (pinBmp == null || pinColor != pc || pinBmp.Width != ps || pinState != Pinned) { pinBmp?.Dispose(); pinBmp = PinIcon.Make(pc, ps, Pinned); pinColor = pc; pinState = Pinned; }
                g.DrawImage(pinBmp, new Rectangle(r.X + (r.Width - ps) / 2, r.Y + (r.Height - ps) / 2, ps, ps));
                continue;
            }
            TextRenderer.DrawText(g, Buttons[i].Key == "full" && FullOn ? "" : Buttons[i].Glyph, icon, r, hover == i ? p.Fore : p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>파일 트리에서 문서를 끌 때 놓일 자리를 보여 주는 반투명 상자(윈도우 창 스냅 미리보기처럼). 눌리지도 포커스를 뺏지도 않는다.</summary>
internal sealed class SnapPreview : Form
{
    public SnapPreview()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0.3;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT(마우스는 밑으로)
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StickyChrome.RoundCorners(this); }
}