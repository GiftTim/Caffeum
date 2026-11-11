using System.Drawing.Drawing2D;
using Microsoft.VisualBasic.FileIO;

namespace CoffeeViewer;

/// <summary>
/// 볼트 왼쪽 패널(옵시디언의 왼쪽 사이드바를 본뜬 것). 맨 위 아이콘 셋으로 [파일 · 검색 · 북마크] 화면을 오가고,
/// 파일 화면에는 [새 문서 · 새 폴더 · 정렬 · 모두 접기] 아이콘 줄과 볼트 이름, 실제 폴더 구조의 트리가 있다.
/// 트리에서는 이름 바꾸기(F2) · 삭제(Delete, 휴지통) · 새로 만들기 · 북마크를 한다.
/// 문서를 여는 일은 직접 하지 않고 OpenRequested로 알려서 VaultForm이 탭으로 연다.
/// </summary>
internal sealed class VaultSidebar : Panel
{
    /// <summary>트리에 보일 파일 형식. 읽을 수 있는 형식에 마크다운(.md)을 더한 것(.md는 txt처럼 열린다).</summary>
    private static readonly HashSet<string> TreeExtensions = new(LibraryStore.BookExtensions.Append(".md"), StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string Path, bool IsDir);

    private readonly string vaultPath;
    private readonly AppSettings settings = AppShared.Settings;
    private readonly SideIconButton filesTab, searchTab, bookmarkTab;
    private readonly Panel filesPanel, searchPanel, bookmarkPanel, iconBar;
    private readonly Label nameLabel;
    private readonly NoHScrollTreeView tree;
    private readonly TextBox searchBox;
    private readonly PathList searchList, bookmarkList;
    private readonly Label searchHint;
    private readonly ContextMenuStrip treeMenu;
    private readonly ToolStripMenuItem miNewNote, miNewFolder, miRename, miDelete, miBookmark, miReveal;
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 250 };
    private CancellationTokenSource? searchCts;
    private bool renaming;                 // F2 · 새로 만들기로 이름 고치기를 연 때만 허용(이미 고른 항목을 다시 누르는 것으로는 열지 않는다)
    private bool openAfterRename;          // 새 문서는 이름을 정한 뒤 바로 탭으로 연다
    private LibraryPalette palette = LibraryPalette.From(Themes.All[0]);

    /// <summary>문서를 탭으로 열어 달라는 요청(트리 클릭 · 검색 결과 · 북마크).</summary>
    public event Action<string>? OpenRequested;
    /// <summary>이름 바꾸기 · 삭제 직전에, 그 경로(폴더면 그 안 전부)를 열어 둔 탭을 닫아 달라는 요청.</summary>
    public event Action<string>? PathAboutToChange;

    public VaultSidebar(string vaultPath, string vaultName)
    {
        this.vaultPath = vaultPath;
        Dock = DockStyle.Fill;
        Padding = new Padding(8, 8, 4, 6);

        // ── 맨 위 아이콘 셋: 파일 · 검색 · 북마크 ──
        filesTab = new SideIconButton(SideIcon.Files, "파일") { Selected = true };
        searchTab = new SideIconButton(SideIcon.Search, "검색 (Ctrl+Shift+F)");
        bookmarkTab = new SideIconButton(SideIcon.Bookmark, "북마크");
        filesTab.Click += (_, _) => ShowPage(0);
        searchTab.Click += (_, _) => ShowPage(1);
        bookmarkTab.Click += (_, _) => ShowPage(2);
        iconBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        var refreshButton = new SideIconButton(SideIcon.Refresh, "새로 고침 (F5)");
        refreshButton.Click += (_, _) => ReloadAll();
        iconBar.Controls.AddRange(new Control[] { filesTab, searchTab, bookmarkTab, refreshButton });

        // ── 파일 화면: 볼트 이름 · 트리 (새 문서 · 새 폴더 · 정렬 · 모두 접기는 트리 우클릭 메뉴에) ──

        var sortMenu = new ToolStripMenuItem("정렬");
        foreach (var (key, label) in new[] { ("name", "파일 이름 (가 → 힣)"), ("name-desc", "파일 이름 (힣 → 가)"), ("modified", "수정한 날짜 (새것 → 오래된 것)"), ("modified-asc", "수정한 날짜 (오래된 것 → 새것)") })
        {
            string k = key;
            var item = new ToolStripMenuItem(label) { Tag = k };
            item.Click += (_, _) => { settings.VaultSort = k; settings.Save(); RefreshTree(); };
            sortMenu.DropDownItems.Add(item);
        }
        sortMenu.DropDownOpening += (_, _) => { foreach (ToolStripMenuItem i in sortMenu.DropDownItems) i.Checked = (string?)i.Tag == settings.VaultSort; };

        nameLabel = new Label { Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), AutoEllipsis = true };
        nameLabel.Text = vaultName;

        tree = new NoHScrollTreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            ShowLines = false,
            ShowRootLines = true,
            FullRowSelect = true,
            LabelEdit = true,
            Indent = LogicalToDeviceUnits(18),
        };
        tree.BeforeExpand += (_, e) => FillChildren(e.Node!);
        tree.BeforeLabelEdit += (_, e) => { if (!renaming) e.CancelEdit = true; };
        tree.AfterLabelEdit += OnAfterLabelEdit;
        tree.NodeMouseClick += (_, e) =>
        {
            tree.SelectedNode = e.Node;
            if (e.Button == MouseButtons.Left && e.Node?.Tag is Entry { IsDir: false } file) OpenRequested?.Invoke(file.Path);
        };
        tree.KeyDown += OnTreeKeyDown;

        // 끌어서 옮기기: 항목을 폴더(또는 볼트 이름 줄 = 맨 위)로 끌어다 놓는다.
        tree.AllowDrop = nameLabel.AllowDrop = true;
        tree.ItemDrag += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Item is TreeNode { Tag: Entry } n) { tree.SelectedNode = n; tree.DoDragDrop(n, DragDropEffects.Move); }
        };
        tree.DragOver += (_, e) =>
        {
            if (IsFileDrop(e.Data))
            {
                // 파일 탐색기에서 끌어온 파일 · 폴더: 복사
                e.Effect = DragDropEffects.Copy;
                var over = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
                SetDropHighlight(over?.Tag is Entry { IsDir: true } ? over : null);
                return;
            }
            var target = DropTarget(e.X, e.Y, e.Data);
            e.Effect = target != null ? DragDropEffects.Move : DragDropEffects.None;
            var hit = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
            SetDropHighlight(target != null && hit?.Tag is Entry { IsDir: true } ? hit : null);
        };
        tree.DragLeave += (_, _) => SetDropHighlight(null);
        tree.DragDrop += (_, e) =>
        {
            SetDropHighlight(null);
            if (IsFileDrop(e.Data)) { CopyInto((string[])e.Data!.GetData(DataFormats.FileDrop)!, FolderAt(e.X, e.Y)); return; }
            if (e.Data?.GetData(typeof(TreeNode)) is TreeNode { Tag: Entry src } && DropTarget(e.X, e.Y, e.Data) is { } dest) MoveEntry(src, dest);
        };
        nameLabel.DragOver += (_, e) => e.Effect = IsFileDrop(e.Data) ? DragDropEffects.Copy : e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
        nameLabel.DragDrop += (_, e) =>
        {
            if (IsFileDrop(e.Data)) { CopyInto((string[])e.Data!.GetData(DataFormats.FileDrop)!, vaultPath); return; }
            if (e.Data?.GetData(typeof(TreeNode)) is TreeNode { Tag: Entry src }) MoveEntry(src, vaultPath);
        };

        treeMenu = new ContextMenuStrip();
        miNewNote = new ToolStripMenuItem("새 문서", null, (_, _) => CreateNew(false));
        miNewFolder = new ToolStripMenuItem("새 폴더", null, (_, _) => CreateNew(true));
        miRename = new ToolStripMenuItem("이름 바꾸기", null, (_, _) => BeginRename()) { ShortcutKeyDisplayString = "F2" };
        miBookmark = new ToolStripMenuItem("북마크 추가", null, (_, _) => ToggleBookmark(SelectedEntry?.Path));
        miReveal = new ToolStripMenuItem("탐색기에서 보기", null, (_, _) => Reveal(SelectedEntry?.Path ?? vaultPath));
        miDelete = new ToolStripMenuItem("삭제 (휴지통)", null, (_, _) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" };
        var miCollapse = new ToolStripMenuItem("모두 접기", null, (_, _) => tree.CollapseAll());
        var miExpand = new ToolStripMenuItem("모두 펴기", null, (_, _) => ExpandAllFolders());
        var miRefresh = new ToolStripMenuItem("새로 고침", null, (_, _) => ReloadAll()) { ShortcutKeyDisplayString = "F5" };
        treeMenu.Items.AddRange(new ToolStripItem[] { miNewNote, miNewFolder, new ToolStripSeparator(), miRename, miBookmark, new ToolStripSeparator(), sortMenu, miExpand, miCollapse, miRefresh, new ToolStripSeparator(), miReveal, miDelete });
        treeMenu.Opening += (_, _) =>
        {
            var entry = SelectedEntry;
            miRename.Enabled = miDelete.Enabled = entry != null;
            miBookmark.Visible = entry is { IsDir: false };
            if (entry is { IsDir: false }) miBookmark.Text = IsBookmarked(entry.Path) ? "북마크 해제" : "북마크 추가";
        };
        tree.ContextMenuStrip = treeMenu;
        // 클릭한 자리의 항목을 고른다(오른쪽 클릭도 — 메뉴가 열리기 전에). 빈 곳을 누르면 선택을 풀어서, 새로 만들기가 볼트 맨 위(루트)에 되게 한다.
        tree.MouseDown += (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            var hit = tree.GetNodeAt(e.Location);
            if (hit != tree.SelectedNode) tree.SelectedNode = hit;
        };
        // 볼트 이름 줄도 "맨 위 폴더" 자리 — 누르거나 우클릭하면 선택을 풀고 같은 메뉴를 쓴다.
        nameLabel.MouseDown += (_, _) => tree.SelectedNode = null;
        nameLabel.ContextMenuStrip = treeMenu;

        filesPanel = new Panel { Dock = DockStyle.Fill };
        filesPanel.Controls.AddRange(new Control[] { tree, nameLabel });

        // ── 검색 화면: 입력칸 · 결과 목록(파일 이름에 들어 있는 글자로 찾는다) ──
        searchBox = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "파일 이름 검색" };
        searchHint = new Label { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0) };
        searchList = new PathList(vaultPath) { Dock = DockStyle.Fill };
        searchList.PathChosen += p => OpenRequested?.Invoke(p);
        searchPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        searchPanel.Controls.AddRange(new Control[] { searchList, searchHint, searchBox });
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); _ = RunSearchAsync(); };
        searchBox.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && searchList.Items.Count > 0) { searchList.SelectedIndex = Math.Max(0, searchList.SelectedIndex); searchList.ChooseSelected(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down && searchList.Items.Count > 0) { searchList.Focus(); if (searchList.SelectedIndex < 0) searchList.SelectedIndex = 0; e.Handled = true; }
        };

        // ── 북마크 화면 ──
        bookmarkList = new PathList(vaultPath) { Dock = DockStyle.Fill, EmptyText = "북마크한 문서가 없어요.\n트리에서 문서를 우클릭해 추가하세요." };
        bookmarkList.PathChosen += p => OpenRequested?.Invoke(p);
        bookmarkList.RemoveRequested += p => ToggleBookmark(p);
        bookmarkPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        bookmarkPanel.Controls.Add(bookmarkList);

        // Dock는 나중에 넣은 것부터 자리를 잡는다 — Fill을 먼저, 맨 위에 올 것을 나중에.
        Controls.AddRange(new Control[] { filesPanel, searchPanel, bookmarkPanel, iconBar });
        Disposed += (_, _) => { searchTimer.Dispose(); searchCts?.Cancel(); };
        BuildRoot();
    }

    private Entry? SelectedEntry => tree.SelectedNode?.Tag as Entry;

    // ───────────────────────── 화면 전환 ─────────────────────────

    public void ShowPage(int index)
    {
        filesTab.Selected = index == 0;
        searchTab.Selected = index == 1;
        bookmarkTab.Selected = index == 2;
        filesPanel.Visible = index == 0;
        searchPanel.Visible = index == 1;
        bookmarkPanel.Visible = index == 2;
        if (index == 1) { searchBox.Focus(); searchBox.SelectAll(); }
        else if (index == 2) FillBookmarks();
        else tree.Focus();
    }

    // ───────────────────────── 트리 ─────────────────────────

    private void BuildRoot()
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        FillInto(tree.Nodes, vaultPath);
        tree.EndUpdate();
    }

    /// <summary>새로 고침: 지금 펼쳐 둔 폴더와 고른 항목을 기억했다가 같은 모습으로 다시 만든다. (old → new)는 방금 이름을 바꾼 경로.</summary>
    public void RefreshTree((string Old, string New)? renamed = null)
    {
        string Map(string p)
        {
            if (renamed is not { } r) return p;
            if (string.Equals(p, r.Old, StringComparison.OrdinalIgnoreCase)) return r.New;
            return p.StartsWith(r.Old + "\\", StringComparison.OrdinalIgnoreCase) ? r.New + p[r.Old.Length..] : p;
        }
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(TreeNode n)
        {
            if (n.IsExpanded && n.Tag is Entry e) expanded.Add(Map(e.Path));
            foreach (TreeNode c in n.Nodes) Collect(c);
        }
        foreach (TreeNode n in tree.Nodes) Collect(n);
        string? selected = SelectedEntry is { } se ? Map(se.Path) : null;

        tree.BeginUpdate();
        tree.Nodes.Clear();
        FillInto(tree.Nodes, vaultPath);
        void Restore(TreeNode n)
        {
            if (n.Tag is not Entry { IsDir: true } e || !expanded.Contains(e.Path)) return;
            n.Expand(); // BeforeExpand가 자식을 채운다
            foreach (TreeNode c in n.Nodes) Restore(c);
        }
        foreach (TreeNode n in tree.Nodes) Restore(n);
        if (selected != null) SelectPath(selected);
        tree.EndUpdate();
    }

    private TreeNode? SelectPath(string path)
    {
        TreeNode? Find(TreeNode n)
        {
            if (n.Tag is Entry e && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) return n;
            foreach (TreeNode c in n.Nodes) if (Find(c) is { } hit) return hit;
            return null;
        }
        foreach (TreeNode n in tree.Nodes)
            if (Find(n) is { } hit) { tree.SelectedNode = hit; hit.EnsureVisible(); return hit; }
        return null;
    }

    /// <summary>모두 펴기: 폴더 아래 폴더까지 전부 연다. 자식은 펼칠 때 채워지므로(BeforeExpand) 한 단계씩 직접 편다. 폴더가 아주 많은 볼트는 멈춘 듯 보이지 않게 5000개에서 그친다.</summary>
    private void ExpandAllFolders()
    {
        int opened = 0;
        const int limit = 5000;
        tree.BeginUpdate();
        try
        {
            void Open(TreeNode n)
            {
                if (opened >= limit) return;
                if (n.Nodes.Count == 0 && !n.IsExpanded) return;
                n.Expand(); // BeforeExpand가 자식을 채운다
                opened++;
                foreach (TreeNode c in n.Nodes) Open(c);
            }
            foreach (TreeNode root in tree.Nodes) Open(root);
        }
        finally { tree.EndUpdate(); }
    }

    private void FillChildren(TreeNode node)
    {
        if (node.Tag is not Entry { IsDir: true } dir) return;
        if (node.Nodes.Count == 1 && node.Nodes[0].Tag == null) node.Nodes.Clear(); // 자리표시용 빈 자식
        else return; // 이미 채웠다
        FillInto(node.Nodes, dir.Path);
    }

    /// <summary>실제 폴더의 내용을 노드로 채운다. 폴더가 먼저, 그 안에서 정렬 설정대로. 숨김 · 시스템 항목과 .으로 시작하는 것(.obsidian 등)은 뺀다.</summary>
    private void FillInto(TreeNodeCollection nodes, string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            string sort = settings.VaultSort;
            IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> name, Func<T, DateTime> time) => sort switch
            {
                "name-desc" => items.OrderByDescending(name, NaturalStringComparer.Instance),
                "modified" => items.OrderByDescending(time),
                "modified-asc" => items.OrderBy(time),
                _ => items.OrderBy(name, NaturalStringComparer.Instance),
            };
            foreach (var d in Order(info.EnumerateDirectories().Where(IsShown), d => d.Name, d => d.LastWriteTime))
            {
                var child = new TreeNode(d.Name) { Tag = new Entry(d.FullName, true) };
                child.Nodes.Add(new TreeNode()); // 폴더는 늘 [>]를 단다(펼칠 때 채운다 — 비어 있으면 화살표가 사라진다)
                nodes.Add(child);
            }
            foreach (var f in Order(info.EnumerateFiles().Where(f => IsShown(f) && TreeExtensions.Contains(f.Extension)), f => f.Name, f => f.LastWriteTime))
                nodes.Add(new TreeNode(f.Name) { Tag = new Entry(f.FullName, false) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            nodes.Add(new TreeNode("(열 수 없는 폴더)") { ForeColor = palette.Dim });
        }
    }

    private static bool IsShown(FileSystemInfo i) =>
        !i.Name.StartsWith('.') && (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;


    // ───────────────────────── 새로 만들기 · 이름 바꾸기 · 삭제 ─────────────────────────

    /// <summary>새로 만들 위치: 고른 폴더, 문서를 골랐으면 그 문서가 든 폴더, 아무것도 없으면 볼트 맨 위.</summary>
    private string TargetFolder() => SelectedEntry switch
    {
        { IsDir: true } e => e.Path,
        { } e => Path.GetDirectoryName(e.Path) ?? vaultPath,
        _ => vaultPath,
    };

    private static string UniquePath(string folder, string baseName, string ext)
    {
        string path = Path.Combine(folder, baseName + ext);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(folder, $"{baseName} {i}{ext}");
        return path;
    }

    private void CreateNew(bool isFolder)
    {
        ShowPage(0);
        string folder = TargetFolder();
        string path;
        try
        {
            if (isFolder) { path = UniquePath(folder, "새 폴더", ""); Directory.CreateDirectory(path); }
            else { path = UniquePath(folder, "제목 없음", ".txt"); File.WriteAllBytes(path, Array.Empty<byte>()); }
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), $"만들지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RefreshAfterCreate(folder, path);
        openAfterRename = !isFolder;
        BeginRename();
    }

    /// <summary>새로 만든 항목이 보이도록 그 폴더를 펼쳐서 다시 읽고 그 항목을 고른다.</summary>
    private void RefreshAfterCreate(string folder, string path)
    {
        if (!string.Equals(folder, vaultPath, StringComparison.OrdinalIgnoreCase))
        {
            // 폴더 노드의 자식을 새로 채워 정렬 위치에 새 항목이 들어가게 한다.
            if (SelectPath(folder) is { } node && node.Tag is Entry { IsDir: true })
            {
                node.Nodes.Clear();
                FillInto(node.Nodes, folder);
                node.Expand();
            }
            else RefreshTree();
        }
        else RefreshTree();
        SelectPath(path);
    }

    private void BeginRename()
    {
        if (tree.SelectedNode == null) return;
        renaming = true;
        tree.SelectedNode.BeginEdit();
    }

    private void OnAfterLabelEdit(object? sender, NodeLabelEditEventArgs e)
    {
        renaming = false;
        bool openIt = openAfterRename;
        openAfterRename = false;
        if (e.Node?.Tag is not Entry entry) { e.CancelEdit = true; return; }
        string? label = e.Label?.Trim();
        // 그대로 두고 Enter/Esc: 새 문서는 기본 이름 그대로 연다.
        if (string.IsNullOrEmpty(label) || label == e.Node.Text)
        {
            e.CancelEdit = true;
            if (openIt && !entry.IsDir) BeginInvoke(() => OpenRequested?.Invoke(entry.Path));
            return;
        }
        if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || label is "." or "..")
        {
            e.CancelEdit = true;
            MessageBox.Show(FindForm(), "이름에 쓸 수 없는 글자(\\ / : * ? \" < > |)가 들어 있어요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 파일은 확장자를 빼고 적었다면 원래 확장자를 붙여 준다(메모 → 메모.txt).
        if (!entry.IsDir && !TreeExtensions.Contains(Path.GetExtension(label))) label += Path.GetExtension(entry.Path);
        string newPath = Path.Combine(Path.GetDirectoryName(entry.Path)!, label);
        if (!string.Equals(newPath, entry.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            e.CancelEdit = true;
            MessageBox.Show(FindForm(), $"같은 이름이 이미 있어요: {label}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        e.CancelEdit = true; // 글자는 아래에서 트리를 다시 만들며 반영한다
        try
        {
            PathAboutToChange?.Invoke(entry.Path);
            if (entry.IsDir) Directory.Move(entry.Path, newPath); else File.Move(entry.Path, newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), $"이름을 바꾸지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RenameBookmarks(entry.Path, newPath);
        BeginInvoke(() =>
        {
            RefreshTree((entry.Path, newPath));
            if (openIt && !entry.IsDir) OpenRequested?.Invoke(newPath);
        });
    }

    private static bool IsFileDrop(IDataObject? data) => data?.GetDataPresent(DataFormats.FileDrop) == true;

    /// <summary>끌어다 놓은 자리의 폴더: 폴더 위면 그 폴더, 문서 위면 그 문서가 든 폴더, 빈 곳이면 볼트 맨 위.</summary>
    private string FolderAt(int screenX, int screenY)
    {
        var hit = tree.GetNodeAt(tree.PointToClient(new Point(screenX, screenY)));
        return hit?.Tag switch
        {
            Entry { IsDir: true } d => d.Path,
            Entry f => Path.GetDirectoryName(f.Path) ?? vaultPath,
            _ => vaultPath,
        };
    }

    /// <summary>파일 탐색기에서 끌어온 파일 · 폴더(안쪽 전부)를 이 폴더에 복사한다. 같은 이름이 있으면 "이름 2"처럼 번호를 붙인다. 원본은 그대로 둔다.</summary>
    private void CopyInto(string[] sources, string destFolder)
    {
        string? first = null;
        var failed = new List<string>();
        foreach (var src in sources)
        {
            try
            {
                string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;
                if (Directory.Exists(src))
                {
                    // 폴더를 자기 자신 안으로 복사하면 끝없이 이어지니 막는다.
                    if (string.Equals(src.TrimEnd(Path.DirectorySeparatorChar), destFolder, StringComparison.OrdinalIgnoreCase)
                        || destFolder.StartsWith(src.TrimEnd(Path.DirectorySeparatorChar) + "\\", StringComparison.OrdinalIgnoreCase)) { failed.Add($"{name} (자기 자신 안으로는 복사할 수 없어요)"); continue; }
                    string target = UniquePath(destFolder, name, "");
                    CopyDirectory(src, target);
                    first ??= target;
                }
                else if (File.Exists(src))
                {
                    string target = UniquePath(destFolder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
                    File.Copy(src, target);
                    first ??= target;
                }
            }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(src)} ({ex.Message})"); }
        }
        if (first != null) RefreshAfterCreate(destFolder, first);
        if (failed.Count > 0)
            MessageBox.Show(FindForm(), "복사하지 못한 항목이 있어요:\n" + string.Join("\n", failed), AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private TreeNode? dropHot;
    private void SetDropHighlight(TreeNode? node)
    {
        if (dropHot == node) return;
        if (dropHot != null) { dropHot.BackColor = Color.Empty; dropHot.ForeColor = Color.Empty; }
        dropHot = node;
        if (node != null) { node.BackColor = palette.Dim; node.ForeColor = palette.Side; }
    }

    /// <summary>끌어다 놓을 폴더 경로. 폴더 위면 그 폴더, 문서 위면 그 문서가 든 폴더, 빈 곳이면 볼트 맨 위. 자기 자신/자기 아래/이미 그 폴더면 null.</summary>
    private string? DropTarget(int screenX, int screenY, IDataObject? data)
    {
        if (data?.GetData(typeof(TreeNode)) is not TreeNode { Tag: Entry src }) return null;
        var hit = tree.GetNodeAt(tree.PointToClient(new Point(screenX, screenY)));
        string dest = hit?.Tag switch
        {
            Entry { IsDir: true } d => d.Path,
            Entry f => Path.GetDirectoryName(f.Path) ?? vaultPath,
            _ => vaultPath,
        };
        if (string.Equals(Path.GetDirectoryName(src.Path), dest, StringComparison.OrdinalIgnoreCase)) return null;
        if (src.IsDir && (string.Equals(src.Path, dest, StringComparison.OrdinalIgnoreCase) || dest.StartsWith(src.Path + "\\", StringComparison.OrdinalIgnoreCase))) return null;
        return dest;
    }

    private void MoveEntry(Entry src, string destFolder)
    {
        string newPath = Path.Combine(destFolder, Path.GetFileName(src.Path));
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            MessageBox.Show(FindForm(), $"그 폴더에 같은 이름이 이미 있어요: {Path.GetFileName(src.Path)}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            PathAboutToChange?.Invoke(src.Path);
            if (src.IsDir) Directory.Move(src.Path, newPath); else File.Move(src.Path, newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), $"옮기지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RenameBookmarks(src.Path, newPath);
        BeginInvoke(() => RefreshTree((src.Path, newPath)));
    }

    private void DeleteSelected()
    {
        if (SelectedEntry is not { } entry) return;
        string name = Path.GetFileName(entry.Path);
        string what = entry.IsDir ? $"폴더 '{name}'과(와) 그 안의 모든 것을" : $"'{name}'을(를)";
        if (MessageBox.Show(FindForm(), $"{what} 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            PathAboutToChange?.Invoke(entry.Path);
            if (entry.IsDir) FileSystem.DeleteDirectory(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else FileSystem.DeleteFile(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show(FindForm(), $"삭제하지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RemoveBookmarksUnder(entry.Path);
        RefreshTree();
    }

    private static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* 탐색기를 못 열어도 앱은 그대로 */ }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (tree.SelectedNode?.IsEditing == true) return;
        if (e.KeyCode == Keys.Enter && SelectedEntry is { IsDir: false } file) { OpenRequested?.Invoke(file.Path); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.F2) { BeginRename(); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { ReloadAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
    }

    /// <summary>새로 고침: 탐색기 등 밖에서 바뀐 폴더 · 파일을 다시 읽는다(펼친 폴더와 고른 항목은 그대로). 검색 · 북마크 화면도 같이 갱신한다.</summary>
    public void ReloadAll()
    {
        RefreshTree();
        FillBookmarks();
        if (!string.IsNullOrWhiteSpace(searchBox.Text)) _ = RunSearchAsync();
    }

    // ───────────────────────── 검색 ─────────────────────────

    /// <summary>검색칸에 띄어쓰기로 여러 단어를 넣으면 모든 단어가 파일 이름에 든 문서를 찾는다.</summary>
    private async Task RunSearchAsync()
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var words = searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { searchList.SetPaths(Array.Empty<string>()); searchHint.Text = ""; return; }
        searchHint.Text = "찾는 중…";
        const int Limit = 300;
        List<string> found;
        try { found = await Task.Run(() => Search(words, Limit, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        searchList.SetPaths(found);
        searchHint.Text = found.Count == 0 ? "결과가 없어요." : found.Count >= Limit ? $"{Limit}개 이상 — 단어를 더 넣어 보세요." : $"{found.Count}개";
    }

    private List<string> Search(string[] words, int limit, CancellationToken ct)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(vaultPath);
        while (stack.Count > 0 && result.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(stack.Pop());
            try
            {
                foreach (var d in info.EnumerateDirectories().Where(IsShown)) stack.Push(d.FullName);
                foreach (var f in info.EnumerateFiles().Where(f => IsShown(f) && TreeExtensions.Contains(f.Extension)))
                    if (words.All(w => f.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { result.Add(f.FullName); if (result.Count >= limit) break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 못 여는 폴더는 건너뛴다 */ }
        }
        result.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        return result;
    }

    // ───────────────────────── 북마크 ─────────────────────────

    private List<string> BookmarkList(bool create)
    {
        var key = settings.Bookmarks.Keys.FirstOrDefault(k => string.Equals(k, vaultPath, StringComparison.OrdinalIgnoreCase));
        if (key == null)
        {
            if (!create) return new List<string>();
            key = vaultPath;
            settings.Bookmarks[key] = new List<string>();
        }
        return settings.Bookmarks[key];
    }

    private bool IsBookmarked(string path) => BookmarkList(false).Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private void ToggleBookmark(string? path)
    {
        if (path == null) return;
        var list = BookmarkList(true);
        int i = list.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list.RemoveAt(i); else list.Add(path);
        settings.Save();
        if (bookmarkPanel.Visible) FillBookmarks();
    }

    private void RenameBookmarks(string oldPath, string newPath)
    {
        var list = BookmarkList(false);
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], oldPath, StringComparison.OrdinalIgnoreCase)) { list[i] = newPath; changed = true; }
            else if (list[i].StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)) { list[i] = newPath + list[i][oldPath.Length..]; changed = true; }
        }
        if (changed) settings.Save();
    }

    private void RemoveBookmarksUnder(string path)
    {
        var list = BookmarkList(false);
        if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) > 0) settings.Save();
    }

    private void FillBookmarks() => bookmarkList.SetPaths(BookmarkList(false).Where(File.Exists).ToList());

    // ───────────────────────── 테마 · 글꼴 ─────────────────────────

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Side;
        ForeColor = p.Fore;
        foreach (var c in new Control[] { iconBar, filesPanel, searchPanel, bookmarkPanel }) { c.BackColor = p.Side; c.ForeColor = p.Fore; }
        foreach (var b in new[] { filesTab, searchTab, bookmarkTab }) b.SetPalette(p);
        nameLabel.BackColor = p.Side;
        nameLabel.ForeColor = p.Fore;
        tree.BackColor = p.Side;
        tree.ForeColor = p.Fore;
        searchBox.BackColor = p.Card;
        searchBox.ForeColor = p.Fore;
        searchHint.BackColor = p.Side;
        searchHint.ForeColor = p.Dim;
        searchList.SetPalette(p);
        bookmarkList.SetPalette(p);
        tree.Invalidate();
    }

    public void ApplyFont(Font treeFont)
    {
        tree.Font = treeFont;
        tree.ItemHeight = Math.Clamp(treeFont.Height + LogicalToDeviceUnits(12), 16, 256);
        nameLabel.Font = new Font(treeFont, FontStyle.Bold);
        searchBox.Font = treeFont;
        searchHint.Font = new Font(treeFont.FontFamily, Math.Max(8f, treeFont.SizeInPoints - 1));
        searchList.Font = treeFont;
        bookmarkList.Font = treeFont;
    }

    /// <summary>Ctrl+Shift+F 같은 단축키에서 검색 화면을 연다.</summary>
    public void FocusSearch() => ShowPage(1);
}

/// <summary>사이드바의 작은 아이콘 단추(직접 그린다). 선택된 화면은 배경색으로, 마우스를 올리면 살짝 밝게.</summary>
internal enum SideIcon { Files, Search, Bookmark, NewNote, NewFolder, Sort, Collapse, Refresh }

internal sealed class SideIconButton : Control
{
    private readonly SideIcon icon;
    /// <summary>새로 고침 버튼에 그리는 Img\refresh.png(exe에 포함). 못 읽으면 null → 예전 선 아이콘.</summary>
    private static readonly Bitmap? RefreshImage = LoadRefreshImage();
    private static Bitmap? LoadRefreshImage()
    {
        try
        {
            using var s = typeof(SideIconButton).Assembly.GetManifestResourceStream("refresh.png");
            return s == null ? null : new Bitmap(s);
        }
        catch { return null; }
    }
    private bool hover, down;
    private LibraryPalette? palette;
    private readonly ToolTip tip = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    private bool selected;

    public SideIconButton(SideIcon icon, string tooltip)
    {
        this.icon = icon;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 4, 0);
        tip.SetToolTip(this, tooltip);
        TabStop = false;
        Disposed += (_, _) => tip.Dispose();
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    protected override Size DefaultSize => new(34, 30);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Size = new Size((int)(34 * DeviceDpi / 96.0), (int)(30 * DeviceDpi / 96.0)); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = palette ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(Parent?.BackColor ?? p.Side);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (selected || hover)
        {
            using var path = LibraryPalette.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 6 * DeviceDpi / 96f);
            using var b = new SolidBrush(selected ? p.AccentSoft : (down ? p.CardHover : p.Card));
            g.FillPath(b, path);
        }
        float size = 16f * DeviceDpi / 96f;
        float u = size / 16f;
        g.TranslateTransform((Width - size) / 2f, (Height - size) / 2f);
        g.ScaleTransform(u, u);
        using var pen = new Pen(selected ? p.Fore : (hover ? p.Fore : p.Dim), 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        Draw(g, pen);
    }

    // 16×16 칸에 그린 선 아이콘들.
    private void Draw(Graphics g, Pen pen)
    {
        PointF[] folder = { new(1.5f, 3.5f), new(6f, 3.5f), new(7.5f, 5f), new(14.5f, 5f), new(14.5f, 13f), new(1.5f, 13f) };
        switch (icon)
        {
            case SideIcon.Files:
                g.DrawPolygon(pen, folder);
                break;
            case SideIcon.Search:
                g.DrawEllipse(pen, 2f, 2f, 8.5f, 8.5f);
                g.DrawLine(pen, 9.5f, 9.5f, 14f, 14f);
                break;
            case SideIcon.Bookmark:
                g.DrawPolygon(pen, new PointF[] { new(4f, 2f), new(12f, 2f), new(12f, 14f), new(8f, 10.5f), new(4f, 14f) });
                break;
            case SideIcon.NewNote:
                g.DrawLines(pen, new PointF[] { new(8.5f, 2.5f), new(3f, 2.5f), new(3f, 13.5f), new(13f, 13.5f), new(13f, 8f) });
                g.DrawLine(pen, 7.5f, 9f, 13.5f, 3f);
                g.DrawLine(pen, 7.5f, 9f, 7f, 10.8f);
                break;
            case SideIcon.NewFolder:
                g.DrawPolygon(pen, folder);
                g.DrawLine(pen, 8f, 7.5f, 8f, 11.5f);
                g.DrawLine(pen, 6f, 9.5f, 10f, 9.5f);
                break;
            case SideIcon.Sort:
                g.DrawLine(pen, 5f, 13f, 5f, 3f);
                g.DrawLines(pen, new PointF[] { new(2.5f, 5.5f), new(5f, 3f), new(7.5f, 5.5f) });
                g.DrawLine(pen, 11f, 3f, 11f, 13f);
                g.DrawLines(pen, new PointF[] { new(8.5f, 10.5f), new(11f, 13f), new(13.5f, 10.5f) });
                break;
            case SideIcon.Refresh:
                if (RefreshImage != null)
                {
                    // Img\refresh.png(검정 바탕 투명)를 선 색으로 물들여 16×16 칸 가운데에 그린다.
                    var c = pen.Color;
                    var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
                    {
                        new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 },
                    });
                    using var ia = new System.Drawing.Imaging.ImageAttributes();
                    ia.SetColorMatrix(cm);
                    float w = 16f * RefreshImage.Width / RefreshImage.Height;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(RefreshImage, new Rectangle((int)Math.Round((16f - w) / 2f), 0, (int)Math.Round(w), 16),
                        0, 0, RefreshImage.Width, RefreshImage.Height, GraphicsUnit.Pixel, ia);
                    break;
                }
                g.DrawArc(pen, 2.5f, 2.5f, 11f, 11f, -50f, 280f);
                g.DrawLines(pen, new PointF[] { new(1.2f, 3.2f), new(4.4f, 3.6f), new(4.8f, 0.6f) });
                break;
            case SideIcon.Collapse:
                g.DrawLines(pen, new PointF[] { new(4f, 2.5f), new(8f, 6f), new(12f, 2.5f) });
                g.DrawLines(pen, new PointF[] { new(4f, 13.5f), new(8f, 10f), new(12f, 13.5f) });
                break;
        }
    }
}

/// <summary>경로 목록(검색 결과 · 북마크). 한 줄에 파일 이름, 그 밑에 흐리게 볼트 안의 폴더 위치. 누르면 PathChosen.</summary>
internal sealed class PathList : ListBox
{
    private readonly string vaultPath;
    private LibraryPalette palette = LibraryPalette.From(Themes.All[0]);
    private readonly ContextMenuStrip menu = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";
    public event Action<string>? PathChosen;
    /// <summary>우클릭 > 북마크 해제 (북마크 화면에서만 쓴다).</summary>
    public event Action<string>? RemoveRequested;

    public PathList(string vaultPath)
    {
        this.vaultPath = vaultPath;
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        var remove = new ToolStripMenuItem("북마크 해제", null, (_, _) => { if (SelectedItem is string p) RemoveRequested?.Invoke(p); });
        menu.Items.Add(remove);
        menu.Opening += (_, e) => e.Cancel = RemoveRequested == null || SelectedItem == null;
        ContextMenuStrip = menu;
        MouseDown += (_, e) =>
        {
            int i = IndexFromPoint(e.Location);
            if (i >= 0) SelectedIndex = i;
        };
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && IndexFromPoint(e.Location) >= 0) ChooseSelected();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ChooseSelected(); e.Handled = true; } };
        FontChanged += (_, _) => ItemHeight = Font.Height * 2 + 10;
        ItemHeight = Font.Height * 2 + 10;
    }

    public void ChooseSelected() { if (SelectedItem is string p) PathChosen?.Invoke(p); }

    public void SetPaths(IReadOnlyList<string> paths)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var p in paths) Items.Add(p);
        EndUpdate();
        Invalidate();
    }

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Side; ForeColor = p.Fore; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Items.Count == 0 && EmptyText.Length > 0)
            TextRenderer.DrawText(e.Graphics, EmptyText, Font, ClientRectangle with { Y = 12, Height = ClientSize.Height - 12 }, palette.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not string path) return;
        var g = e.Graphics;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? palette.AccentSoft : palette.Side)) g.FillRectangle(b, e.Bounds);
        string dir = Path.GetDirectoryName(path) ?? "";
        string rel = dir.Length > vaultPath.Length ? dir[(vaultPath.Length + 1)..] : "";
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var nameRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 12, Font.Height);
        var dirRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4 + Font.Height, e.Bounds.Width - 12, Font.Height);
        TextRenderer.DrawText(g, Path.GetFileName(path), Font, nameRect, palette.Fore, flags);
        if (rel.Length > 0) TextRenderer.DrawText(g, rel, Font, dirRect, palette.Dim, flags);
    }
}
