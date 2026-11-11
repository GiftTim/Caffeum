using System.Drawing.Drawing2D;
using Microsoft.VisualBasic.FileIO;

namespace CoffeeViewer;

/// <summary>
/// 워크스페이스의 "파일 트리" 칸(Muvel의 파일 트리처럼).
/// 맨 위 작품 카드(표지 · 제목 · N편 · ▾ 메뉴), 그 밑 한 목록 안에 [에피소드 N +] 회차들(001 · 002 …)과
/// [위키 문서 N +] 카테고리 나무(등장인물 · 사건 … 펼치고 접기, 개수)와 문서들.
/// 누르면 지금 고른 칸에 열고, Ctrl+누르면 옆 칸에 나눠 연다. 회차는 끌어서 순서를 바꾼다.
/// ShowPage(2)이면 같은 자리에 볼트 전체 파일 트리(VaultSidebar)가 보인다.
/// </summary>
internal sealed class EpisodePanel : Panel
{
    private readonly NovelCard card;
    private readonly NovelTreeView tree;
    private readonly Control filesView;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 2000 };
    private LibraryPalette? palette;
    private int page; // 0 = 작품 나무, 2 = 볼트 파일 트리

    public string? NovelPath { get; private set; }
    public event Action? BackRequested;
    /// <summary>파일 트리 맨 위 작품 카드를 눌렀다: 지금 고른 칸을 소설 메인 화면으로.</summary>
    public event Action? OverviewRequested;
    /// <summary>예전 접기 버튼 자리(지금은 쓰지 않는다 — 워크스페이스 왼쪽 아이콘 막대가 맡는다).</summary>
    public event Action? CollapseRequested;
    public event Action<string>? OpenRequested;
    /// <summary>Ctrl+누름: 옆 칸에 나눠 열어 달라.</summary>
    public event Action<string>? OpenSplitRequested;
    /// <summary>오른쪽 클릭 메뉴의 [PIP 모드로 열기]: 항상 위에 뜨는 작은 창으로 연다.</summary>
    public event Action<string>? PipRequested;
    public event Action<string>? PathAboutToChange;
    public event Action<string>? WikiOpenRequested;
    public event Action<string>? WikiOpenSplitRequested;
    /// <summary>에피소드 그룹 줄의 [캔버스]를 눌렀다(그룹 이름).</summary>
    public event Action<string>? CanvasRequested;
    /// <summary>회차 · 위키 줄을 문서 칸 쪽으로 끌고 있다(화면 좌표).</summary>
    public event Action<Point>? SnapDragging;
    /// <summary>끌던 줄을 놓았다(경로, 화면 좌표 — Esc로 그만뒀으면 null).</summary>
    public event Action<string, Point?>? SnapDropped;
    public event Action<string>? WikiAboutToChange;
    public event Action<string, string>? EpisodeRenamed;
    public event Action? Reloaded;
    public event Action? WikiReloaded;
    /// <summary>참고 자료 목록이 다시 읽혔다.</summary>
    public event Action? RefsReloaded;
    public List<string> RefPaths { get; private set; } = new();

    public List<WikiDoc> WikiDocs { get; private set; } = new();
    private List<string> episodes = new();
    public IReadOnlyList<string> EpisodePaths => episodes;
    public string StatsText { get; private set; } = "";

    public EpisodePanel(Control filesView)
    {
        this.filesView = filesView;
        DoubleBuffered = true;
        card = new NovelCard { Dock = DockStyle.Top };
        card.Clicked += () => OverviewRequested?.Invoke();
        tree = new NovelTreeView { Dock = DockStyle.Fill };
        tree.EpisodeChosen += (p, split) => (split ? OpenSplitRequested : OpenRequested)?.Invoke(p);
        tree.WikiChosen += (p, split) => (split ? WikiOpenSplitRequested : WikiOpenRequested)?.Invoke(p);
        tree.AddEpisodeRequested += ShowAddDropDown;
        tree.SortRefsRequested += ShowRefSortMenu;
        tree.AddWikiRequested += CreateWiki;
        tree.EpisodeMenuRequested += ShowEpisodeMenu;
        tree.GroupMenuRequested += ShowGroupMenu;
        tree.WikiMenuRequested += ShowWikiMenu;
        tree.RefChosen += (p, split) => (split ? OpenSplitRequested : OpenRequested)?.Invoke(p);
        tree.AddRefRequested += AddReferences;
        tree.RefMenuRequested += ShowRefMenu;
        tree.FilesDropped += files => AddReferenceFiles(files);
        tree.GroupChanged += (p, grp) => NovelStore.SetEpisodeGroup(p, grp);
        tree.CanvasRequested += g => CanvasRequested?.Invoke(g);
        tree.EpisodeOrderChanged += order => { if (NovelPath != null) { NovelStore.SaveOrder(NovelPath, order); Reload(); } };
        tree.SnapDragging += pt => SnapDragging?.Invoke(pt);
        tree.SnapDropped += (p, pt) => SnapDropped?.Invoke(SnapPathOf(p), pt);
        filesView.Dock = DockStyle.Fill;
        filesView.Visible = false;
        Controls.Add(tree);
        Controls.Add(filesView);
        Controls.Add(card);
        refreshTimer.Tick += (_, _) => { if (Visible && NovelPath != null) RefreshCounts(); };
        refreshTimer.Start();
        Disposed += (_, _) => refreshTimer.Dispose();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Side;
        card.SetPalette(p);
        tree.SetPalette(p);
    }

    public void ApplyFont(Font f) => tree.ApplyFont(f);

    public void LoadNovel(string novelDir)
    {
        NovelPath = novelDir;
        card.SetNovel(Path.GetFileName(novelDir));
        card.SetCover(NovelStore.LoadCover(novelDir)); // 파일 트리 맨 위 카드에도 표지 그림
        ShowPage(0);
        Reload();
        ReloadWiki();
        ReloadRefs();
    }

    public void ShowFiles(bool files) => ShowPage(files ? 2 : 0);

    /// <summary>0(또는 1) = 작품 나무(에피소드 · 위키), 2 = 볼트 전체 파일 트리.</summary>
    public void ShowPage(int which)
    {
        page = which == 2 ? 2 : 0;
        filesView.Visible = page == 2;
        tree.Visible = page != 2;
        (page == 2 ? filesView : tree).BringToFront();
    }
    public bool ShowingFiles => page == 2;

    // ───────── 다시 읽기 ─────────

    public void Reload()
    {
        if (NovelPath == null) return;
        episodes = NovelStore.Episodes(NovelPath);
        NovelStore.SyncPrologueGroup(NovelPath);
        LoadGroups();
        tree.SetGroups(GroupNames, GroupMap);
        tree.SetTypes(episodeTypes);
        tree.SetEpisodes(episodes);
        RefreshCounts(force: true);
    }

    public void ReloadWiki()
    {
        if (NovelPath == null) return;
        WikiDocs = WikiStore.List(NovelPath);
        tree.SetWiki(WikiDocs);
        card.SetStats(episodes.Count, WikiDocs.Count);
        WikiReloaded?.Invoke();
    }

    // ───────── 참고 자료 ─────────

    // ───────── 참고 자료 정렬(파일 트리) ─────────
    private int refSortCol; // 0 파일명 · 1 형식 · 2 크기 · 3 수정일 · 4 독서상태 · 5 즐겨찾기
    private bool refSortDesc;
    private static readonly string[] RefSortNames = { "파일명", "형식", "크기", "수정일", "독서상태", "즐겨찾기" };

    private List<string> SortRefPaths(List<string> src)
    {
        var meta = NovelPath != null ? NovelStore.LoadMeta(NovelPath) : new NovelStore.Meta();
        string State(string f) => meta.RefStatus != null && meta.RefStatus.TryGetValue(Path.GetFileName(f), out var st) && Array.IndexOf(RefList.StateNames, st) >= 0 ? st : RefList.StateNames[0];
        bool Fav(string f) => meta.RefFavorites?.Contains(Path.GetFileName(f)) == true;
        long Size(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }
        DateTime Edited(string f) { try { return new FileInfo(f).LastWriteTime; } catch { return DateTime.MinValue; } }
        IEnumerable<string> q = refSortCol switch
        {
            1 => src.OrderBy(RefList.Kind).ThenBy(Path.GetFileName, NaturalStringComparer.Instance),
            2 => src.OrderBy(Size),
            3 => src.OrderBy(Edited),
            4 => src.OrderBy(f => Array.IndexOf(RefList.StateNames, State(f))),
            5 => src.OrderBy(f => Fav(f) ? 0 : 1),
            _ => src.OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance),
        };
        var list = q.ToList();
        if (refSortDesc) list.Reverse();
        return list;
    }

    /// <summary>참고 자료 정렬 목록: 파일명 · 형식 · 크기 · 수정일 · 독서상태 · 즐겨찾기. 같은 항목을 다시 고르면 오름 · 내림이 바뀐다.</summary>
    private void ShowRefSortMenu(Point screen)
    {
        menu?.Dispose();
        var m = menu = new ContextMenuStrip();
        for (int i = 0; i < RefSortNames.Length; i++)
        {
            int col = i;
            string arrow = col == refSortCol ? (refSortDesc ? "  ▼" : "  ▲") : "";
            var it = new ToolStripMenuItem(RefSortNames[i] + arrow, null, (_, _) =>
            {
                if (refSortCol == col) refSortDesc = !refSortDesc; else { refSortCol = col; refSortDesc = false; }
                ReloadRefs();
            }) { Checked = col == refSortCol };
            m.Items.Add(it);
        }
        m.Show(screen);
    }

    public void ReloadRefs()
    {
        if (NovelPath == null) return;
        RefPaths = SortRefPaths(NovelStore.References(NovelPath));
        tree.SetRefs(RefPaths);
        RefsReloaded?.Invoke();
    }

    /// <summary>[참고 자료 +]: 참고할 소설 · 만화 · 이미지 파일을 골라 작품의 "참고 자료" 폴더로 복사해 넣는다.</summary>
    public void AddReferences()
    {
        if (NovelPath == null) return;
        using var dlg = new OpenFileDialog
        {
            Title = "참고 자료 넣기",
            Multiselect = true,
            Filter = "참고 자료 (소설 · 만화 · PDF · 이미지)|" + string.Join(";", NovelStore.ReferenceExtensions.Select(e => "*" + e)) +
                     "|소설 (*.txt;*.epub;*.hwp;*.hwpx)|*.txt;*.epub;*.hwp;*.hwpx|만화 (*.zip;*.cbz)|*.zip;*.cbz|PDF (*.pdf)|*.pdf|이미지|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AddReferenceFiles(dlg.FileNames);
    }

    /// <summary>파일들을 참고 자료로 넣는다(파일 트리에 끌어다 놓아도 여기로 온다).</summary>
    public void AddReferenceFiles(IEnumerable<string> files)
    {
        if (NovelPath == null) return;
        var list = files.ToList();
        var skipped = list.Where(f => !File.Exists(f) || !NovelStore.ReferenceExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        try { NovelStore.AddReferences(NovelPath, list); }
        catch (Exception ex) { Warn($"참고 자료를 넣지 못했어요: {ex.Message}"); }
        ReloadRefs();
        if (skipped.Count > 0)
            Warn("참고 자료로 넣을 수 없는 파일은 빼고 넣었어요(소설 · 만화 zip · PDF · 한글 문서 · 이미지만 돼요):\n" + string.Join("\n", skipped.Select(Path.GetFileName)));
    }

    public void ShowRefMenu(string path, Point screen)
    {
        menu?.Dispose();
        var m = menu = new ContextMenuStrip();
        m.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke(path));
        m.Items.Add("PIP 모드로 열기", null, (_, _) => PipRequested?.Invoke(path));
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(() =>
        {
            if (MessageBox.Show(FindForm(), $"'{Path.GetFileName(path)}' 참고 자료를 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            PathAboutToChange?.Invoke(path);
            try { FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch (Exception ex) { Warn($"삭제하지 못했어요: {ex.Message}"); }
            ReloadRefs();
        }));
        m.Show(screen);
    }

    private void RefreshCounts(bool force = false)
    {
        if (NovelPath == null) return;
        long total = tree.RefreshCounts();
        string s = $"{episodes.Count}편 · {total:N0}자";
        card.SetStats(episodes.Count, WikiDocs.Count);
        if (force || StatsText != s) { StatsText = s; Reloaded?.Invoke(); }
    }

    /// <summary>지금 보고 있는 문서(회차든 위키든)를 나무에서 짚어 둔다.</summary>
    public void MarkCurrent(string? path) => tree.Current = path;
    public void MarkWiki(string? path) { if (path != null) tree.Current = path; }

    // ───────── 만들기 · 메뉴 ─────────

    /// <summary>에피소드 그룹 이름들(보이는 순서)과 회차 경로 → 그룹 이름 표. Reload마다 다시 읽는다.</summary>
    public List<string> GroupNames { get; private set; } = new();
    public Dictionary<string, string> GroupMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> episodeTypes = new(StringComparer.OrdinalIgnoreCase);

    private void LoadGroups()
    {
        GroupNames = new();
        GroupMap = new(StringComparer.OrdinalIgnoreCase);
        episodeTypes = new(StringComparer.OrdinalIgnoreCase);
        if (NovelPath == null) return;
        var meta = NovelStore.LoadMeta(NovelPath);
        if (meta.Types != null) foreach (var (file, t) in meta.Types) episodeTypes[Path.Combine(NovelPath, file)] = t;
        GroupNames = meta.Groups?.ToList() ?? new();
        if (meta.GroupOf != null)
            foreach (var (file, g) in meta.GroupOf) if (GroupNames.Contains(g)) GroupMap[Path.Combine(NovelPath, file)] = g;
    }

    /// <summary>[새 편 쓰기 > 에피소드 그룹]: 새 그룹을 만든다(이름은 나중에 바꾼다).</summary>
    public void CreateGroup()
    {
        if (NovelPath == null) return;
        NovelStore.CreateGroup(NovelPath);
        Reload();
        Reloaded?.Invoke();
    }

    /// <summary>끌어 놓은 줄의 키를 열 문서 경로로: 에피소드 그룹 줄("grp:이름")이면 그 그룹의 플롯 캔버스 파일.</summary>
    private string SnapPathOf(string key) =>
        key.StartsWith("grp:", StringComparison.Ordinal) && NovelPath is string d ? CanvasStore.PathFor(d, key.Substring(4)) : key;

    /// <summary>이 에피소드 그룹의 캔버스를 열어 달라(소설 메인 목록의 그룹 줄에서).</summary>
    public void RequestCanvas(string group) => CanvasRequested?.Invoke(group);

    public void ShowGroupMenu(string group, Point screen)
    {
        if (NovelPath is not string dir) return;
        menu?.Dispose();
        var m = menu = new ContextMenuStrip();
        m.Items.Add("이 그룹에 새 에피소드", null, (_, _) => BeginInvoke(() => AddEpisode(group)));
        m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(() =>
        {
            using var dlg = new NovelInputDialog("그룹 이름 바꾸기", "그룹 이름", group);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            string oldCanvas = CanvasStore.PathFor(dir, group);
            PathAboutToChange?.Invoke(oldCanvas); // 그 그룹의 캔버스가 열려 있으면 닫았다가
            string renamed = NovelStore.RenameGroup(dir, group, dlg.FirstValue);
            CanvasStore.Rename(dir, group, renamed); // 캔버스 파일도 새 이름으로
            EpisodeRenamed?.Invoke(oldCanvas, CanvasStore.PathFor(dir, renamed)); // 새 이름으로 다시 연다
            Reload();
            Reloaded?.Invoke();
        }));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("그룹 지우기 (에피소드는 그룹 밖으로 나와요)", null, (_, _) => BeginInvoke(() =>
        {
            if (MessageBox.Show(FindForm(), $"'{group}' 그룹을 지울까요?\n그룹 안의 에피소드는 지워지지 않고 그룹 밖으로 나와요.", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            NovelStore.DeleteGroup(dir, group);
            Reload();
            Reloaded?.Invoke();
        }));
        m.Show(screen);
    }

    private ToolStripDropDown? addDropDown;

    /// <summary>파일 트리 [에피소드] 줄의 + : 에피소드 / 새 에피소드 그룹 중 하나를 고르는 목록(소설 메인의 새 편 쓰기와 같은 모양).</summary>
    private void ShowAddDropDown()
    {
        if (NovelPath == null) return;
        addDropDown?.Dispose();
        var font = new Font(NovelStore.UiFontFamily, 9.5f);
        var gray = Color.FromArgb(150, 148, 146);
        var panel = new IconMenuPanel(font, DeviceDpi, BackColor, ForeColor);
        panel.Add("에피소드", PurpleIcons.Book(S(40), gray), () => BeginInvoke(() => AddEpisode()));
        panel.Add("새 에피소드 그룹", PurpleIcons.Lines(S(40), gray), () => BeginInvoke(() => CreateGroup()));
        panel.Size = panel.MeasureAll();
        var dd = addDropDown = new ToolStripDropDown { Padding = Padding.Empty, BackColor = BackColor, AutoSize = true };
        dd.Items.Add(new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size });
        panel.Chosen += () => dd.Close();
        var at = Cursor.Position;
        dd.Show(new Point(at.X - S(12), at.Y + S(14))); // 누른 + 바로 밑
    }

    public void AddEpisode(string? group = null)
    {
        if (NovelPath == null) return;
        try
        {
            string p = NovelStore.AddEpisode(NovelPath);
            if (group != null) NovelStore.SetEpisodeGroup(p, group);
            Reload();
            OpenRequested?.Invoke(p);
        }
        catch (Exception ex) { Warn($"회차를 만들지 못했어요: {ex.Message}"); }
    }

    /// <summary>[+ 새 위키 문서] · Alt+W: 만들고 바로 연다.</summary>
    public void CreateWiki()
    {
        if (NovelPath == null) return;
        if (WikiStore.AskCreate(FindForm(), NovelPath) is string p) { ReloadWiki(); WikiOpenRequested?.Invoke(p); }
    }

    private ContextMenuStrip? menu;


    public void ShowEpisodeMenu(string path, Point screen)
    {
        menu?.Dispose();
        var m = menu = new ContextMenuStrip();
        int i = episodes.IndexOf(path);
        m.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke(path));
        m.Items.Add("PIP 모드로 열기", null, (_, _) => PipRequested?.Invoke(path));
        m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(() => Rename(path)));
        // (그룹 옮기기 · 위로/아래로 올리기는 순서 관리 모드에서 끌어서 한다)
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""));
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(() => Delete(path)));
        m.Show(screen);
    }

    private void Move(string path, int step)
    {
        int i = episodes.IndexOf(path), j = i + step;
        if (NovelPath == null || i < 0 || j < 0 || j >= episodes.Count) return;
        var order = episodes.ToList();
        (order[i], order[j]) = (order[j], order[i]);
        NovelStore.SaveOrder(NovelPath, order);
        Reload();
    }

    public void ShowWikiMenu(string path, Point screen)
    {
        menu?.Dispose();
        var m = menu = new ContextMenuStrip();
        m.Items.Add("열기", null, (_, _) => WikiOpenRequested?.Invoke(path));
        m.Items.Add("PIP 모드로 열기", null, (_, _) => PipRequested?.Invoke(path));
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(() =>
        {
            if (MessageBox.Show(FindForm(), $"'{Path.GetFileNameWithoutExtension(path)}' 위키 문서를 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            WikiAboutToChange?.Invoke(path);
            try { WikiStore.Delete(path); } catch (Exception ex) { Warn($"삭제하지 못했어요: {ex.Message}"); }
            ReloadWiki();
        }));
        m.Show(screen);
    }

    public void RenameEpisodeTo(string path, string newTitle) => DoRename(path, newTitle);

    private void Rename(string path)
    {
        using var dlg = new NovelInputDialog("회차 이름 바꾸기", "회차 제목", Path.GetFileNameWithoutExtension(path));
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        DoRename(path, dlg.FirstValue);
    }

    private void DoRename(string path, string newTitle)
    {
        try
        {
            PathAboutToChange?.Invoke(path);
            string np = NovelStore.RenameEpisode(path, newTitle);
            Reload();
            EpisodeRenamed?.Invoke(path, np);
        }
        catch (Exception ex) { Warn($"이름을 바꾸지 못했어요: {ex.Message}"); Reload(); }
    }

    private void Delete(string path)
    {
        if (MessageBox.Show(FindForm(), $"'{Path.GetFileNameWithoutExtension(path)}' 회차를 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            PathAboutToChange?.Invoke(path);
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { Warn($"삭제하지 못했어요: {ex.Message}"); }
        if (NovelPath != null) NovelStore.SaveOrder(NovelPath, NovelStore.Episodes(NovelPath));
        Reload();
    }

    private void Warn(string msg) => MessageBox.Show(FindForm(), msg, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>파일 트리 맨 위의 작품 카드: [표지] 제목 / N편 · 위키 M · txt / ▾(메뉴).</summary>
    private sealed class NovelCard : Control
    {
        private LibraryPalette? palette;
        private string title = "";
        private int eps, wikis;
        private bool hoverMenu;
        /// <summary>작품 카드를 눌렀다(소설 메인 화면으로).</summary>
        public event Action? Clicked;

        public NovelCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font(NovelStore.UiFontFamily, 9f);
        }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
        public void SetNovel(string t) { title = t; Invalidate(); }
        private Bitmap? coverImage;
        /// <summary>작품 표지 그림(없으면 null → 색 네모). 넘긴 그림은 이 카드가 맡아 지운다.</summary>
        public void SetCover(Bitmap? img) { coverImage?.Dispose(); coverImage = img; Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) coverImage?.Dispose(); base.Dispose(disposing); }
        public void SetStats(int e, int w) { if (e == eps && w == wikis) return; eps = e; wikis = w; Invalidate(); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(92); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); bool h = ClientRectangle.Contains(e.Location); if (h != hoverMenu) { hoverMenu = h; Invalidate(); } Cursor = Cursors.Hand; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverMenu = false; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Clicked?.Invoke(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            var g = e.Graphics;
            g.Clear(p?.Side ?? BackColor);
            if (p == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new RectangleF(S(10) + 0.5f, S(10) + 0.5f, Width - S(20) - 1, Height - S(18) - 1);
            using (var path = LibraryPalette.Rounded(box, S(10)))
            {
                using (var b = new SolidBrush(hoverMenu ? Themes.Blend(p.Side, p.Fore, 0.04f) : Themes.Blend(p.Side, p.Main, 0.7f))) g.FillPath(b, path);
                using (var pen = new Pen(p.Line)) g.DrawPath(pen, path);
            }
            float coverH = box.Height - S(20);
            var cover = new RectangleF(box.X + S(10), box.Y + S(10), coverImage != null ? coverH * 400f / 600f : S(34), coverH); // 그림이 있으면 표지 비율(400:600)
            using (var cp = LibraryPalette.Rounded(cover, S(4)))
            {
                using (var cb = new SolidBrush(Themes.Blend(p.Side, p.Accent, 0.35f))) g.FillPath(cb, cp);
                if (coverImage != null)
                {
                    var old = g.Clip;
                    g.SetClip(cp, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(coverImage, Rectangle.Round(cover));
                    g.Clip = old;
                    using var cpen = new Pen(p.Line);
                    g.DrawPath(cpen, cp);
                }
            }
            int tx = (int)cover.Right + S(12), tw = Math.Max(0, (int)box.Right - S(12) - tx);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            using (var tf = new Font(Font.FontFamily, 11.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, tf, new Rectangle(tx, (int)box.Y + S(14), tw, tf.Height), p.Fore, flags);
            TextRenderer.DrawText(g, $"{eps}편 · 위키 {wikis} · txt", Font, new Rectangle(tx, (int)box.Y + S(40), tw, Font.Height), p.Dim, flags);
        }
    }
}

/// <summary>
/// 파일 트리의 한 목록: [에피소드 N  +] 001 제목 ……, [위키 문서 N  +] ▸ 등장인물 N → 문서들.
/// 머리 줄 · 카테고리 줄을 누르면 접고 펼친다. 회차는 끌어서 순서를 바꾼다.
/// </summary>
internal sealed class NovelTreeView : Control
{
    private enum Kind { Section, Episode, Group, Category, Wiki, Ref }
    private sealed record Row(Kind Kind, string Key, string Text, int Index = 0, WikiCategory? Cat = null, int Count = 0, string? Sub = null);

    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, Visible = false };
    private readonly List<Row> rows = new();
    private List<string> groupNames = new();
    private readonly Dictionary<string, string> groupMap = new(StringComparer.OrdinalIgnoreCase); // 회차 경로 → 그룹 이름
    private readonly Dictionary<string, string> typeMap = new(StringComparer.OrdinalIgnoreCase); // 회차 경로 → 유형(없으면 일반)
    private List<string> episodes = new();
    private List<WikiDoc> wiki = new();
    private List<string> refs = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> collapsed = new();
    private LibraryPalette? palette;
    private string? current;
    private int hover = -1, hoverPlus = -1, hoverSort = -1;
    private int dragIndex = -1; // 끌고 있는 줄(순서 바꾸기면 회차, 아니면 문서 칸으로 가져가는 회차 · 위키)
    // 순서 바꾸기: 놓을 자리. Line = 표시선을 그을 줄 번호, Anchor = 이 회차 앞(After면 뒤)에 놓는다(null이면 Group의 맨 앞),
    // Group = 놓은 뒤 소속 그룹(null이면 그룹 밖). 소설 메인 [순서 관리]처럼 다른 그룹 · 그룹 밖으로도 옮겨진다.
    private sealed record TreeDrop(int Line, string? Anchor, bool After, string? Group);
    private TreeDrop? drop;
    private int dropIndex => drop?.Line ?? -1;
    private bool reorder; // 에피소드 줄의 [순서 바꾸기]가 켜져 있다(다시 눌러야 끝, 그동안 끌어서 열기는 쉰다)
    private Point downAt;
    private bool dragging;

    public event Action<string, bool>? EpisodeChosen;
    public event Action<string, bool>? WikiChosen;
    public event Action? AddEpisodeRequested;
    /// <summary>참고 자료 줄의 정렬 버튼을 눌렀다(인수: 목록을 띄울 화면 좌표).</summary>
    public event Action<Point>? SortRefsRequested;
    public event Action? AddWikiRequested;
    public event Action<string, Point>? EpisodeMenuRequested;
    /// <summary>그룹 줄을 오른쪽 눌렀다(그룹 이름, 화면 좌표).</summary>
    public event Action<string, Point>? GroupMenuRequested;
    public event Action<string, Point>? WikiMenuRequested;
    public event Action<Point>? SnapDragging;
    public event Action<string, Point?>? SnapDropped;
    public event Action<List<string>>? EpisodeOrderChanged;
    /// <summary>순서 바꾸기로 다른 그룹(null이면 그룹 밖)에 놓았다. EpisodeOrderChanged보다 먼저 온다.</summary>
    public event Action<string, string?>? GroupChanged;
    /// <summary>그룹 줄 오른쪽 [캔버스] 아이콘을 눌렀다(그룹 이름).</summary>
    public event Action<string>? CanvasRequested;
    public event Action<string, bool>? RefChosen;
    public event Action? AddRefRequested;
    public event Action<string, Point>? RefMenuRequested;
    /// <summary>탐색기에서 파일을 끌어다 놓았다(참고 자료로 넣으라고).</summary>
    public event Action<string[]>? FilesDropped;

    public NovelTreeView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        Controls.Add(scroll);
        scroll.ValueChanged += (_, _) => Invalidate();
        if (Environment.GetEnvironmentVariable("CAFFEUM_TEST_COLLAPSE") is { Length: > 0 } tc) foreach (var k in tc.Split(',')) collapsed.Add(k); // 시험용: 접어 둘 묶음(sec:wiki 등)
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files); };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Current { get => current; set { if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase)) return; current = value; Invalidate(); } }

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Side; Invalidate(); }
    public void ApplyFont(Font f) { Font = new Font(NovelStore.UiFontFamily, Math.Max(8f, f.SizeInPoints)); Rebuild(); }
    public void SetRefs(List<string> list) { refs = list.ToList(); Rebuild(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int RowH(Row r) => r.Kind == Kind.Section ? Font.Height + S(22) : Font.Height + S(12);
    private int Offset => scroll.Visible ? scroll.Value : 0;

    public void SetEpisodes(List<string> list) { episodes = list.ToList(); Rebuild(); }

    /// <summary>에피소드 그룹(이름 순서와 회차 → 그룹 표)을 받아 둔다. SetEpisodes 앞에 부른다.</summary>
    /// <summary>회차 유형 표를 받아 둔다(번호 대신 유형 이름을 보일지 정하려고). SetEpisodes 앞에 부른다.</summary>
    public void SetTypes(Dictionary<string, string> map) { typeMap.Clear(); foreach (var (k, v) in map) typeMap[k] = v; }

    public void SetGroups(List<string> names, Dictionary<string, string> map)
    {
        groupNames = names.ToList();
        groupMap.Clear();
        foreach (var (k, v) in map) groupMap[k] = v;
    }
    public void SetWiki(List<WikiDoc> docs) { wiki = docs; Rebuild(); }

    public long RefreshCounts()
    {
        long total = 0;
        bool changed = false;
        foreach (var p in episodes)
        {
            int n = NovelStore.CountChars(p);
            total += n;
            if (!counts.TryGetValue(p, out int old) || old != n) { counts[p] = n; changed = true; }
        }
        if (changed) Invalidate();
        return total;
    }

    private void Rebuild()
    {
        rows.Clear();
        rows.Add(new Row(Kind.Section, "sec:ep", "에피소드", Count: episodes.Count + groupNames.Count)); // 그룹도 하나로 센다
        if (!collapsed.Contains("sec:ep"))
        {
            // 그룹 밖 회차 → (그룹 줄 · 그 그룹의 회차들) …. 번호는 일반 에피소드만 이어서 센다(프롤로그 · 에필로그 · 특별편 · 단편은 유형 이름).
            int n = -1; // 번호는 000부터
            string Label(string p) => (++n).ToString("000"); // 유형과 상관없이 모든 회차에 번호(프롤로그도 001 · 002 …)
            foreach (var p in episodes.Where(p => !groupMap.ContainsKey(p))) rows.Add(new Row(Kind.Episode, p, Path.GetFileNameWithoutExtension(p), 0, Sub: Label(p)));
            foreach (var gname in groupNames)
            {
                rows.Add(new Row(Kind.Group, "grp:" + gname, gname));
                if (collapsed.Contains("grp:" + gname)) { foreach (var p in episodes.Where(p => groupMap.TryGetValue(p, out var gn) && gn == gname)) _ = Label(p); continue; }
                foreach (var p in episodes.Where(p => groupMap.TryGetValue(p, out var gn) && gn == gname)) rows.Add(new Row(Kind.Episode, p, Path.GetFileNameWithoutExtension(p), 1, Sub: Label(p)));
            }
        }
        rows.Add(new Row(Kind.Section, "sec:wiki", "위키 문서", Count: wiki.Count));
        if (!collapsed.Contains("sec:wiki"))
            foreach (var cat in WikiCategory.All)
            {
                var inCat = wiki.Where(d => d.Category == cat.Key).ToList();
                rows.Add(new Row(Kind.Category, "cat:" + cat.Key, cat.Label, Cat: cat, Count: inCat.Count));
                if (!collapsed.Contains("cat:" + cat.Key))
                    foreach (var d in inCat) rows.Add(new Row(Kind.Wiki, d.Path, d.Title, Cat: cat));
            }
        rows.Add(new Row(Kind.Section, "sec:ref", "참고 자료", Count: refs.Count));
        if (!collapsed.Contains("sec:ref"))
            foreach (var f in refs) rows.Add(new Row(Kind.Ref, f, Path.GetFileName(f)));
        SyncScroll();
        Invalidate();
    }

    private int ContentHeight() => rows.Sum(RowH) + S(16);

    private void SyncScroll()
    {
        int content = ContentHeight();
        bool need = content > ClientSize.Height && ClientSize.Height > 0;
        scroll.Visible = need;
        if (need) { scroll.Maximum = content; scroll.LargeChange = Math.Max(1, ClientSize.Height); scroll.SmallChange = S(28); }
        else if (scroll.Value != 0) scroll.Value = 0;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    private int IndexAt(int y)
    {
        int top = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++) { int h = RowH(rows[i]); if (y >= top && y < top + h) return i; top += h; }
        return -1;
    }

    private int RowTop(int index) { int top = S(4) - Offset; for (int i = 0; i < index; i++) top += RowH(rows[i]); return top; }
    private int Width0 => ClientSize.Width - (scroll.Visible ? scroll.Width : 0);
    private Rectangle PlusRect(int i) { int top = RowTop(i), h = RowH(rows[i]); return new Rectangle(Width0 - S(36), top + (h - S(24)) / 2 + S(3), S(24), S(24)); }
    /// <summary>에피소드 그룹 줄 오른쪽 끝의 [캔버스] 버튼.</summary>
    private Rectangle CanvasRect(int i) { int top = RowTop(i), h = RowH(rows[i]); return new Rectangle(Width0 - S(36), top + (h - S(24)) / 2, S(24), S(24)); }
    private Bitmap? canvasIcon;
    private Color canvasIconColor;
    /// <summary>참고 자료 줄의 정렬 버튼: + 바로 왼쪽.</summary>
    private Rectangle SortRect(int i) { var p = PlusRect(i); return new Rectangle(p.X - S(28), p.Y, p.Width, p.Height); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!scroll.Visible) return;
        scroll.Value = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * S(84), 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragIndex >= 0 && e.Button == MouseButtons.Left)
        {
            var ds = SystemInformation.DragSize;
            if (!dragging && (Math.Abs(e.X - downAt.X) > ds.Width || Math.Abs(e.Y - downAt.Y) > ds.Height)) { dragging = true; Invalidate(); }
            if (dragging && reorder)
            {
                var t = DropAt(e.Y);
                if (t != drop) { drop = t; Invalidate(); }
                Cursor = Cursors.SizeNS;
                return;
            }
            if (dragging)
            {
                SnapDragging?.Invoke(PointToScreen(e.Location));
                Cursor = Cursors.SizeAll;
                return;
            }
        }
        int h = IndexAt(e.Y);
        int hp = h >= 0 && ((rows[h].Kind == Kind.Section && PlusRect(h).Contains(e.Location)) || (rows[h].Kind == Kind.Group && CanvasRect(h).Contains(e.Location))) ? h : -1;
        int hs = h >= 0 && rows[h].Kind == Kind.Section && rows[h].Key is "sec:ref" or "sec:ep" && SortRect(h).Contains(e.Location) ? h : -1;
        if (h != hover || hp != hoverPlus || hs != hoverSort) { hover = h; hoverPlus = hp; hoverSort = hs; Invalidate(); }
        Cursor = h < 0 ? Cursors.Default : reorder && rows[h].Kind == Kind.Episode ? Cursors.SizeAll : Cursors.Hand;
    }

    /// <summary>순서 바꾸기에서 y에 놓으면 갈 자리: 에피소드 묶음 안의 회차 줄(위 반 = 앞, 아래 반 = 뒤) 또는 그룹 줄(그 그룹 맨 앞).</summary>
    private TreeDrop? DropAt(int y)
    {
        int end = rows.FindIndex(r => r.Key == "sec:wiki");
        var cands = Enumerable.Range(0, end < 0 ? rows.Count : end).Where(k => rows[k].Kind is Kind.Episode or Kind.Group).ToList();
        if (cands.Count == 0) return null;
        int k = cands.FirstOrDefault(c => y < RowTop(c) + RowH(rows[c]), -1);
        bool past = k < 0;
        if (past) k = cands[^1];
        var r = rows[k];
        string? GroupOf(string p) => groupMap.TryGetValue(p, out var g) ? g : null;
        if (r.Kind == Kind.Group) return new TreeDrop(k + 1, null, false, r.Text);
        bool after = past || y > RowTop(k) + RowH(r) / 2;
        return new TreeDrop(after ? k + 1 : k, r.Key, after, GroupOf(r.Key));
    }

    /// <summary>순서 바꾸기를 끝낸다(다른 문서 · 화면으로 옮겨 가거나 다른 일을 시작하면).</summary>
    public void EndReorder()
    {
        if (!reorder) return;
        reorder = false;
        dragIndex = -1; drop = null; dragging = false;
        Invalidate();
    }

    protected override void OnLeave(EventArgs e) { base.OnLeave(e); EndReorder(); } // 글 상자 · 다른 칸으로 포커스가 가면
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) EndReorder(); } // 파일 트리가 닫히거나 서재 · 소설 메인으로

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = hoverPlus = hoverSort = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = IndexAt(e.Y);
        // [순서 바꾸기]가 켜져 있으면 회차를 끌어 순서를 바꾸고, 꺼져 있으면 문서 칸으로 가져가 놓은 쪽(왼쪽 · 오른쪽)에 연다(창 스냅처럼).
        bool canDrag = i >= 0 && (reorder ? rows[i].Kind == Kind.Episode : rows[i].Kind is Kind.Episode or Kind.Wiki or Kind.Group); // 그룹 줄을 끌어 놓으면 그 그룹의 캔버스가 열린다
        dragIndex = e.Button == MouseButtons.Left && canDrag ? i : -1;
        downAt = e.Location;
        dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape || !dragging || dragIndex < 0) return;
        if (!reorder) SnapDropped?.Invoke(rows[dragIndex].Key, null); // 끌기 그만두기
        dragIndex = -1; drop = null; // dragging은 그대로 둬서, 손을 뗄 때 클릭(열기)으로 치지 않게
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Y);
        try
        {
            if (dragging && reorder)
            {
                if (dragIndex < 0 || dragIndex >= rows.Count || drop is not { } tgt) return;
                // 끌던 회차를 놓은 자리로 옮긴다 — 순서와 소속 그룹이 함께 바뀐다(다른 그룹 · 그룹 밖으로도)
                string item = rows[dragIndex].Key;
                if (string.Equals(tgt.Anchor, item, StringComparison.OrdinalIgnoreCase)) return; // 제자리
                var order = episodes.ToList();
                order.Remove(item);
                int insertAt;
                if (tgt.Anchor == null)
                {
                    var members = order.Where(p => groupMap.TryGetValue(p, out var g2) && g2 == tgt.Group).ToList();
                    insertAt = members.Count > 0 ? order.IndexOf(members[0]) : order.Count; // 그 그룹의 맨 앞(비어 있으면 어디든)
                }
                else
                {
                    int ai = order.IndexOf(tgt.Anchor);
                    insertAt = ai < 0 ? order.Count : tgt.After ? ai + 1 : ai;
                }
                order.Insert(Math.Clamp(insertAt, 0, order.Count), item);
                string? curGroup = groupMap.TryGetValue(item, out var cg) ? cg : null;
                if (curGroup != tgt.Group) GroupChanged?.Invoke(item, tgt.Group);
                if (curGroup != tgt.Group || !order.SequenceEqual(episodes)) EpisodeOrderChanged?.Invoke(order);
                return;
            }
            if (dragging)
            {
                if (dragIndex >= 0 && dragIndex < rows.Count) SnapDropped?.Invoke(rows[dragIndex].Key, PointToScreen(e.Location));
                return;
            }
            if (i < 0) return;
            var r = rows[i];
            bool split = (ModifierKeys & Keys.Control) != 0;
            if (e.Button == MouseButtons.Right)
            {
                if (r.Kind == Kind.Episode) EpisodeMenuRequested?.Invoke(r.Key, PointToScreen(e.Location));
                else if (r.Kind == Kind.Group) GroupMenuRequested?.Invoke(r.Text, PointToScreen(e.Location));
                else if (r.Kind == Kind.Wiki) WikiMenuRequested?.Invoke(r.Key, PointToScreen(e.Location));
                else if (r.Kind == Kind.Ref) RefMenuRequested?.Invoke(r.Key, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            // 순서 바꾸기 중에 회차 · 그룹 · 에피소드 줄(접기) 말고 다른 것(위키 · 참고 자료 · 새 문서 …)을 누르면 끝낸다
            bool epSection = r.Kind is Kind.Episode or Kind.Group || (r.Key == "sec:ep" && !PlusRect(i).Contains(e.Location));
            if (!epSection) EndReorder();
            switch (r.Kind)
            {
                case Kind.Section when r.Key == "sec:ref" && SortRect(i).Contains(e.Location):
                    SortRefsRequested?.Invoke(PointToScreen(new Point(SortRect(i).Left, SortRect(i).Bottom)));
                    break;
                case Kind.Section when r.Key == "sec:ep" && SortRect(i).Contains(e.Location):
                    reorder = !reorder; // 순서 바꾸기 켜기 · 끄기(다시 눌러야 끝)
                    break;
                case Kind.Section when PlusRect(i).Contains(e.Location):
                    if (r.Key == "sec:ep") AddEpisodeRequested?.Invoke();
                    else if (r.Key == "sec:wiki") AddWikiRequested?.Invoke();
                    else AddRefRequested?.Invoke();
                    break;
                case Kind.Group when CanvasRect(i).Contains(e.Location):
                    CanvasRequested?.Invoke(r.Text); // 이 그룹의 플롯 캔버스
                    break;
                case Kind.Section:
                case Kind.Category:
                case Kind.Group:
                    if (!collapsed.Remove(r.Key)) collapsed.Add(r.Key);
                    Rebuild();
                    break;
                case Kind.Episode: if (!reorder) EpisodeChosen?.Invoke(r.Key, split); break; // 순서 바꾸기 중에는 눌러도 열지 않는다
                case Kind.Wiki: WikiChosen?.Invoke(r.Key, split); break;
                case Kind.Ref: RefChosen?.Invoke(r.Key, split); break;
            }
        }
        finally
        {
            dragIndex = -1; drop = null;
            dragging = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(p?.Side ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Width0, pad = S(10);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using var bold = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var icon = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.9f);
        using var chev = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.65f);
        int y = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int h = RowH(r);
            if (y + h >= 0 && y <= ClientSize.Height)
            {
                var rr = new Rectangle(pad, y + S(1), w - pad * 2, h - S(2));
                bool cur = (r.Kind is Kind.Episode or Kind.Wiki or Kind.Ref) && string.Equals(r.Key, current, StringComparison.OrdinalIgnoreCase);
                if (r.Kind == Kind.Section)
                {
                    var tr = new Rectangle(rr.X + S(8), y + S(8), rr.Width, h - S(8));
                    int lw = TextRenderer.MeasureText(r.Text, bold, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, r.Text, bold, new Rectangle(tr.X, tr.Y, lw + 2, tr.Height), p.Fore, flags);
                    TextRenderer.DrawText(g, r.Count.ToString(), small, new Rectangle(tr.X + lw + S(8), tr.Y, S(40), tr.Height), p.Dim, flags);
                    var pr = PlusRect(i);
                    if (hoverPlus == i) using (var hb = new SolidBrush(p.CardHover)) using (var hp = LibraryPalette.Rounded(pr, S(6))) g.FillPath(hb, hp);
                    using (var pf = new Font(Font.FontFamily, Font.SizeInPoints * 1.35f))
                        TextRenderer.DrawText(g, "+", pf, pr, p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (r.Key == "sec:ref")
                    {
                        // 정렬 버튼(슬라이더 모양): 가로줄 둘에 작은 동그라미
                        var sr = SortRect(i);
                        if (hoverSort == i) using (var hb2 = new SolidBrush(p.CardHover)) using (var hp2 = LibraryPalette.Rounded(sr, S(6))) g.FillPath(hb2, hp2);
                        var sm = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var sp = new Pen(p.Dim, Math.Max(1.3f, DeviceDpi / 96f * 1.3f));
                        float x0 = sr.X + S(7), x1 = sr.Right - S(7), y1 = sr.Y + sr.Height * 0.39f, y2 = sr.Y + sr.Height * 0.63f, rad = S(2);
                        float k1 = x0 + (x1 - x0) * 0.68f, k2 = x0 + (x1 - x0) * 0.32f;
                        g.DrawLine(sp, x0, y1, k1 - rad, y1); g.DrawLine(sp, k1 + rad, y1, x1, y1); g.DrawEllipse(sp, k1 - rad, y1 - rad, rad * 2, rad * 2);
                        g.DrawLine(sp, x0, y2, k2 - rad, y2); g.DrawLine(sp, k2 + rad, y2, x1, y2); g.DrawEllipse(sp, k2 - rad, y2 - rad, rad * 2, rad * 2);
                        g.SmoothingMode = sm;
                    }
                    else if (r.Key == "sec:ep")
                    {
                        // 순서 바꾸기 버튼: 가는 점선 네모 안에 ↖ 화살표(모서리 ㄱ자 + 대각선). 켜져 있으면 강조색 바탕
                        var sr = SortRect(i);
                        if (reorder || hoverSort == i) using (var hb2 = new SolidBrush(reorder ? p.AccentSoft : p.CardHover)) using (var hp2 = LibraryPalette.Rounded(sr, S(6))) g.FillPath(hb2, hp2);
                        var smOld = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                        Color ink = reorder ? p.Accent : p.Fore;
                        float u = sr.Width / 24f * 0.78f, cx0 = sr.X + sr.Width / 2f, cy0 = sr.Y + sr.Height / 2f;
                        float px = Math.Max(1f, DeviceDpi / 96f);
                        // 점선 네모(가늘게)
                        using (var dp = new Pen(Color.FromArgb(170, ink), px) { DashPattern = new[] { 3f, 2.2f } })
                            g.DrawRectangle(dp, cx0 - 9f * u, cy0 - 9f * u, 18f * u, 18f * u);
                        // 화살표: 왼쪽 위 모서리(ㄱ자)에서 오른쪽 아래로 뻗는 대각선
                        float ax = cx0 - 2.6f * u, ay = cy0 - 2.2f * u;
                        using (var ap = new Pen(ink, Math.Max(1.4f, px * 1.5f)) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                        {
                            g.DrawLines(ap, new[] { new PointF(ax + 5.8f * u, ay), new PointF(ax, ay), new PointF(ax, ay + 5.8f * u) });
                            g.DrawLine(ap, ax, ay, ax + 6.4f * u, ay + 6.4f * u);
                        }
                        g.SmoothingMode = smOld;
                    }
                }
                else
                {
                    if (cur || i == hover || (dragging && i == dragIndex))
                    {
                        using var path = LibraryPalette.Rounded(rr, S(6));
                        using var b = new SolidBrush(cur ? p.AccentSoft : p.CardHover);
                        g.FillPath(b, path);
                    }
                    switch (r.Kind)
                    {
                        case Kind.Episode:
                        {
                            string num = r.Sub ?? "";
                            int lead = 0;
                            if (reorder)
                            {
                                // 순서 바꾸기 중: 소설 메인 [순서 관리]처럼 줄 앞에 끌어서 옮기는 손잡이(점 6개)
                                using var db = new SolidBrush(p.Dim);
                                int gx = rr.X + S(8) + r.Index * S(18), gy = rr.Y + rr.Height / 2 - S(6), d = S(3);
                                for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 3; cy++) g.FillEllipse(db, gx + cx * S(6), gy + cy * S(6), d, d);
                                lead = S(18);
                            }
                            int nx = rr.X + S(8) + r.Index * S(18) + lead, nw = Math.Max(TextRenderer.MeasureText("000", small, Size.Empty, TextFormatFlags.NoPadding).Width, TextRenderer.MeasureText(num, small, Size.Empty, TextFormatFlags.NoPadding).Width);
                            TextRenderer.DrawText(g, num, small, new Rectangle(nx, rr.Y, nw, rr.Height), cur ? p.Accent : p.Dim, flags);
                            string cnt = counts.TryGetValue(r.Key, out int n) ? $"{n:N0}" : "";
                            int cw = TextRenderer.MeasureText(cnt, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                            TextRenderer.DrawText(g, cnt, small, new Rectangle(rr.Right - cw - S(8), rr.Y, cw + 2, rr.Height), Themes.Blend(p.Side, p.Dim, 0.7f), flags);
                            int tx = nx + nw + S(10);
                            TextRenderer.DrawText(g, r.Text, cur ? bold : Font, new Rectangle(tx, rr.Y, Math.Max(0, rr.Right - cw - S(16) - tx), rr.Height), p.Fore, flags);
                            break;
                        }
                        case Kind.Group:
                        {
                            bool open = !collapsed.Contains(r.Key);
                            TextRenderer.DrawText(g, open ? "" : "", chev, new Rectangle(rr.X + S(6), rr.Y, S(14), rr.Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, r.Text, Font, new Rectangle(rr.X + S(26), rr.Y, rr.Width - S(34) - S(28), rr.Height), p.Fore, flags);
                            // 오른쪽 끝 [캔버스] 버튼: 이 그룹의 플롯 캔버스를 연다
                            var cr = CanvasRect(i);
                            if (hoverPlus == i) using (var hb = new SolidBrush(p.CardHover)) using (var hpath = LibraryPalette.Rounded(cr, S(6))) g.FillPath(hb, hpath);
                            if (canvasIcon == null || canvasIconColor != p.Dim || canvasIcon.Width != S(16)) { canvasIcon?.Dispose(); canvasIcon = PurpleIcons.Board(S(16), p.Dim); canvasIconColor = p.Dim; }
                            g.DrawImage(canvasIcon, cr.X + (cr.Width - canvasIcon.Width) / 2, cr.Y + (cr.Height - canvasIcon.Height) / 2);
                            break;
                        }
                        case Kind.Category:
                        {
                            bool open = !collapsed.Contains(r.Key);
                            TextRenderer.DrawText(g, open ? "" : "", chev, new Rectangle(rr.X + S(6), rr.Y, S(14), rr.Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, r.Cat!.Glyph, icon, new Rectangle(rr.X + S(24), rr.Y, S(18), rr.Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, r.Text, Font, new Rectangle(rr.X + S(48), rr.Y, rr.Width - S(90), rr.Height), p.Fore, flags);
                            string c = r.Count.ToString();
                            int cw = TextRenderer.MeasureText(c, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                            TextRenderer.DrawText(g, c, small, new Rectangle(rr.Right - cw - S(8), rr.Y, cw + 2, rr.Height), p.Dim, flags);
                            break;
                        }
                        case Kind.Ref:
                            TextRenderer.DrawText(g, NovelStore.ReferenceGlyph(r.Key), icon, new Rectangle(rr.X + S(8), rr.Y, S(18), rr.Height), cur ? p.Accent : p.Dim, flags | TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, r.Text, cur ? bold : Font, new Rectangle(rr.X + S(32), rr.Y, rr.Width - S(40), rr.Height), p.Fore, flags);
                            break;
                        case Kind.Wiki:
                            TextRenderer.DrawText(g, r.Cat!.Glyph, icon, new Rectangle(rr.X + S(42), rr.Y, S(18), rr.Height), cur ? p.Accent : Themes.Blend(p.Side, p.Accent, 0.7f), flags | TextFormatFlags.HorizontalCenter);
                            TextRenderer.DrawText(g, r.Text, cur ? bold : Font, new Rectangle(rr.X + S(66), rr.Y, rr.Width - S(74), rr.Height), p.Fore, flags);
                            break;
                    }
                }
            }
            y += h;
        }
        if (dragging && reorder && dropIndex >= 0)
        {
            int dy = RowTop(dropIndex);
            using var pen = new Pen(p.Accent, S(2));
            g.DrawLine(pen, pad, dy, w - pad, dy);
        }
    }
}

/// <summary>
/// 소설 메인의 에피소드 목록. 맨 위 머리줄(파일명 · 크기 · 집필상태 · 수정일)과, 줄마다 [번호 또는 유형] 제목 [뱃지] ……… 크기 · 집필상태 · 고친 때.
/// 그룹이 있으면 그룹 밖 회차를 먼저 보이고, 그 밑에 [구분선 · 책갈피 그룹 이름]과 그 그룹의 회차들이 차례로 놓인다.
/// 번호는 일반 에피소드만 001부터 이어서 세고, 프롤로그 · 에필로그 · 특별편 · 단편은 번호 자리에 유형 이름이 적힌다.
/// 끌어서 순서를 바꾸는 건 순서 관리 모드에서만(보이는 회차 사이에서).
/// </summary>
internal sealed class EpisodeList : Control
{
    private enum RowKind { Episode, Group }
    private sealed record Row(RowKind Kind, string Key, string? Number = null); // Key = 회차 경로 또는 그룹 이름

    private readonly List<string> paths = new();
    private readonly List<Row> rows = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, Visible = false };
    private LibraryPalette? palette;
    private string? current;
    private int hoverIndex = -1;
    private int dragRow = -1;                   // 끌고 있는 줄(rows 번호)
    /// <summary>놓을 자리: 기준 줄 · 그 뒤쪽인지 · 들어갈 그룹(null이면 그룹 밖) · 그룹 줄 위에 놓는 건지(그 그룹의 맨 앞).</summary>
    private (int Row, bool After, string? Group, bool Header)? dropTarget;
    private Point downAt;
    private bool dragging;

    private List<string> groupNames = new();
    private readonly Dictionary<string, string> groupMap = new(StringComparer.OrdinalIgnoreCase); // 회차 경로 → 그룹 이름
    private readonly Dictionary<string, string> statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Created, DateTime Edited)> times = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Rectangle Rect, string Path)> badges = new();

    public List<string> Paths => paths;
    public event Action<string>? EpisodeChosen;
    public event Action? OrderChanged;
    public event Action<string, Point>? MenuRequested;
    /// <summary>끌어서 놓은 회차의 소속 그룹이 바뀌었다(회차 경로, 새 그룹 이름 · null이면 그룹 밖). OrderChanged보다 먼저 온다.</summary>
    public event Action<string, string?>? GroupChanged;
    /// <summary>그룹 줄을 오른쪽 눌렀다(그룹 이름, 화면 좌표).</summary>
    public event Action<string, Point>? GroupMenuRequested;
    /// <summary>머리줄(파일명 · 크기 · 수정일)을 눌렀다: 1 = 이름, 2 = 크기, 3 = 수정일.</summary>
    public event Action<int>? SortHeaderClicked;
    /// <summary>집필 상태 뱃지를 눌렀다.</summary>
    public event Action<string>? StatusClicked;

    public EpisodeList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Controls.Add(scroll);
        scroll.ValueChanged += (_, _) => Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Current { get => current; set { if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase)) return; current = value; Invalidate(); } }

    /// <summary>예전 호환용(이 목록은 이제 소설 메인에서만 쓴다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OnMain { get; set; }

    /// <summary>순서 관리 모드: 켜져 있을 때만 끌어서 순서를 바꾼다. 줄 왼쪽에 손잡이가 보인다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReorderMode { get => reorderMode; set { reorderMode = value; dragRow = -1; dropTarget = null; dragging = false; Invalidate(); } }
    private bool reorderMode;
    private bool CanDrag => reorderMode && SortMode == 0;

    /// <summary>검색 중이 아닐 때만 빈 그룹도 보인다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowEmptyGroups { get => showEmptyGroups; set { if (showEmptyGroups == value) return; showEmptyGroups = value; Rebuild(); } }
    private bool showEmptyGroups = true;

    /// <summary>보기 정렬: 0 = 직접 정한 순서, 1 = 이름, 2 = 글자 수, 3 = 고친 때. (보기만 바꾸고 저장된 순서는 건드리지 않는다.)</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SortMode { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SortDescending { get; private set; }
    /// <summary>줄 간격을 좁게(간단히 보기).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Compact { get => compact; set { compact = value; SyncScroll(); Invalidate(); } }
    private bool compact;

    /// <summary>목록이 비었을 때 보여 줄 말(검색 결과가 없을 때 바꿔 쓴다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? EmptyText { get; set; }

    /// <summary>정렬 기준을 정한다. 호출한 쪽이 이어서 SetPaths로 원래 순서를 다시 넣어야 한다.</summary>
    public void SetSort(int mode, bool descending) { SortMode = mode; SortDescending = descending; dragRow = -1; dropTarget = null; dragging = false; }

    private void ApplySort()
    {
        if (SortMode == 0) return;
        List<string> sorted = SortMode switch
        {
            1 => paths.OrderBy(p => Path.GetFileNameWithoutExtension(p), NaturalStringComparer.Instance).ToList(),
            2 => paths.OrderBy(p => counts.TryGetValue(p, out int n) ? n : 0).ToList(),
            _ => paths.OrderBy(p => TimesOf(p).Edited).ToList(),
        };
        if (SortDescending) sorted.Reverse();
        paths.Clear();
        paths.AddRange(sorted);
    }

    private Color Surface => palette == null ? BackColor : palette.Main;

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = Surface; Invalidate(); }
    public void ApplyFont(Font f) { Font = new Font(NovelStore.UiFontFamily, f.SizeInPoints); SyncScroll(); Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int EpisodeH => Font.Height + S(compact ? 18 : 54);
    private int GroupH => Font.Height + S(compact ? 40 : 56);
    private int RowHeight(Row r) => r.Kind == RowKind.Group ? GroupH : EpisodeH;
    private int Offset => scroll.Visible ? scroll.Value : 0;
    /// <summary>맨 위 머리줄(파일명 · 크기 · 집필상태 · 수정일).</summary>
    private int Top0 => S(46);
    private int Viewport => Math.Max(1, ClientSize.Height - Top0);
    private int ContentHeight() => rows.Sum(RowHeight) + S(12);

    /// <summary>머리줄과 모든 줄을 스크롤 없이 다 펼치는 데 필요한 높이(쪽 전체가 스크롤될 때 쓴다).</summary>
    public int PreferredHeight => Top0 + ContentHeight() + S(8);

    /// <summary>집필 상태 네 가지(누르면 이 순서로 돈다).</summary>
    public static readonly string[] StatusNames = { "준비", "집필", "퇴고", "완료" };

    /// <summary>저장된 집필 상태(없으면 글자 수로 자동: 비었으면 준비, 아니면 집필). 예전 이름(예정 · 진행)도 받아 준다.</summary>
    public string StatusOf(string path)
    {
        if (statuses.TryGetValue(path, out var s))
            return s switch { "예정" => "준비", "진행" => "집필", _ => s };
        return counts.TryGetValue(path, out int n) && n > 0 ? "집필" : "준비";
    }

    public void SetStatuses(string novelDir, Dictionary<string, string>? map)
    {
        statuses.Clear();
        if (map != null) foreach (var (name, st) in map) statuses[Path.Combine(novelDir, name)] = st;
        Invalidate();
    }

    /// <summary>파일 이름 → 유형 표(.caffeum.json)를 받아 둔다. 없으면 일반.</summary>
    public void SetTypes(string novelDir, Dictionary<string, string>? map)
    {
        types.Clear();
        if (map != null) foreach (var (name, t) in map) types[Path.Combine(novelDir, name)] = t;
        Rebuild();
    }

    private string TypeOf(string path) => types.TryGetValue(path, out var t) ? t : NovelStore.EpisodeTypes[0];

    /// <summary>그룹 이름들(보이는 순서)과 회차 → 그룹 표를 받아 둔다.</summary>
    public void SetGroups(List<string> names, Dictionary<string, string> map)
    {
        groupNames = names.ToList();
        groupMap.Clear();
        foreach (var (k, v) in map) groupMap[k] = v;
        Rebuild();
    }

    /// <summary>만든 때 · 고친 때(그릴 때마다 파일을 건드리지 않게 담아 둔다).</summary>
    private (DateTime Created, DateTime Edited) TimesOf(string path)
    {
        if (!times.TryGetValue(path, out var t)) t = times[path] = ReadTimes(path);
        return t;
    }

    private static (DateTime, DateTime) ReadTimes(string path)
    {
        try { return (File.GetCreationTime(path), File.GetLastWriteTime(path)); } catch { return (DateTime.MinValue, DateTime.MinValue); }
    }

    public void SetPaths(List<string> newPaths)
    {
        paths.Clear();
        paths.AddRange(newPaths);
        foreach (var p in paths) times[p] = ReadTimes(p);
        ApplySort();
        Rebuild();
    }

    /// <summary>접어 둔 그룹 이름(그룹 줄을 누르면 접고 펼친다).</summary>
    private readonly HashSet<string> collapsed = new();

    /// <summary>보이는 줄을 다시 만든다: 그룹 밖 회차 → (그룹 줄 · 그 그룹의 회차들)…, 번호는 일반 에피소드만 이어서 센다.</summary>
    private void Rebuild()
    {
        rows.Clear();
        int n = -1; // 번호는 000부터
        string? NumberFor(string p) => (++n).ToString("000"); // 유형과 상관없이 모든 회차에 번호
        foreach (var p in paths.Where(p => !groupMap.ContainsKey(p))) rows.Add(new Row(RowKind.Episode, p, NumberFor(p)));
        foreach (var g in groupNames)
        {
            var members = paths.Where(p => groupMap.TryGetValue(p, out var gn) && gn == g).ToList();
            if (members.Count == 0 && !showEmptyGroups) continue;
            rows.Add(new Row(RowKind.Group, g));
            foreach (var p in members) { int num = int.Parse(NumberFor(p)!); if (!collapsed.Contains(g)) rows.Add(new Row(RowKind.Episode, p, num.ToString("000"))); } // 접힌 그룹은 안의 회차 줄을 감춘다(번호는 그대로 이어진다)
        }
        SyncScroll();
        Invalidate();
    }

    /// <summary>글자 수를 다시 센다(고친 회차만 실제로 읽는다). 전체 합을 돌려준다.</summary>
    public long RefreshCounts()
    {
        long total = 0;
        bool changed = false;
        foreach (var p in paths)
        {
            int n = NovelStore.CountChars(p);
            total += n;
            if (!counts.TryGetValue(p, out int old) || old != n) { counts[p] = n; changed = true; }
            var t = ReadTimes(p);
            if (!times.TryGetValue(p, out var oldT) || oldT != t) { times[p] = t; changed = true; }
        }
        if (changed) { if (SortMode is 2 or 3) { ApplySort(); Rebuild(); } Invalidate(); }
        return total;
    }

    public void Move(string path, int step)
    {
        int i = paths.IndexOf(path), j = i + step;
        if (i < 0 || j < 0 || j >= paths.Count) return;
        (paths[i], paths[j]) = (paths[j], paths[i]);
        Rebuild();
        OrderChanged?.Invoke();
    }

    private void SyncScroll()
    {
        int content = ContentHeight();
        bool need = content > Viewport && ClientSize.Height > 0;
        scroll.Visible = need;
        if (need)
        {
            scroll.Dock = DockStyle.None;
            scroll.SetBounds(ClientSize.Width - scroll.Width, Top0, scroll.Width, Viewport);
            scroll.Maximum = content;
            scroll.LargeChange = Viewport;
            scroll.SmallChange = EpisodeH;
            if (scroll.Value > content - Viewport) scroll.Value = Math.Max(0, content - Viewport);
        }
        else if (scroll.Value != 0) scroll.Value = 0;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    // ───────── 자리 계산 ─────────

    private int RowTop(int index)
    {
        int y = S(4) + Top0 - Offset;
        for (int i = 0; i < index && i < rows.Count; i++) y += RowHeight(rows[i]);
        return y;
    }

    private int IndexAt(int y)
    {
        if (y < Top0) return -1;
        int top = S(4) + Top0 - Offset;
        for (int i = 0; i < rows.Count; i++) { int h = RowHeight(rows[i]); if (y >= top && y < top + h) return i; top += h; }
        return -1;
    }

    /// <summary>끌다가 놓을 자리를 정한다. 회차 줄 위에서는 그 줄의 앞/뒤(같은 그룹으로), 그룹 줄 위에서는 그 그룹의 맨 앞, 맨 위 · 맨 아래는 처음/끝.</summary>
    private (int Row, bool After, string? Group, bool Header)? DropTargetAt(int y)
    {
        if (rows.Count == 0) return null;
        int ri = IndexAt(y);
        if (ri < 0) ri = y < Top0 + S(8) ? 0 : rows.Count - 1;
        var r = rows[ri];
        if (r.Kind == RowKind.Group) return (ri, true, r.Key, true);
        bool after = y > RowTop(ri) + EpisodeH / 2;
        string? grp = groupMap.TryGetValue(r.Key, out var g) ? g : null;
        return (ri, after, grp, false);
    }

    private IEnumerable<int> EpisodeRowIndices() => Enumerable.Range(0, rows.Count).Where(i => rows[i].Kind == RowKind.Episode);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!scroll.Visible) return;
        int max = scroll.Maximum - scroll.LargeChange + 1;
        scroll.Value = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * EpisodeH * 3, 0, Math.Max(0, max));
    }

    // 머리줄 · 열 자리: [번호/유형 · 제목 · 뱃지 ……… 크기 | 집필상태 | 고친 때]
    private int ColEdited(int w) => w - S(10) - S(8) - S(112);
    private int ColCreated(int w) => ColEdited(w) - S(112);
    private int ColSize(int w) => ColCreated(w) - S(130);
    private int ListWidth => ClientSize.Width - (scroll.Visible ? scroll.Width : 0);

    private Rectangle HeaderCell(int col)
    {
        int w = ListWidth;
        return col switch
        {
            1 => new Rectangle(S(18), S(8), S(110), Top0 - S(8)),
            2 => new Rectangle(ColSize(w), S(8), S(90), Top0 - S(8)),
            _ => new Rectangle(ColEdited(w), S(8), S(100), Top0 - S(8)),
        };
    }

    private int HeaderColAt(Point pt)
    {
        if (pt.Y >= Top0) return 0;
        for (int c = 1; c <= 3; c++) if (HeaderCell(c).Contains(pt)) return c;
        return 0;
    }

    // ───────── 마우스 ─────────

    /// <summary>그룹 줄 오른쪽 끝의 [캔버스] 버튼 자리.</summary>
    private Rectangle GroupCanvasRect(int i)
    {
        int y = RowTop(i), h = RowHeight(rows[i]), pad = S(10);
        var gr = new Rectangle(pad, y + S(10), ListWidth - pad * 2, h - S(14));
        return new Rectangle(gr.Right - S(36), gr.Y + (gr.Height - S(28)) / 2, S(28), S(28));
    }
    private int hoverCanvas = -1;
    private Bitmap? canvasIcon;
    private Color canvasIconColor;
    /// <summary>그룹 줄의 [캔버스]를 눌렀다(그룹 이름).</summary>
    public event Action<string>? CanvasRequested;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hc = IndexAt(e.Y) is int gi && gi >= 0 && rows[gi].Kind == RowKind.Group && GroupCanvasRect(gi).Contains(e.Location) ? gi : -1;
        if (hc != hoverCanvas) { hoverCanvas = hc; Invalidate(); }
        if (dragRow >= 0 && e.Button == MouseButtons.Left && CanDrag)
        {
            if (!dragging && (Math.Abs(e.X - downAt.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - downAt.Y) > SystemInformation.DragSize.Height)) dragging = true;
            if (dragging)
            {
                var t = DropTargetAt(e.Y);
                if (t != dropTarget) { dropTarget = t; Invalidate(); }
                Cursor = Cursors.SizeNS;
                return;
            }
        }
        int h = IndexAt(e.Y);
        if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
        bool overBadge = !reorderMode && badges.Any(b => b.Rect.Contains(e.Location));
        Cursor = overBadge || HeaderColAt(e.Location) > 0 ? Cursors.Hand
            : h >= 0 ? (rows[h].Kind == RowKind.Group ? Cursors.Hand : reorderMode ? Cursors.SizeAll : Cursors.Hand) : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIndex = hoverCanvas = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            int i = IndexAt(e.Y);
            dragRow = i >= 0 && rows[i].Kind == RowKind.Episode ? i : -1;
            downAt = e.Location; dragging = false; dropTarget = null;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int at = IndexAt(e.Y);
        if (e.Button == MouseButtons.Right)
        {
            if (at >= 0 && rows[at].Kind == RowKind.Episode) MenuRequested?.Invoke(rows[at].Key, PointToScreen(e.Location));
            else if (at >= 0) GroupMenuRequested?.Invoke(rows[at].Key, PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (!dragging)
        {
            int col = HeaderColAt(e.Location);
            if (col > 0) { SortHeaderClicked?.Invoke(col); dragRow = -1; return; }
            if (!reorderMode && badges.FirstOrDefault(b => b.Rect.Contains(e.Location)) is { Path: not null } hit) { StatusClicked?.Invoke(hit.Path); dragRow = -1; return; }
        }
        if (!dragging && at >= 0 && rows[at].Kind == RowKind.Group && GroupCanvasRect(at).Contains(e.Location))
        {
            dragRow = -1;
            CanvasRequested?.Invoke(rows[at].Key); // 이 그룹의 플롯 캔버스
            return;
        }
        if (!dragging && at >= 0 && rows[at].Kind == RowKind.Group)
        {
            // 그룹 줄을 누르면 그 그룹을 접거나 펼친다
            string gname = rows[at].Key;
            if (!collapsed.Remove(gname)) collapsed.Add(gname);
            dragRow = -1;
            Rebuild();
            return;
        }
        if (dragging && dragRow >= 0 && dropTarget is { } tgt && CanDrag)
        {
            // 끌던 회차를 놓은 자리로 옮긴다 — 순서와 소속 그룹이 함께 바뀐다(다른 그룹 · 그룹 밖으로도 옮겨진다)
            string item = rows[dragRow].Key;
            var order = paths.ToList();
            order.Remove(item);
            int insertAt;
            if (tgt.Header)
            {
                var members = order.Where(p => groupMap.TryGetValue(p, out var g2) && g2 == tgt.Group).ToList();
                insertAt = members.Count > 0 ? order.IndexOf(members[0]) : order.Count; // 그 그룹의 맨 앞(비어 있으면 어디든)
            }
            else
            {
                int ai = order.IndexOf(rows[tgt.Row].Key);
                insertAt = ai < 0 ? order.Count : tgt.After ? ai + 1 : ai;
            }
            order.Insert(Math.Clamp(insertAt, 0, order.Count), item);
            string? curGroup = groupMap.TryGetValue(item, out var cg) ? cg : null;
            if (curGroup != tgt.Group) GroupChanged?.Invoke(item, tgt.Group);
            if (curGroup != tgt.Group || !order.SequenceEqual(paths))
            {
                paths.Clear();
                paths.AddRange(order);
                OrderChanged?.Invoke();
            }
        }
        else if (at >= 0 && at == dragRow && !reorderMode && rows[at].Kind == RowKind.Episode) EpisodeChosen?.Invoke(rows[at].Key);
        dragRow = -1; dropTarget = null;
        dragging = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Surface);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ListWidth, pad = S(10), top0 = Top0;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var meas = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        using var numFont = new Font(Font.FontFamily, compact ? 13f : 26f);
        using var titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.1f);
        using var titleBold = new Font(titleFont, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var groupFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.0f);
        using var badgeFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.8f);
        using var icon = new Font("Segoe MDL2 Assets", Font.SizeInPoints * 0.8f);
        Color soft = Themes.Blend(p.Main, p.Dim, 0.8f);

        // 첫 칸(번호 · 유형 이름)의 폭: "000"과 지금 쓰이는 유형 이름 가운데 가장 긴 것
        int numW = TextRenderer.MeasureText("000", numFont, Size.Empty, meas).Width;
        foreach (var r in rows) if (r.Kind == RowKind.Episode && r.Number == null) numW = Math.Max(numW, TextRenderer.MeasureText(TypeOf(r.Key), numFont, Size.Empty, meas).Width);
        badges.Clear();

        g.SetClip(new Rectangle(0, top0, ClientSize.Width, Math.Max(0, ClientSize.Height - top0)));
        for (int i = 0; i < rows.Count; i++)
        {
            int y = RowTop(i), h = RowHeight(rows[i]);
            if (y + h < top0) continue;
            if (y > ClientSize.Height) break;

            if (rows[i].Kind == RowKind.Group)
            {
                // 구분선 · [책갈피] 그룹 이름
                if (i > 0) using (var lp = new Pen(p.Line)) g.DrawLine(lp, pad, y + S(4), w - pad, y + S(4));
                var gr = new Rectangle(pad, y + S(10), w - pad * 2, h - S(14));
                if (i == hoverIndex) using (var hp = LibraryPalette.Rounded(gr, S(8))) using (var hb = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.04f))) g.FillPath(hb, hp);
                {   // 접고 펼치는 화살표(접힘: ▸ · 펼침: ▾)
                    using var cp = new Pen(p.Dim, Math.Max(1.3f, DeviceDpi / 96f * 1.3f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                    var prevSmooth = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float cx = gr.X + S(14), cy = gr.Y + gr.Height / 2f, d = S(4);
                    if (collapsed.Contains(rows[i].Key)) { g.DrawLine(cp, cx - d / 2, cy - d, cx + d / 2, cy); g.DrawLine(cp, cx + d / 2, cy, cx - d / 2, cy + d); }
                    else { g.DrawLine(cp, cx - d, cy - d / 2, cx, cy + d / 2); g.DrawLine(cp, cx, cy + d / 2, cx + d, cy - d / 2); }
                    g.SmoothingMode = prevSmooth;
                }
                using var bookmark = new Font("Segoe MDL2 Assets", Font.SizeInPoints * 0.95f);
                TextRenderer.DrawText(g, "", bookmark, new Rectangle(gr.X + S(26), gr.Y, S(22), gr.Height), soft, flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, rows[i].Key, groupFont, new Rectangle(gr.X + S(56), gr.Y, Math.Max(0, gr.Width - S(64) - S(40)), gr.Height), p.Dim, flags | TextFormatFlags.EndEllipsis);
                // 오른쪽 끝 [캔버스] 버튼: 이 그룹의 플롯 캔버스를 연다(파일 트리 그룹 줄과 같은 아이콘)
                var cr = GroupCanvasRect(i);
                if (hoverCanvas == i) using (var chp = LibraryPalette.Rounded(cr, S(6))) using (var chb = new SolidBrush(p.CardHover)) g.FillPath(chb, chp);
                if (canvasIcon == null || canvasIconColor != p.Dim || canvasIcon.Width != S(18)) { canvasIcon?.Dispose(); canvasIcon = PurpleIcons.Board(S(18), p.Dim); canvasIconColor = p.Dim; }
                g.DrawImage(canvasIcon, cr.X + (cr.Width - canvasIcon.Width) / 2, cr.Y + (cr.Height - canvasIcon.Height) / 2);
                continue;
            }

            string path = rows[i].Key;
            var row = new Rectangle(pad, y + S(2), w - pad * 2, h - S(4));
            bool cur = string.Equals(path, current, StringComparison.OrdinalIgnoreCase);
            if (cur || i == hoverIndex || (dragging && i == dragRow))
            {
                using var rp = LibraryPalette.Rounded(row, S(8));
                using var rb = new SolidBrush(cur ? p.AccentSoft : Themes.Blend(p.Main, p.Fore, 0.05f));
                g.FillPath(rb, rp);
            }
            int lead = reorderMode && SortMode == 0 ? S(20) : 0;
            if (lead > 0)
            {
                using var db = new SolidBrush(p.Dim); // 끌어서 옮기는 손잡이(점 6개)
                int gx = row.X + S(10), gy = row.Y + row.Height / 2 - S(6), d = S(3);
                for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 3; cy++) g.FillEllipse(db, gx + cx * S(6), gy + cy * S(6), d, d);
            }
            using (var bar = new SolidBrush(cur ? p.Accent : Themes.Blend(p.Main, p.Dim, 0.7f)))
                g.FillRectangle(bar, row.X + lead + S(2), row.Y + S(6), S(3), row.Height - S(12));
            int nx = row.X + lead + S(16);
            // 번호(일반 에피소드) 또는 유형 이름(프롤로그 · 에필로그 · 특별편 · 단편)
            TextRenderer.DrawText(g, rows[i].Number ?? TypeOf(path), numFont, new Rectangle(nx, row.Y, numW + S(4), row.Height), soft, flags);

            bool wide = ColSize(w) - (nx + numW) >= S(240);
            int rightEdge = (wide ? ColSize(w) : ColEdited(w)) - S(12);
            int tx = nx + numW + S(18);
            string title = Path.GetFileNameWithoutExtension(path), st = StatusOf(path);
            int badgeW = TextRenderer.MeasureText(st, badgeFont, Size.Empty, meas).Width + S(18), badgeH = badgeFont.Height + S(8);
            // 집필 상태: 넓으면 크기 · 수정일 앞 자리에, 좁으면 제목 바로 옆에
            int tw = Math.Min(TextRenderer.MeasureText(title, titleFont, Size.Empty, meas).Width + S(2), Math.Max(S(40), rightEdge - tx - (wide ? 0 : badgeW + S(10))));
            TextRenderer.DrawText(g, title, cur ? titleBold : titleFont, new Rectangle(tx, row.Y, tw, row.Height), p.Fore, flags | TextFormatFlags.EndEllipsis);
            var br = wide
                ? new Rectangle(ColCreated(w), row.Y + (row.Height - badgeH) / 2, badgeW, badgeH)
                : new Rectangle(tx + tw + S(10), row.Y + (row.Height - badgeH) / 2, badgeW, badgeH);
            if (br.Right <= rightEdge + S(4) || wide)
            {
                bool done = st == "완료";
                using (var bp = LibraryPalette.Rounded(br, S(5)))
                {
                    using (var bb = new SolidBrush(done ? p.AccentSoft : Themes.Blend(p.Main, p.Fore, 0.06f))) g.FillPath(bb, bp);
                    using (var bpen = new Pen(done ? Themes.Blend(p.AccentSoft, p.Accent, 0.5f) : p.Line)) g.DrawPath(bpen, bp);
                }
                TextRenderer.DrawText(g, st, badgeFont, br, done ? p.Accent : p.Dim, flags | TextFormatFlags.HorizontalCenter);
                badges.Add((br, path));
            }

            void Col(int x, string glyph, string text)
            {
                TextRenderer.DrawText(g, glyph, icon, new Rectangle(x, row.Y, S(20), row.Height), soft, flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, text, small, new Rectangle(x + S(24), row.Y, S(104), row.Height), p.Dim, flags | TextFormatFlags.EndEllipsis);
            }
            var (_, edited) = TimesOf(path);
            if (wide) Col(ColSize(w), "", counts.TryGetValue(path, out int cnt) && cnt > 0 ? $"{cnt:N0}자" : "계산되지 않음");
            Col(ColEdited(w), "", NovelStore.Ago(edited));
        }
        if (dragging && dropTarget is { } dt)
        {
            // 놓을 자리 표시선: 그룹 줄이면 그 줄 바로 밑, 회차 줄이면 그 줄의 위/아래
            int dy = dt.Header ? RowTop(dt.Row) + RowHeight(rows[dt.Row]) - S(2) : dt.After ? RowTop(dt.Row) + RowHeight(rows[dt.Row]) : RowTop(dt.Row);
            using var pen = new Pen(p.Accent, S(2));
            g.DrawLine(pen, pad, dy, w - pad, dy);
        }
        g.ResetClip();
        if (rows.Count == 0)
            TextRenderer.DrawText(g, EmptyText ?? "아직 에피소드가 없어요.\n[새 편 쓰기]로 시작해 보세요.", Font, new Rectangle(0, top0 + S(24), w, S(80)), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        // 머리줄: 파일명 ^ · 크기 ⇅ · 수정일 ⇅ (눌러서 정렬). 집필상태는 정렬 없이 글자만.
        using (var hb = new SolidBrush(p.Main)) g.FillRectangle(hb, 0, 0, ClientSize.Width, top0);
        using var headFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.92f, FontStyle.Bold);
        float u = DeviceDpi / 96f;
        for (int col = 1; col <= 3; col++)
        {
            var cell = HeaderCell(col);
            string label = col == 1 ? "파일명" : col == 2 ? "크기" : "수정일";
            int state = SortMode == col ? (SortDescending ? 2 : 1) : 0;
            TextRenderer.DrawText(g, label, headFont, new Rectangle(cell.X, cell.Y, cell.Width - S(24), cell.Height - S(4)), p.Fore, flags);
            int lw = TextRenderer.MeasureText(label, headFont, Size.Empty, meas).Width;
            using var ap = new Pen(state == 0 ? Themes.Blend(p.Main, p.Accent, 0.55f) : p.Accent, Math.Max(1.4f, 1.4f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float ax = cell.X + lw + S(14), ay = cell.Y + (cell.Height - S(4)) / 2f;
            if (col == 1)
            {
                float s2 = state == 2 ? -1 : 1;
                g.DrawLine(ap, ax - 4 * u, ay + 2 * u * s2, ax, ay - 2 * u * s2);
                g.DrawLine(ap, ax, ay - 2 * u * s2, ax + 4 * u, ay + 2 * u * s2);
            }
            else
            {
                using var up2 = new Pen(state == 1 ? p.Accent : ap.Color, ap.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var dn2 = new Pen(state == 2 ? p.Accent : ap.Color, ap.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(up2, ax - 3 * u, ay - 5 * u, ax - 3 * u, ay + 5 * u);
                g.DrawLine(up2, ax - 5.5f * u, ay - 2.5f * u, ax - 3 * u, ay - 5 * u);
                g.DrawLine(up2, ax - 0.5f * u, ay - 2.5f * u, ax - 3 * u, ay - 5 * u);
                g.DrawLine(dn2, ax + 3 * u, ay - 5 * u, ax + 3 * u, ay + 5 * u);
                g.DrawLine(dn2, ax + 0.5f * u, ay + 2.5f * u, ax + 3 * u, ay + 5 * u);
                g.DrawLine(dn2, ax + 5.5f * u, ay + 2.5f * u, ax + 3 * u, ay + 5 * u);
            }
        }
        if (ColSize(w) - (S(10) + S(16) + numW) >= S(240))
            TextRenderer.DrawText(g, "집필상태", headFont, new Rectangle(ColCreated(w), S(8), S(90), top0 - S(12)), p.Fore, flags);
        using (var lp = new Pen(p.Line)) g.DrawLine(lp, pad, top0 - S(5), w - pad, top0 - S(5)); // 머리줄 밑줄은 끊지 않고 한 줄로
    }
}

/// <summary>
/// 작품 화면(Muvel의 소설 페이지): 탭이 하나도 없을 때 본문 자리에 뜬다.
/// "N편 · 로컬" / 큰 제목 / 소개 / 태그 / [탐색기에서 열기] [소설 삭제] (소개 글을 누르면 작품 소개 창) /
/// [에피소드 · 위키 · 메모] + [소설 내 검색] / [선택 · 설정] [+ 새 편 쓰기] / 회차 목록(번호 · 제목 · 상태 · 크기 · 만든 때 · 고친 때).
/// 회차 목록 · 새 편 쓰기 · 우클릭 메뉴는 왼쪽 EpisodePanel의 것을 그대로 쓴다.
/// </summary>
internal sealed class NovelOverview : Panel
{
    private readonly EpisodePanel source;
    private readonly Panel head;
    private readonly VScrollBar pageScroll;
    private int pageOffset;
    private string? syncedNovel;
    private bool inPageLayout;
    private readonly Label metaLabel, titleLabel, descLabel;
    private readonly TagStrip tagStrip;
    private readonly CoverBox coverBox;
    private readonly PillButton newButton, folderButton, deleteButton, episodesTab, wikiTab, memoTab, refTab;
    private readonly IconButton reorderButton;
    private readonly SearchField search;
    private readonly TextBox memoBox;
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer memoTimer = new() { Interval = 600 };
    private int sortMode;
    private bool sortDesc;
    private readonly EpisodeList list;
    private readonly WikiList wikiList;
    private readonly RefList refList;
    private int tab; // 0 = 에피소드, 1 = 위키, 2 = 메모
    private string query = "";
    private bool loadingMemo;
    private LibraryPalette? palette;

    public event Action<string>? OpenRequested;
    public event Action<string>? WikiOpenRequested;
    /// <summary>참고 자료 한 줄을 눌렀다.</summary>
    public event Action<string>? RefOpenRequested;
    /// <summary>[소설 삭제하기] — 확인 · 삭제 · 서재로 돌아가기는 받는 쪽이 한다.</summary>
    public event Action<string>? DeleteRequested;
    /// <summary>기본 정보 창에서 제목을 바꿨다(작품 폴더 경로, 새 제목) — 폴더 이름 바꾸기와 다시 열기는 받는 쪽이 한다.</summary>
    public event Action<string, string>? RenameRequested;

    public NovelOverview(EpisodePanel source)
    {
        this.source = source;
        string ff = NovelStore.UiFontFamily;
        DoubleBuffered = true;
        metaLabel = new Label { AutoSize = true, Font = new Font(ff, 11.5f), UseMnemonic = false };
        titleLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = new Font(ff, 30f, FontStyle.Bold), UseMnemonic = false };
        descLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = new Font(ff, 10.5f), UseMnemonic = false, Cursor = Cursors.Hand, TextAlign = ContentAlignment.TopLeft, Padding = new Padding(18, 16, 18, 12) };
        descLabel.Click += (_, _) => OpenInfo();
        titleLabel.Click += (_, _) => OpenInfo();
        titleLabel.Cursor = Cursors.Hand;
        coverBox = new CoverBox();
        coverBox.Clicked += OpenInfo;
        tagStrip = new TagStrip();
        tagStrip.RemoveRequested += RemoveTag;
        tagStrip.TagSubmitted += AddTagText;
        newButton = new PillButton("새 편 쓰기") { Primary = true, Glyph = "", Font = new Font(ff, 10.5f, FontStyle.Bold) };
        newButton.Click += (_, _) => { if (tab == 1) ShowWikiMenu(); else if (tab == 3) source.AddReferences(); else ShowNewMenu(); };
        folderButton = new PillButton("탐색기에서 열기") { Compact = true, Glyph = "", Font = new Font(ff, 9.5f) };
        folderButton.Click += (_, _) => { if (source.NovelPath != null) System.Diagnostics.Process.Start("explorer.exe", $"\"{source.NovelPath}\""); };
        deleteButton = new PillButton("소설 삭제하기") { Compact = true, Glyph = "", Font = new Font(ff, 9.5f) };
        deleteButton.Click += (_, _) => { if (source.NovelPath != null) DeleteRequested?.Invoke(source.NovelPath); };
        reorderButton = new IconButton(IconKind.Select, "순서 관리 모드") { Label = "순서 관리", Font = new Font(ff, 10.5f) };
        reorderButton.Click += (_, _) => { reorderButton.Selected = !reorderButton.Selected; ApplyListMode(); };
        episodesTab = new PillButton("에피소드") { Selected = true, Glyph = "", Font = new Font(ff, 10f, FontStyle.Bold) };
        episodesTab.GlyphFactory = (sz, col) => PurpleIcons.Book(sz, col); // 위키 · 메모 아이콘과 같은 색(글자색에 맞춤)
        wikiTab = new PillButton("위키") { Glyph = "", Font = new Font(ff, 10f, FontStyle.Bold) };
        memoTab = new PillButton("메모") { Glyph = "", Font = new Font(ff, 10f, FontStyle.Bold) };
        refTab = new PillButton("참고자료") { Glyph = "", Font = new Font(ff, 10f, FontStyle.Bold) };
        episodesTab.Click += (_, _) => SetTab(0);
        wikiTab.Click += (_, _) => SetTab(1);
        memoTab.Click += (_, _) => SetTab(2);
        refTab.Click += (_, _) => SetTab(3);

        search = new SearchField("소설 내 검색하기…") { Size = new Size(300, 40) };
        search.Box.Font = new Font(ff, 10.5f);
        search.Box.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); ApplyQuery(search.Box.Text.Trim()); };

        wikiList = new WikiList { OnMain = true, Font = new Font(ff, 11f), Visible = false };
        wikiList.DocChosen += p => WikiOpenRequested?.Invoke(p);
        wikiList.MenuRequested += source.ShowWikiMenu;
        source.WikiReloaded += () => wikiList.SetDocs(FilteredWiki());
        refList = new RefList { Font = new Font(ff, 11f), Visible = false };
        refList.Chosen += p => RefOpenRequested?.Invoke(p);
        refList.MenuRequested += source.ShowRefMenu;
        refList.StateClicked += path => ChangeRefState(path);
        refList.FavoriteClicked += path =>
        {
            if (source.NovelPath is not string d) return;
            NovelStore.ToggleRefFavorite(d, Path.GetFileName(path));
            ReloadRefMeta();
        };
        source.RefsReloaded += () => refList.SetFiles(FilteredRefs());

        memoBox = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Font = new Font(ff, 11.5f), Visible = false, PlaceholderText = "이 소설에 대한 메모를 자유롭게 남겨 보세요. 자동으로 저장돼요." };
        memoBox.TextChanged += (_, _) => { if (!loadingMemo) { memoTimer.Stop(); memoTimer.Start(); } };
        memoBox.Leave += (_, _) => SaveMemoNow();
        memoTimer.Tick += (_, _) => SaveMemoNow();

        head = new Panel();
        pageScroll = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        pageScroll.ValueChanged += (_, _) => { newMenu?.Close(); pageOffset = pageScroll.Value; LayoutPage(); }; // 쪽을 내리면 떠 있는 [새 편 쓰기] 목록은 닫는다(화면에 혼자 남아 떠다니지 않게)
        head.Controls.AddRange(new Control[] { coverBox, metaLabel, titleLabel, descLabel, tagStrip, newButton, folderButton, deleteButton, reorderButton, episodesTab, wikiTab, memoTab, refTab, search });
        head.Layout += (_, _) => LayoutHead();

        list = new EpisodeList { OnMain = true, Compact = true, Font = new Font(ff, 11f) }; // 간단히 보기(줄 간격 좁게)로 고정
        list.EpisodeChosen += p => OpenRequested?.Invoke(p);
        list.MenuRequested += source.ShowEpisodeMenu;
        list.GroupMenuRequested += source.ShowGroupMenu;
        list.OrderChanged += () =>
        {
            if (source.NovelPath == null || query.Length > 0) { Sync(); return; } // 검색 중에는 일부만 보이니 순서를 저장하지 않는다
            NovelStore.SaveOrder(source.NovelPath, list.Paths);
            source.Reload();
        };
        list.GroupChanged += (path, grp) => NovelStore.SetEpisodeGroup(path, grp); // 순서 관리로 다른 그룹에 놓았다
        list.CanvasRequested += g => source.RequestCanvas(g); // 그룹 줄의 [캔버스] → 파일 트리와 같은 길로 연다
        list.SortHeaderClicked += OnHeaderSort;
        list.StatusClicked += CycleStatus;
        Controls.Add(list);
        Controls.Add(wikiList);
        Controls.Add(refList);
        Controls.Add(memoBox);
        Controls.Add(head);
        Controls.Add(pageScroll);
        Resize += (_, _) => { head.PerformLayout(); LayoutPage(); };
        source.Reloaded += Sync;
        Disposed += (_, _) => { SaveMemoNow(); searchTimer.Dispose(); memoTimer.Dispose(); };
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int AvailWidth => ClientSize.Width - (pageScroll.Visible ? pageScroll.Width : 0);
    private int ColumnWidth => Math.Min(AvailWidth - S(96) * 2, S(1040));
    private int ColumnLeft => Math.Max(S(24), (AvailWidth - ColumnWidth) / 2);

    private void LayoutHead()
    {
        int x = ColumnLeft, w = Math.Max(S(100), ColumnWidth), top = S(40);
        // 왼쪽: 표지 사진(누르면 기본 정보) / 오른쪽: 글 묶음 — 제목 → N편 · 로컬 → 태그 → 소개 상자(회색) → 버튼
        int cw = S(240), ch = S(360);
        coverBox.SetBounds(x, top, cw, ch);
        int tx = x + cw + S(48), tw = Math.Max(S(160), w - cw - S(48));
        int titleH = titleLabel.Font.Height + S(6);
        int boxW = Math.Min(tw, S(560)), boxH = S(148);
        int th = tagStrip.FitHeight(tw); // 태그(없으면 [새 태그 추가 +]만) 줄
        int bh = Math.Max(folderButton.Height, S(32));
        int y = top + S(26);
        titleLabel.SetBounds(tx - S(3), y, tw, titleH);
        y = titleLabel.Bottom + S(2);
        metaLabel.Location = new Point(tx, y);
        y = metaLabel.Bottom + S(12);
        tagStrip.SetBounds(tx, y, tw, th);
        y = tagStrip.Bottom + S(14);
        descLabel.SetBounds(tx, y, boxW, boxH);
        y = descLabel.Bottom + S(6);
        int bx = tx;
        foreach (var b in new Control[] { folderButton, deleteButton })
        {
            b.SetBounds(bx, y, b.Width, bh);
            bx = b.Right + S(10);
        }
        y = Math.Max(top + ch, y + bh) + S(40); // 표지 · 글 묶음 밑과 탭 줄 사이

        // 탭 줄: [에피소드 · 위키 · 메모] ………… [소설 내 검색]
        int tabH = S(34);
        // 폭은 글자 기준 원래 크기에서 계산한다(현재 폭에 더하면 배치할 때마다 커진다)
        int tabPad = S(8);
        episodesTab.SetBounds(x, y, episodesTab.GetPreferredSize(Size.Empty).Width + tabPad, tabH);
        wikiTab.SetBounds(episodesTab.Right + S(10), y, wikiTab.GetPreferredSize(Size.Empty).Width + tabPad, tabH);
        memoTab.SetBounds(wikiTab.Right + S(10), y, memoTab.GetPreferredSize(Size.Empty).Width + tabPad, tabH);
        refTab.SetBounds(memoTab.Right + S(10), y, refTab.GetPreferredSize(Size.Empty).Width + tabPad, tabH);
        // 검색칸: 탭 오른쪽에 자리가 있으면 그 줄 끝에, 좁으면 탭 줄 아래로 내린다(겹쳐서 잘리지 않게)
        int room = w - (refTab.Right - x) - S(24);
        bool below = room < S(180);
        int sw = below ? Math.Min(w, S(320)) : Math.Min(room, S(320));
        int sy = below ? y + tabH + S(10) : y + (tabH - S(36)) / 2;
        search.SetBounds(below ? x : x + w - sw - S(30), sy, sw, S(36)); // 오른쪽 도구 줄(새 편 쓰기)과 오른쪽 끝을 맞춘다
        search.Visible = tab != 2;
        y += tabH + S(18);
        if (below && tab != 2) y += S(36) + S(10);

        // 도구 줄(오른쪽): [순서 관리] [+ 새 편 쓰기]
        bool tools = tab != 2;
        newButton.Visible = tools;
        reorderButton.Visible = tab == 0;
        if (tools)
        {
            int nh = S(32);
            newButton.SetBounds(x + w - newButton.Width - S(30), y, newButton.Width, nh);
            int ix = newButton.Left - S(10);
            reorderButton.Size = new Size(reorderButton.WidthFor(nh), nh);
            reorderButton.Location = new Point(ix - reorderButton.Width, y);
            y += nh + S(8);
        }
        if (head.Height != y) head.Height = y;
        LayoutPage();
    }

    /// <summary>머리 밑 본문(목록 · 위키 · 메모)의 높이. 목록은 자기 안에서 따로 스크롤하지 않고 전체 높이를 다 펼친다 — 쪽 전체가 한 번에 내려간다.</summary>
    private int BodyHeight() => tab switch
    {
        0 => Math.Max(S(120), list.PreferredHeight),
        1 => Math.Max(S(120), wikiList.PreferredHeight),
        3 => Math.Max(S(120), refList.PreferredHeight),
        _ => Math.Max(S(260), ClientSize.Height - head.Height - S(24)),
    };

    /// <summary>머리 · 본문을 쪽 스크롤만큼 올려서 놓는다(쪽이 길면 오른쪽에 스크롤바).</summary>
    private void LayoutPage()
    {
        if (inPageLayout || ClientSize.Width <= 0) return;
        inPageLayout = true;
        bool wasVisible = pageScroll.Visible;
        try
        {
            int body = BodyHeight(), total = head.Height + body + S(32), vh = ClientSize.Height;
            bool need = total > vh && vh > 0;
            pageScroll.Visible = need;
            if (need)
            {
                pageOffset = Math.Clamp(pageOffset, 0, total - vh);
                pageScroll.Maximum = total;
                pageScroll.LargeChange = vh;
                pageScroll.SmallChange = S(60);
                if (pageScroll.Value != pageOffset) pageScroll.Value = pageOffset;
            }
            else pageOffset = 0;
            head.SetBounds(0, -pageOffset, AvailWidth, head.Height);
            var r = new Rectangle(ColumnLeft - S(10), head.Bottom, ColumnWidth + S(20), body);
            list.Bounds = r;
            wikiList.Bounds = r;
            refList.Bounds = r;
            memoBox.Bounds = new Rectangle(r.X + S(10), r.Y + S(4), Math.Max(0, r.Width - S(20)), Math.Max(0, r.Height - S(8)));
        }
        finally { inPageLayout = false; }
        // 스크롤바가 생기고 없어지면 폭이 달라지니 한 번 더(창 핸들이 생기기 전에는 BeginInvoke를 못 쓰니 그때는 다음 배치 때 맞춰진다)
        if (pageScroll.Visible != wasVisible && IsHandleCreated) BeginInvoke(() => { head.PerformLayout(); LayoutPage(); });
    }

    /// <summary>마우스 휠로 쪽을 내린다(목록 위에서도 — 목록은 자기 스크롤이 없다).</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        newMenu?.Close();
        wikiMenu?.Close();
        if (!pageScroll.Visible) return;
        int max = Math.Max(0, pageScroll.Maximum - pageScroll.LargeChange + 1);
        pageScroll.Value = Math.Clamp(pageScroll.Value - Math.Sign(e.Delta) * S(90), 0, max);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutPage();
    }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = head.BackColor = metaLabel.BackColor = titleLabel.BackColor = p.Main;
        descLabel.BackColor = Themes.Blend(p.Main, p.Fore, p.Dark ? 0.09f : 0.07f); // 소개는 연한 회색 상자
        titleLabel.ForeColor = p.Fore;
        metaLabel.ForeColor = p.Dim;
        memoBox.BackColor = p.Main;
        memoBox.ForeColor = p.Fore;
        foreach (var b in new[] { newButton, folderButton, deleteButton, episodesTab, wikiTab, memoTab, refTab }) b.SetPalette(p);
        deleteButton.TextColor = p.Dark ? Color.FromArgb(0xFF, 0x6B, 0x6B) : Color.FromArgb(0xE0, 0x2B, 0x2B);
        reorderButton.SetPalette(p);
        search.SetPalette(p);
        tagStrip.SetPalette(p);
        coverBox.SetPalette(p);
        list.SetPalette(p);
        wikiList.SetPalette(p);
        refList.SetPalette(p);
        UpdateDescStyle();
    }

    private void UpdateDescStyle()
    {
        if (palette == null) return;
        bool has = !string.IsNullOrWhiteSpace(descLabel.Tag as string) && (string)descLabel.Tag! != NovelStore.DefaultDescription;
        descLabel.ForeColor = has ? palette.Dim : Themes.Blend(palette.Main, palette.Dim, 0.7f);
    }

    /// <summary>왼쪽 회차 패널과 같은 내용으로 맞춘다.</summary>
    public void Sync()
    {
        if (source.NovelPath is not string dir) return;
        if (!string.Equals(syncedNovel, dir, StringComparison.OrdinalIgnoreCase)) { syncedNovel = dir; pageOffset = 0; pageScroll.Value = 0; } // 다른 작품으로 들어오면 맨 위부터
        var meta = NovelStore.LoadMeta(dir);
        titleLabel.Text = Path.GetFileName(dir);
        string desc = meta.Description?.Replace("\r", "").Replace('\n', ' ') ?? "";
        descLabel.Tag = desc;
        descLabel.Text = desc.Length > 0 ? desc : NovelStore.DefaultDescription;
        metaLabel.Text = $"{source.EpisodePaths.Count}편 · 로컬";
        tagStrip.SetTags(meta.Tags ?? new());
        coverBox.LoadFor(dir); // 표지 이미지(바뀐 때만 다시 읽는다)
        list.SetStatuses(dir, meta.Status);
        list.SetTypes(dir, meta.Types);
        list.SetGroups(source.GroupNames, source.GroupMap);
        refList.SetMeta(meta.RefStatus, meta.RefFavorites);
        ApplyQuery(query, force: true);
        if (!memoBox.Focused) { loadingMemo = true; memoBox.Text = meta.Memo ?? ""; loadingMemo = false; }
        UpdateDescStyle();
        head.PerformLayout();
        PerformLayout();
    }

    // ───────── 탭 · 검색 ─────────

    /// <summary>[에피소드 | 위키 | 메모] 전환. 주 버튼도 [+ 새 편 쓰기] · [+ 새 위키 문서]로 바뀐다.</summary>
    public void SetWikiMode(bool wiki) => SetTab(wiki ? 1 : 0);

    private void SetTab(int t)
    {
        SaveMemoNow();
        tab = t;
        episodesTab.Selected = t == 0;
        wikiTab.Selected = t == 1;
        memoTab.Selected = t == 2;
        refTab.Selected = t == 3;
        refList.Visible = t == 3;
        list.Visible = t == 0;
        wikiList.Visible = t == 1;
        memoBox.Visible = t == 2;
        newButton.Text = t == 1 ? "새 위키 문서" : t == 3 ? "참고 자료 넣기" : "새 편 쓰기";
        newButton.Size = newButton.GetPreferredSize(Size.Empty);
        if (t == 1) wikiList.SetDocs(FilteredWiki());
        if (t == 3) refList.SetFiles(FilteredRefs());
        if (t == 2) memoBox.Focus();
        head.PerformLayout();
        PerformLayout();
    }

    private List<string> FilteredRefs() =>
        query.Length == 0 ? source.RefPaths
            : source.RefPaths.Where(p => Path.GetFileName(p).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

    private List<WikiDoc> FilteredWiki() =>
        query.Length == 0 ? source.WikiDocs
            : source.WikiDocs.Where(d => d.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || d.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

    private void ApplyQuery(string q, bool force = false)
    {
        if (!force && q == query) return;
        query = q;
        bool filtering = q.Length > 0;
        if (filtering && reorderButton.Selected) { reorderButton.Selected = false; ApplyListMode(); }
        reorderButton.Enabled = sortMode == 0 && !filtering;
        list.ShowEmptyGroups = !filtering; // 검색 중에는 빈 그룹을 숨긴다
        list.EmptyText = filtering ? $"'{q}'에 맞는 에피소드가 없어요." : null;
        IEnumerable<string> eps = source.EpisodePaths;
        if (filtering)
            eps = eps.Where(p => Path.GetFileNameWithoutExtension(p).Contains(q, StringComparison.CurrentCultureIgnoreCase) || NovelStore.ContainsText(p, q));
        list.SetPaths(eps.ToList());
        list.RefreshCounts();
        wikiList.SetDocs(FilteredWiki());
        refList.SetFiles(FilteredRefs());
    }

    // ───────── 정렬 · 목록 설정 ─────────

    private void OnHeaderSort(int col)
    {
        if (sortMode == col) { if (!sortDesc) sortDesc = true; else { sortMode = 0; sortDesc = false; } }
        else { sortMode = col; sortDesc = false; }
        ApplySort();
    }

    private void ApplyListMode()
    {
        list.ReorderMode = reorderButton.Selected;
        list.Invalidate();
    }

    private void ApplySort()
    {
        list.SetSort(sortMode, sortDesc);
        if (sortMode != 0 && reorderButton.Selected) { reorderButton.Selected = false; ApplyListMode(); }
        reorderButton.Enabled = sortMode == 0 && query.Length == 0;
        ApplyQuery(query, force: true);
    }

    // ───────── 소개 · 태그 · 상태 · 메모 ─────────

    private ToolStripDropDown? newMenu;

    /// <summary>[새 편 쓰기]를 누르면 버튼 밑에 뜨는 목록: 에피소드(바로 쓰기) / 에피소드 그룹(회차를 묶는 그룹).</summary>
    private ToolStripDropDown? wikiMenu;

    /// <summary>[새 위키 문서]를 누르면 밑에 뜨는 목록: 문서 / 분류 / 템플릿. 분류 · 템플릿은 아직 준비 중이다.</summary>
    private void ShowWikiMenu()
    {
        wikiMenu?.Dispose();
        var font = new Font(NovelStore.UiFontFamily, 9.5f);
        var panel = new IconMenuPanel(font, DeviceDpi, BackColor, ForeColor);
        panel.Add("문서", PurpleIcons.Document(S(40)), () => BeginInvoke(() => source.CreateWiki()));
        panel.Add("분류", PurpleIcons.Folder(S(40)), () => BeginInvoke(() => MessageBox.Show(FindForm(), "분류 만들기는 준비 중이에요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information)));
        panel.Add("템플릿", PurpleIcons.Copy(S(40)), () => BeginInvoke(() => MessageBox.Show(FindForm(), "템플릿으로 만들기는 준비 중이에요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information)));
        panel.Size = panel.MeasureAll();
        var dd = wikiMenu = new ToolStripDropDown { Padding = Padding.Empty, BackColor = BackColor, AutoSize = true };
        dd.Items.Add(new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size });
        panel.Chosen += () => dd.Close();
        dd.Show(newButton, new Point(0, newButton.Height + S(6)));
    }

    private void ShowNewMenu()
    {
        try { ShowNewMenuCore(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), "새 편 쓰기 메뉴를 열지 못했어요: " + ex.Message, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ShowNewMenuCore()
    {
        newMenu?.Dispose();
        var menuFont = new Font(NovelStore.UiFontFamily, 9.5f);
        var gray = Color.FromArgb(150, 148, 146);
        // 아이콘은 직접 그린 선 아이콘(에피소드 = 책, 에피소드 그룹 = 줄 목록). 목록 안쪽 전체를 직접 그리는 컨트롤 하나를 상자에 넣는다(IconMenuPanel)
        var panel = new IconMenuPanel(menuFont, DeviceDpi, BackColor, ForeColor);
        panel.Add("에피소드", PurpleIcons.Book(S(40), gray), () => BeginInvoke(() => source.AddEpisode()));
        panel.Add("새 에피소드 그룹", PurpleIcons.Lines(S(40), gray), () => BeginInvoke(() => source.CreateGroup()));
        panel.Size = panel.MeasureAll();
        var dd = newMenu = new ToolStripDropDown { Padding = Padding.Empty, BackColor = BackColor, AutoSize = true };
        dd.Items.Add(new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size });
        panel.Chosen += () => dd.Close();
        dd.Show(newButton, new Point(0, newButton.Height + S(6))); // 버튼 왼쪽 끝에 맞춰 오른쪽으로 펼친다
    }

    /// <summary>제목이나 설명을 눌렀다: [기본 정보] 창에서 제목 · 작품 설명을 고친다.</summary>
    private void OpenInfo()
    {
        if (source.NovelPath is not string dir) return;
        string oldTitle = Path.GetFileName(dir);
        using var dlg = new NovelInfoDialog(oldTitle, NovelStore.LoadMeta(dir).Description, dir);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        NovelStore.SaveDescription(dir, dlg.DescText);
        if (dlg.RemoveCover) NovelStore.DeleteCover(dir);
        if (dlg.NewCoverSource != null) NovelStore.SaveCover(dir, dlg.NewCoverSource); // 폴더 이름을 바꾸기 전에 저장(파일이 같이 따라간다)
        if (dlg.TitleText != oldTitle) RenameRequested?.Invoke(dir, dlg.TitleText); // 폴더 이름이 바뀌니 받는 쪽이 문서를 닫고 새 경로로 다시 연다
        else source.LoadNovel(dir);
    }

    /// <summary>태그 줄 입력칸에서 Enter로 들어온 태그 하나를 저장한다(입력칸은 열린 채로 다음 태그를 받는다).</summary>
    private void AddTagText(string tag)
    {
        if (source.NovelPath is not string dir || tag.Length == 0) return;
        var tags = NovelStore.LoadMeta(dir).Tags ?? new();
        if (tags.Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase))) return;
        tags.Add(tag);
        NovelStore.SaveTags(dir, tags);
        Sync();
    }

    private void RemoveTag(string tag)
    {
        if (source.NovelPath is not string dir) return;
        var tags = (NovelStore.LoadMeta(dir).Tags ?? new()).Where(t => t != tag).ToList();
        NovelStore.SaveTags(dir, tags);
        Sync();
    }

    /// <summary>참고 자료 독서상태 뱃지를 누를 때마다 안 읽음 → 읽는 중 → 완독 → 안 읽음.</summary>
    private void ChangeRefState(string path)
    {
        if (source.NovelPath is not string d) return;
        string name = Path.GetFileName(path);
        var names = RefList.StateNames;
        string cur = NovelStore.LoadMeta(d).RefStatus?.GetValueOrDefault(name) ?? names[0];
        int i = Math.Max(0, Array.IndexOf(names, cur));
        NovelStore.SaveRefState(d, name, names[(i + 1) % names.Length]);
        ReloadRefMeta();
    }

    private void ReloadRefMeta()
    {
        if (source.NovelPath is not string d) return;
        var m = NovelStore.LoadMeta(d);
        refList.SetMeta(m.RefStatus, m.RefFavorites);
    }

    /// <summary>집필 상태 뱃지를 누를 때마다 준비 → 집필 → 퇴고 → 완료 → 준비.</summary>
    private void CycleStatus(string path)
    {
        var names = EpisodeList.StatusNames;
        string next = names[(Array.IndexOf(names, list.StatusOf(path)) + 1) % names.Length];
        NovelStore.SaveStatus(path, next);
        if (source.NovelPath is string dir) list.SetStatuses(dir, NovelStore.LoadMeta(dir).Status);
    }

    private void SaveMemoNow()
    {
        memoTimer.Stop();
        if (loadingMemo || source.NovelPath is not string dir) return;
        if ((NovelStore.LoadMeta(dir).Memo ?? "") == memoBox.Text) return;
        NovelStore.SaveMemo(dir, memoBox.Text);
    }
}

/// <summary>
/// 작품 화면의 태그 줄: 작은 둥근 칩들(오른쪽 ×로 지움)과 줄 끝의 [새 태그 추가 +].
/// [새 태그 추가]를 누르면 그 자리가 입력칸으로 바뀌고(선택 상태), Enter를 칠 때마다 태그가 하나씩 들어가며 바로 다음 태그를 적을 수 있다.
/// 빈 칸에서 Enter · Esc · 다른 곳 클릭이면 입력이 끝난다.
/// </summary>
internal sealed class TagStrip : Control
{
    private LibraryPalette? palette;
    private List<string> tags = new();
    private readonly List<(Rectangle Rect, string Tag)> chips = new();
    private readonly TextBox editor;
    private bool editing;
    private Rectangle addRect;
    private bool hoverAdd;
    private int hoverX = -1; // 마우스가 올라간 ×의 칩 번호

    public event Action<string>? RemoveRequested;
    /// <summary>입력칸에서 Enter로 태그 하나가 적혔다(받는 쪽이 저장하고 SetTags로 다시 알려 준다).</summary>
    public event Action<string>? TagSubmitted;

    public TagStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font(NovelStore.UiFontFamily, 9f);
        TabStop = false;
        editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, PlaceholderText = "새 태그", Font = Font };
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string t = editor.Text.Replace("\r", " ").Replace('\n', ' ').Trim().TrimStart('#').Trim();
                if (t.Length == 0) { EndEdit(); return; } // 빈 칸에서 Enter = 끝
                editor.Clear();
                TagSubmitted?.Invoke(t); // 입력칸은 그대로 남아 다음 태그를 받는다
            }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(); }
        };
        editor.LostFocus += (_, _) => FinishOnClickAway(); // 다른 컨트롤로 포커스가 가면(창 전환 포함) 끝낸다
        Controls.Add(editor);
    }

    /// <summary>입력칸 밖을 누르면 끝낸다. 적던 글이 있으면 그 태그는 살려 두고, 비었으면 그냥 닫는다.</summary>
    private void FinishOnClickAway()
    {
        if (!editing) return;
        string t = editor.Text.Replace("\r", " ").Replace('\n', ' ').Trim().TrimStart('#').Trim();
        EndEdit();
        if (t.Length > 0) TagSubmitted?.Invoke(t);
    }

    /// <summary>입력 중에만 켜 두는 창 전체 마우스 감시: 입력칸이 아닌 곳(포커스를 안 받는 빈 곳 · 라벨 포함)을 누르면 끝낸다.</summary>
    private sealed class ClickAwayFilter : IMessageFilter
    {
        private readonly TagStrip owner;
        public ClickAwayFilter(TagStrip owner) => this.owner = owner;
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_NCLBUTTONDOWN = 0xA1;
            if (m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_NCLBUTTONDOWN && m.HWnd != owner.editor.Handle)
                owner.BeginInvoke(owner.FinishOnClickAway); // 클릭 처리가 끝난 뒤에 닫는다(눌린 버튼 동작은 그대로)
            return false;
        }
    }
    private ClickAwayFilter? clickFilter;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        editor.BackColor = p.Card;
        editor.ForeColor = p.Fore;
        Invalidate();
    }

    public void SetTags(List<string> t) { tags = t.ToList(); Invalidate(); }

    /// <summary>[새 태그 추가]를 눌렀을 때: 그 자리를 입력칸으로 바꾸고 포커스를 준다.</summary>
    public void BeginEdit()
    {
        if (editing) return;
        editing = true;
        editor.Clear();
        Parent?.PerformLayout(); // 입력칸 자리를 잡는다(FitHeight)
        editor.Visible = true;
        editor.Focus();
        Application.AddMessageFilter(clickFilter = new ClickAwayFilter(this));
        Invalidate();
    }

    private void EndEdit()
    {
        if (!editing) return;
        editing = false;
        if (clickFilter != null) { Application.RemoveMessageFilter(clickFilter); clickFilter = null; }
        editor.Visible = false;
        editor.Clear();
        Parent?.PerformLayout();
        Invalidate();
    }

    private int ChipH => Font.Height + S(8);

    /// <summary>이 폭에서 칩들이 차지할 높이. 줄 끝에는 늘 [새 태그 추가 +](쓰는 중이면 입력칸)가 붙는다.</summary>
    public int FitHeight(int width)
    {
        chips.Clear();
        int x = 0, y = 0, h = ChipH, gap = S(6);
        foreach (var t in tags)
        {
            int cw = TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(12) + S(20); // 글자 양옆 여백 + 오른쪽 ×자리
            cw = Math.Min(cw, Math.Max(S(50), width));
            if (x > 0 && x + cw > width) { x = 0; y += h + gap; }
            chips.Add((new Rectangle(x, y, cw, h), t));
            x += cw + gap;
        }
        int aw = TextRenderer.MeasureText("새 태그 추가", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(34);
        if (x > 0 && x + aw > width) { x = 0; y += h + gap; }
        addRect = new Rectangle(x, y, aw, h);
        if (editing) editor.SetBounds(addRect.X + S(8), addRect.Y + (h - editor.Height) / 2, addRect.Width - S(16), editor.Height); // 버튼과 같은 자리 · 같은 크기
        return y + h;
    }

    private Rectangle XRect(Rectangle chip) => new(chip.Right - S(20), chip.Y, S(20), chip.Height);

    private int XIndexAt(Point pt)
    {
        for (int i = 0; i < chips.Count; i++) if (XRect(chips[i].Rect).Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = XIndexAt(e.Location);
        bool ha = !editing && addRect.Contains(e.Location);
        if (i != hoverX || ha != hoverAdd) { hoverX = i; hoverAdd = ha; Invalidate(); }
        Cursor = i >= 0 || ha ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; hoverAdd = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            int xi = XIndexAt(e.Location);
            if (xi >= 0) { hoverX = -1; RemoveRequested?.Invoke(chips[xi].Tag); }
            else if (!editing && addRect.Contains(e.Location)) BeginEdit();
            return;
        }
        if (e.Button != MouseButtons.Right) return;
        foreach (var (rect, tag) in chips)
            if (rect.Contains(e.Location))
            {
                var m = new ContextMenuStrip();
                m.Items.Add($"'{tag}' 태그 지우기", null, (_, _) => RemoveRequested?.Invoke(tag));
                m.Show(this, e.Location);
                return;
            }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        for (int i = 0; i < chips.Count; i++)
        {
            var (rect, tag) = chips[i];
            using (var path = LibraryPalette.Rounded(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1.5f, rect.Height - 1.5f), S(5)))
            {
                using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.05f))) g.FillPath(b, path);
                using (var pen = new Pen(p.Line)) g.DrawPath(pen, path);
            }
            var textRect = new Rectangle(rect.X + S(6), rect.Y, rect.Width - S(20) - S(6), rect.Height);
            TextRenderer.DrawText(g, tag, Font, textRect, p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            // 오른쪽 ×(누르면 이 태그를 지운다)
            var xr = XRect(rect);
            float cx = xr.X + xr.Width / 2f - S(1), cy = xr.Y + xr.Height / 2f, d = 2.8f * u;
            if (hoverX == i) using (var hb = new SolidBrush(p.CardHover)) g.FillEllipse(hb, cx - 7 * u, cy - 7 * u, 14 * u, 14 * u);
            using var xp = new Pen(hoverX == i ? p.Fore : p.Dim, Math.Max(1.2f, 1.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
        }
        if (addRect.Width <= 0) return;
        if (editing)
        {
            // 선택 상태: 강조색 테두리 + 입력칸
            using var path = LibraryPalette.Rounded(new RectangleF(addRect.X + 0.5f, addRect.Y + 0.5f, addRect.Width - 1.5f, addRect.Height - 1.5f), S(5));
            using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);
            using (var pen = new Pen(p.Accent, Math.Max(1.5f, 1.5f * u))) g.DrawPath(pen, path);
            return;
        }
        if (hoverAdd)
            using (var hp = LibraryPalette.Rounded(addRect, S(5))) using (var hb = new SolidBrush(p.CardHover)) g.FillPath(hb, hp);
        TextRenderer.DrawText(g, "새 태그 추가", Font, new Rectangle(addRect.X + S(8), addRect.Y, addRect.Width - S(28), addRect.Height), p.Dim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        float px = addRect.Right - S(12), py = addRect.Y + addRect.Height / 2f, pd = 3.5f * u;
        using var pp = new Pen(hoverAdd ? p.Fore : p.Dim, Math.Max(1.2f, 1.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pp, px - pd, py, px + pd, py);
        g.DrawLine(pp, px, py - pd, px, py + pd);
    }
}

/// <summary>
/// 소설 메인 머리의 표지 사진(왼쪽). 저장된 표지가 있으면 그림을, 없으면 "그림이 아직 준비되지 못했어요"를 보여 준다.
/// 눌러도 기본 정보 창으로 간다(Clicked). 파일이 바뀐 때만 다시 읽는다.
/// </summary>
internal sealed class CoverBox : Control
{
    private LibraryPalette? palette;
    private Bitmap? image;
    private string? loadedKey;
    private bool hover;

    public event Action? Clicked;

    public CoverBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        new ToolTip().SetToolTip(this, "눌러서 기본 정보 고치기");
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    /// <summary>이 작품의 표지를 읽는다(파일 크기 · 고친 때가 그대로면 다시 읽지 않는다).</summary>
    public void LoadFor(string novelDir)
    {
        string file = NovelStore.CoverFile(novelDir);
        string key;
        try { var fi = new FileInfo(file); key = fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : ""; } catch { key = ""; }
        if (key == loadedKey) return;
        loadedKey = key;
        image?.Dispose();
        image = key.Length == 0 ? null : NovelStore.LoadCover(novelDir);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Clicked?.Invoke(); }
    protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(S(2), S(2), Width - S(10), Height - S(10));
        using (var sh = LibraryPalette.Rounded(new RectangleF(r.X + S(2), r.Y + S(4), r.Width, r.Height), S(8)))
        using (var sb = new SolidBrush(Color.FromArgb(p.Dark ? 80 : 34, Color.Black))) g.FillPath(sb, sh);
        using var path = LibraryPalette.Rounded(r, S(8));
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.06f))) g.FillPath(b, path);
        if (image != null)
        {
            var old = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, Rectangle.Round(r));
            g.Clip = old;
        }
        else
        {
            using var f = new Font(NovelStore.UiFontFamily, 11f);
            TextRenderer.DrawText(g, "그림이 아직\n준비되지 못했어요", f, Rectangle.Round(r), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        using var pen = new Pen(hover ? p.Accent : Themes.Blend(p.Main, p.Fore, 0.12f), hover ? 2f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 소설 메인의 [참고자료] 탭 목록. 머리줄 [파일명 · 형식 · 크기 · 수정일 · 독서상태 · 즐겨찾기]를 누르면 그 열로 정렬하고,
/// 줄마다 [종류 아이콘] 이름 ……… 형식 · 크기 · 고친 때 · [독서상태 뱃지] · [★]. 줄을 누르면 Chosen, 오른쪽 누르면 MenuRequested.
/// 독서상태 뱃지는 누를 때마다 안 읽음 → 읽는 중 → 완독, ★은 즐겨찾기 켜고 끄기.
/// 쪽 전체가 스크롤되므로 자기 스크롤바는 없고, PreferredHeight만큼 다 펼친다.
/// </summary>
internal sealed class RefList : Control
{
    public static readonly string[] StateNames = { "안 읽음", "읽는 중", "완독" };

    private List<string> files = new();
    private readonly Dictionary<string, (long Size, DateTime Edited)> info = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> favs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Rectangle Rect, string Path)> badges = new();
    private readonly List<(Rectangle Rect, string Path)> stars = new();
    private LibraryPalette? palette;
    private int hover = -1;
    private int sortCol; // 0 파일명 · 1 형식 · 2 크기 · 3 수정일 · 4 독서상태 · 5 즐겨찾기
    private bool sortDesc;

    public event Action<string>? Chosen;
    public event Action<string, Point>? MenuRequested;
    public event Action<string>? StateClicked;
    public event Action<string>? FavoriteClicked;

    public RefList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 11f);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int RowH => Font.Height + S(26);
    private int Top0 => S(46);
    public int PreferredHeight => Top0 + files.Count * RowH + S(16);

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Main; Invalidate(); }

    /// <summary>독서상태 · 즐겨찾기 표(.caffeum.json)를 받아 둔다(파일 이름 기준).</summary>
    public void SetMeta(Dictionary<string, string>? stateMap, List<string>? favorites)
    {
        states.Clear();
        favs.Clear();
        if (stateMap != null) foreach (var (k, v) in stateMap) states[k] = v;
        if (favorites != null) foreach (var k in favorites) favs.Add(k);
        files = Sorted(files);
        Invalidate();
    }

    public void SetFiles(List<string> list)
    {
        info.Clear();
        foreach (var f in list)
            try { var fi = new FileInfo(f); info[f] = (fi.Length, fi.LastWriteTime); } catch { info[f] = (0, DateTime.MinValue); }
        files = Sorted(list);
        hover = -1;
        Invalidate();
        Parent?.PerformLayout(); // 높이가 바뀌니 쪽 배치를 다시
    }

    private string StateOf(string path) => states.TryGetValue(Path.GetFileName(path), out var s) && StateNames.Contains(s) ? s : StateNames[0];
    private bool IsFav(string path) => favs.Contains(Path.GetFileName(path));

    internal static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".zip" or ".cbz" => "만화",
        ".pdf" => "PDF",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "이미지",
        ".epub" => "전자책",
        ".hwp" or ".hwpx" => "한글 문서",
        _ => "소설",
    };

    private List<string> Sorted(List<string> src)
    {
        IEnumerable<string> q = sortCol switch
        {
            1 => src.OrderBy(Kind).ThenBy(Path.GetFileName, NaturalStringComparer.Instance),
            2 => src.OrderBy(f => info.TryGetValue(f, out var i) ? i.Size : 0),
            3 => src.OrderBy(f => info.TryGetValue(f, out var i) ? i.Edited : DateTime.MinValue),
            4 => src.OrderBy(f => Array.IndexOf(StateNames, StateOf(f))),
            5 => src.OrderBy(f => IsFav(f) ? 0 : 1), // 오름차순 = 즐겨찾기가 먼저
            _ => src.OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance),
        };
        var list = q.ToList();
        if (sortDesc) list.Reverse();
        return list;
    }

    // 열 자리(오른쪽 끝부터): [즐겨찾기] [독서상태] [수정일] [크기] [형식]
    private int ColFav(int w) => w - S(10) - S(8) - S(90);
    private int ColState(int w) => ColFav(w) - S(120);
    private int ColEdited(int w) => ColState(w) - S(112);
    private int ColSize(int w) => ColEdited(w) - S(104);
    private int ColKind(int w) => ColSize(w) - S(96);
    private int NameLeft => S(10) + S(48);
    /// <summary>이름 칸이 너무 좁아지면 형식 · 크기 열을 숨긴다.</summary>
    private bool Wide(int w) => ColKind(w) - NameLeft >= S(200);

    private Rectangle HeaderCell(int col, int w)
    {
        int x = col switch { 0 => S(18), 1 => ColKind(w), 2 => ColSize(w), 3 => ColEdited(w), 4 => ColState(w), _ => ColFav(w) };
        int cw = col == 0 ? S(110) : col == 5 ? S(90) : S(96);
        return new Rectangle(x, S(8), cw, Top0 - S(8));
    }

    private bool ColVisible(int col, int w) => col is 0 or 3 or 4 or 5 || Wide(w);

    private int HeaderColAt(Point pt)
    {
        if (pt.Y >= Top0) return -1;
        for (int c = 0; c <= 5; c++) if (ColVisible(c, Width) && HeaderCell(c, Width).Contains(pt)) return c;
        return -1;
    }

    private int IndexAt(int y) { int v = y - S(4) - Top0; int i = v / RowH; return v < 0 || i >= files.Count ? -1 : i; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i != hover) { hover = i; Invalidate(); }
        bool hand = HeaderColAt(e.Location) >= 0 || badges.Any(b => b.Rect.Contains(e.Location)) || stars.Any(s => s.Rect.Contains(e.Location)) || i >= 0;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            int col = HeaderColAt(e.Location);
            if (col >= 0)
            {
                if (sortCol == col) sortDesc = !sortDesc; else { sortCol = col; sortDesc = false; }
                files = Sorted(files);
                Invalidate();
                return;
            }
            if (badges.FirstOrDefault(b => b.Rect.Contains(e.Location)) is { Path: not null } bd) { StateClicked?.Invoke(bd.Path); return; }
            if (stars.FirstOrDefault(s => s.Rect.Contains(e.Location)) is { Path: not null } st) { FavoriteClicked?.Invoke(st.Path); return; }
        }
        int i = IndexAt(e.Y);
        if (i < 0) return;
        if (e.Button == MouseButtons.Left) Chosen?.Invoke(files[i]);
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(files[i], PointToScreen(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(palette?.Main ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Width, pad = S(10), rowH = RowH, top0 = Top0;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var meas = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        using var icon = new Font(MainForm.IconFontName(), Font.SizeInPoints);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var badgeFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.8f);
        using var star = new Font("Segoe MDL2 Assets", Font.SizeInPoints * 1.05f);
        badges.Clear();
        stars.Clear();
        bool wide = Wide(w);

        g.SetClip(new Rectangle(0, top0, w, Math.Max(0, Height - top0)));
        for (int i = 0; i < files.Count; i++)
        {
            int y = S(4) + top0 + i * rowH;
            var row = new Rectangle(pad, y + S(2), w - pad * 2, rowH - S(4));
            string path = files[i];
            if (i == hover)
            {
                using var rp = LibraryPalette.Rounded(row, S(8));
                using var rb = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.05f));
                g.FillPath(rb, rp);
            }
            TextRenderer.DrawText(g, NovelStore.ReferenceGlyph(path), icon, new Rectangle(row.X + S(12), row.Y, S(24), row.Height), p.Accent, flags | TextFormatFlags.HorizontalCenter);
            int nameRight = (wide ? ColKind(w) : ColEdited(w)) - S(12);
            TextRenderer.DrawText(g, Path.GetFileName(path), Font, new Rectangle(NameLeft, row.Y, Math.Max(0, nameRight - NameLeft), row.Height), p.Fore, flags);
            info.TryGetValue(path, out var fi);
            if (wide)
            {
                TextRenderer.DrawText(g, Kind(path), small, new Rectangle(ColKind(w), row.Y, S(90), row.Height), p.Dim, flags);
                string sz = fi.Size >= 1048576 ? $"{fi.Size / 1048576.0:0.0}MB" : fi.Size >= 1024 ? $"{fi.Size / 1024.0:0}KB" : $"{fi.Size}B";
                TextRenderer.DrawText(g, sz, small, new Rectangle(ColSize(w), row.Y, S(90), row.Height), p.Dim, flags);
            }
            TextRenderer.DrawText(g, NovelStore.Ago(fi.Edited), small, new Rectangle(ColEdited(w), row.Y, S(104), row.Height), p.Dim, flags);

            // 독서상태 뱃지(누르면 바뀐다)
            string st = StateOf(path);
            int bw = TextRenderer.MeasureText(st, badgeFont, Size.Empty, meas).Width + S(18), bh = badgeFont.Height + S(8);
            var br = new Rectangle(ColState(w), row.Y + (row.Height - bh) / 2, bw, bh);
            bool done = st == StateNames[2], reading = st == StateNames[1];
            using (var bp = LibraryPalette.Rounded(br, S(5)))
            {
                using (var bb = new SolidBrush(done ? p.AccentSoft : reading ? Themes.Blend(p.Main, p.Accent, 0.1f) : Themes.Blend(p.Main, p.Fore, 0.06f))) g.FillPath(bb, bp);
                using (var bpen = new Pen(done ? Themes.Blend(p.AccentSoft, p.Accent, 0.5f) : p.Line)) g.DrawPath(bpen, bp);
            }
            TextRenderer.DrawText(g, st, badgeFont, br, done ? p.Accent : p.Dim, flags | TextFormatFlags.HorizontalCenter);
            badges.Add((br, path));

            // 즐겨찾기 별(누르면 켜고 끈다)
            var sr = new Rectangle(ColFav(w), row.Y, S(36), row.Height);
            bool fav = IsFav(path);
            TextRenderer.DrawText(g, fav ? "" : "", star, sr, fav ? Color.FromArgb(0xF2, 0xB3, 0x3D) : Themes.Blend(p.Main, p.Dim, 0.7f), flags | TextFormatFlags.HorizontalCenter);
            stars.Add((sr, path));
        }
        g.ResetClip();
        if (files.Count == 0)
            TextRenderer.DrawText(g, "아직 참고 자료가 없어요.\n[참고 자료 넣기]로 소설 · 만화 · PDF · 이미지를 넣어 두세요.", Font, new Rectangle(0, top0 + S(24), w, S(80)), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        // 머리줄: 파일명 ^ · 형식 ⇅ · 크기 ⇅ · 수정일 ⇅ · 독서상태 ⇅ · 즐겨찾기 ⇅
        using (var hb = new SolidBrush(p.Main)) g.FillRectangle(hb, 0, 0, w, top0);
        using var headFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.92f, FontStyle.Bold);
        string[] labels = { "파일명", "형식", "크기", "수정일", "독서상태", "즐겨찾기" };
        float u = DeviceDpi / 96f;
        for (int col = 0; col <= 5; col++)
        {
            if (!ColVisible(col, w)) continue;
            var cell = HeaderCell(col, w);
            int state = sortCol == col ? (sortDesc ? 2 : 1) : 0;
            bool active = state != 0;
            TextRenderer.DrawText(g, labels[col], headFont, new Rectangle(cell.X, cell.Y, cell.Width - S(24), cell.Height - S(4)), active ? p.Fore : p.Dim, flags);
            int lw = TextRenderer.MeasureText(labels[col], headFont, Size.Empty, meas).Width;
            float ax = cell.X + lw + S(14), ay = cell.Y + (cell.Height - S(4)) / 2f;
            Color soft = Themes.Blend(p.Main, p.Accent, 0.55f);
            using var upPen = new Pen(state == 1 ? p.Accent : soft, Math.Max(1.4f, 1.4f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var dnPen = new Pen(state == 2 ? p.Accent : soft, Math.Max(1.4f, 1.4f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (col == 0 && state != 0 || col == 0)
            {
                float s2 = state == 2 ? -1 : 1; // 파일명은 ^ (내림차순이면 v)
                g.DrawLine(upPen, ax - 4 * u, ay + 2 * u * s2, ax, ay - 2 * u * s2);
                g.DrawLine(upPen, ax, ay - 2 * u * s2, ax + 4 * u, ay + 2 * u * s2);
            }
            else
            {
                g.DrawLine(upPen, ax - 3 * u, ay - 5 * u, ax - 3 * u, ay + 5 * u); // ↑
                g.DrawLine(upPen, ax - 5.5f * u, ay - 2.5f * u, ax - 3 * u, ay - 5 * u);
                g.DrawLine(upPen, ax - 0.5f * u, ay - 2.5f * u, ax - 3 * u, ay - 5 * u);
                g.DrawLine(dnPen, ax + 3 * u, ay - 5 * u, ax + 3 * u, ay + 5 * u); // ↓
                g.DrawLine(dnPen, ax + 0.5f * u, ay + 2.5f * u, ax + 3 * u, ay + 5 * u);
                g.DrawLine(dnPen, ax + 5.5f * u, ay + 2.5f * u, ax + 3 * u, ay + 5 * u);
            }
        }
        using (var lp = new Pen(p.Line)) g.DrawLine(lp, pad, top0 - S(5), w - pad, top0 - S(5)); // 머리줄 밑줄은 한 줄로 이어서
    }
}
