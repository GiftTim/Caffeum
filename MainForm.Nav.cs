namespace CoffeeViewer;

/// <summary>
/// 네비게이션 바(보기 > 네비게이션 바 · 툴바의 목차 아이콘) — 읽기 화면 아래 막대(NavigationBar)를 MainForm에 잇는 부분.
/// 막대는 contentHost 안에 들어간다:
/// - 고정: Dock=Bottom으로 본문 아래 자리를 차지하고 늘 보인다.
/// - 고정 안 함: 본문 위에 겹쳐 뜨는 오버레이 — 마우스를 본문 아래쪽에 가져가면 나타나고, 떠나면 잠시 뒤 숨는다.
///   WebView2(epub · pdf)는 마우스 이벤트를 WinForms로 넘겨주지 않아서, 커서 위치를 타이머로 직접 살핀다.
/// </summary>
public partial class MainForm
{
    private NavigationBar navBar = null!;
    private ToolStripMenuItem navMenuItem = null!;
    private readonly System.Windows.Forms.Timer navTimer = new() { Interval = 150 };
    private DateTime navLastNear;
    private bool navHasContent; // 파일을 하나라도 열었는지(열기 전엔 막대를 안 띄운다)
    private ContentKind navKind = ContentKind.Txt; // 마지막으로 SetContentMode가 정한 종류(OnBookOpened에서 다시 맞출 때 쓴다)
    private int navLastTxtValue = -1;
    private int navTxtWebTick;
    private bool pageNavVisible; // UpdatePageNavVisibility가 마지막으로 받은 값 — 탐색을 껐다 켤 때 예전 표시줄을 되살리려고

    private const int NavTxtScale = 1000; // txt는 글 위치를 1000칸으로 나눠 막대에 싣는다

    private static readonly string[] NavFileExtensions = { ".txt", ".epub", ".pdf", ".zip", ".hwp", ".hwpx" };

    private void InitNavBar(ToolStripMenuItem viewMenu, int menuIndex)
    {
        navBar = new NavigationBar { Visible = false, Pinned = settings.NavBarPinned };
        navBar.FileListRequested += ShowNavFileList;
        navBar.ItemListRequested += ShowNavItemList;
        navBar.PrevPageRequested += () => TurnPage(-1);
        navBar.NextPageRequested += () => TurnPage(1);
        navBar.SeekRequested += OnNavSeek;
        navBar.PinChanged += pinned =>
        {
            settings.NavBarPinned = pinned;
            settings.Save();
            UpdateNavBarLayout();
        };
        contentHost.Controls.Add(navBar);
        contentHost.Resize += (_, _) => { if (!navBar.Pinned) PlaceFloatingNavBar(); };

        navMenuItem = new ToolStripMenuItem("네비게이션 바", null, (_, _) =>
        {
            settings.NavBarEnabled = navMenuItem!.Checked;
            // 네비게이션 바를 켜면 늘 고정된 채로 시작한다(바로 눈에 보이게). 고정을 풀고 싶으면 막대의 📌을 누른다.
            if (navMenuItem.Checked) { settings.NavBarPinned = true; navBar.Pinned = true; }
            settings.Save();
            UpdateNavBarLayout();
            UpdatePageNavVisibility(pageNavVisible); // 네비게이션 바를 끄면 예전 위치 표시줄이 다시 나온다
            UpdateToolbar(); // 툴바의 목차 아이콘(= 네비게이션 바 열고 닫기) 눌림 표시
        }) { CheckOnClick = true, Checked = settings.NavBarEnabled, ToolTipText = "아래쪽 네비게이션 바(파일 · 챕터 · 위치 막대) 열기/닫기" };
        viewMenu.DropDownItems.Insert(menuIndex, navMenuItem);

        navTimer.Tick += (_, _) => OnNavTimer();
        navTimer.Start();
        FormClosed += (_, _) => navTimer.Dispose();
    }

    private bool NavBarOn => settings.NavBarEnabled && navHasContent;

    /// <summary>고정 여부 · 켜짐 여부가 바뀌었을 때 막대를 알맞은 자리에 놓는다.</summary>
    private void UpdateNavBarLayout()
    {
        if (!NavBarOn)
        {
            navBar.Visible = false;
            return;
        }
        if (navBar.Pinned)
        {
            navBar.Dock = DockStyle.Bottom;
            navBar.SendToBack(); // 도킹은 뒤쪽(나중 번호) 컨트롤부터 자리를 잡으므로, 맨 뒤로 보내야 본문보다 먼저 아래 자리를 차지한다
            navBar.Visible = true;
        }
        else
        {
            navBar.Dock = DockStyle.None;
            PlaceFloatingNavBar();
            navBar.Visible = false; // 마우스가 아래쪽에 오면 타이머가 띄운다
        }
        RepositionOverlays();
    }

    private void PlaceFloatingNavBar()
    {
        if (navBar.Dock != DockStyle.None) return;
        navBar.SetBounds(0, contentHost.ClientSize.Height - navBar.Height, contentHost.ClientSize.Width, navBar.Height);
        if (navBar.Visible) navBar.BringToFront();
    }

    private void OnNavTimer()
    {
        if (!NavBarOn) return;
        if (txtWeb) { if (navBar.Visible && ++navTxtWebTick % 4 == 0) QueryTxtTop(); } // 웹 화면 스크롤도 알림이 없어서 가끔 읽어 온다
        else if (!editorHost.Visible || pageMode) navLastTxtValue = -1;
        else if (navBar.Visible) RefreshNavPosition(); // txt 스크롤은 위치가 바뀌었다는 알림이 따로 없어서 여기서 살핀다

        if (navBar.Pinned) return;

        // 고정 안 함: 이 창이 앞에 있고 마우스가 본문 아래쪽(막대 두 개 높이)에 있으면 띄운다.
        var p = contentHost.PointToClient(Cursor.Position);
        bool inside = contentHost.ClientRectangle.Contains(p) && (ContainsFocus || IsHostActive);
        bool near = inside && p.Y >= contentHost.ClientSize.Height - navBar.Height * 2;
        if (near || navBar.IsBusy) navLastNear = DateTime.Now;

        if (near && !navBar.Visible)
        {
            PlaceFloatingNavBar();
            RefreshNavPosition();
            navBar.Visible = true;
            navBar.BringToFront();
        }
        else if (navBar.Visible && !near && !navBar.IsBusy && (DateTime.Now - navLastNear).TotalMilliseconds > 700)
        {
            navBar.Visible = false;
        }
    }

    /// <summary>새 파일을 열거나 보는 종류(txt · epub · 만화 · pdf)가 바뀌었을 때 버튼 이름과 쓸 수 있는 것을 맞춘다.</summary>
    private void ConfigureNavBar(ContentKind kind)
    {
        navHasContent = true;
        bool hasFiles = NavFolderFiles(out _) != null;
        switch (kind)
        {
            case ContentKind.Image:
                navBar.Configure("이미지 선택", listEnabled: true, filesEnabled: hasFiles, seekEnabled: true, liveSeek: true);
                break;
            case ContentKind.Epub:
                navBar.Configure("챕터", listEnabled: true, filesEnabled: hasFiles, seekEnabled: true, liveSeek: false);
                break;
            case ContentKind.Pdf: // pdf는 뷰어가 자체 툴바로 페이지를 넘기고, 우리가 뽑을 목차도 없다
                navBar.Configure("챕터", listEnabled: false, filesEnabled: hasFiles, seekEnabled: false, liveSeek: false, pagesEnabled: false);
                break;
            default:
                navBar.Configure("챕터", listEnabled: true, filesEnabled: hasFiles, seekEnabled: true, liveSeek: false);
                break;
        }
        navLastTxtValue = -1;
        UpdateNavBarLayout();
        RefreshNavPosition();
    }

    /// <summary>위치 막대와 오른쪽 숫자를 지금 보는 곳에 맞춘다. 페이지 · 챕터를 옮길 때마다 부른다.</summary>
    private void RefreshNavPosition()
    {
        if (navBar == null || !navHasContent) return;
        if (imageMode)
        {
            if (imagePages.Count == 0) return;
            navBar.SetPosition(imageIndex, imagePages.Count, $"{imageIndex + 1} / {imagePages.Count}",
                i => $"{i + 1} / {imagePages.Count}   {imageSet?.Name(imagePages[i].ItemIndex)}");
        }
        else if (epubMode && !txtWeb)
        {
            if (epubBook == null || epubBook.Chapters.Count == 0) return;
            var chs = epubBook.Chapters;
            string posText = epubPageMode && epubPageTotal > 0
                ? $"{epubPageIdx + 1} / {epubPageTotal}" // 페이지 방식: 지금 챕터의 현재 페이지 / 전체 페이지
                : $"{epubChapterIndex + 1} / {chs.Count}";
            navBar.SetPosition(epubChapterIndex, chs.Count, posText,
                i => $"{i + 1}. {chs[i].Name}");
        }
        else if (editorHost.Visible || txtWeb)
        {
            int len = TxtLength;
            int value = len == 0 ? 0 : (int)((long)TxtOffset * (NavTxtScale - 1) / len);
            if (value == navLastTxtValue) return;
            navLastTxtValue = value;
            navBar.SetPosition(value, NavTxtScale, $"{value * 100 / (NavTxtScale - 1)}%",
                v => $"{v * 100 / (NavTxtScale - 1)}%   {TxtChapterAt((int)((long)v * TxtLength / (NavTxtScale - 1)))}");
        }
    }

    /// <summary>txt 전체 글 길이 — 페이지 방식은 fullText, 스크롤(수정 중일 수도 있음)은 본문 그대로.</summary>
    private int TxtLength => pageMode || txtWeb ? fullText.Length : mainEditor.TextLength;

    /// <summary>txt에서 지금 화면 맨 위 글자의 전체 글 기준 위치.</summary>
    private int TxtOffset => txtWeb ? txtWebTopOffset : TxtTopOffset(); // GetCharIndexFromPosition은 6만 자를 넘으면 틀리므로 줄 번호로 계산한다

    private string TxtChapterAt(int offset)
    {
        int i = TxtChapterIndexAt(offset);
        return i >= 0 ? chapters[i].Name : "";
    }

    private int TxtChapterIndexAt(int offset)
    {
        int found = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Start <= offset) found = i;
            else break;
        }
        return found;
    }

    private void OnNavSeek(int value)
    {
        HideEdgePrompt();
        if (imageMode) ShowImagePage(value);
        else if (epubMode && !txtWeb) { if (value != epubChapterIndex) ShowEpubChapter(value); }
        else if (editorHost.Visible || txtWeb)
        {
            if (ttsActive) StopTts();
            GoToOffset((int)((long)value * TxtLength / (NavTxtScale - 1)));
            navLastTxtValue = -1;
            RefreshNavPosition();
        }
    }

    /// <summary>"챕터" / "이미지 선택" 버튼 — 지금 보는 곳을 골라둔 채로 목록을 띄운다.</summary>
    private void ShowNavItemList()
    {
        var names = CurrentChapterNames();
        if (names.Count == 0) return;
        var numbered = names.Select((n, i) => $"{i + 1}. {n}").ToList();
        navBar.ShowList(forFiles: false, numbered, CurrentListIndex(), OnChapterActivated);
    }

    /// <summary>챕터 · 이미지 목록에서 지금 보는 곳의 번호(CurrentChapterNames 기준).</summary>
    private int CurrentListIndex() =>
        imageMode ? (imageIndex >= 0 && imageIndex < imagePages.Count ? imagePages[imageIndex].ItemIndex : 0)
        : epubMode && !txtWeb ? epubChapterIndex
        : TxtChapterIndexAt(TxtOffset);

    /// <summary>
    /// 탐색 막대의 ◀ ▶ · ← → 키: 이전 · 다음 페이지. 만화는 한 장, 소설 페이지 방식은 한 페이지(끝이면 다음 챕터 · 옆 파일 처리는
    /// 화살표 버튼과 같다), 스크롤 방식은 한 화면만큼 스크롤, 고치는 중인 txt는 텍스트 상자를 한 화면만큼 스크롤.
    /// </summary>
    private void TurnPage(int delta)
    {
        HideEdgePrompt();
        if (imageMode) ChangeImage(delta);
        else if (epubMode && epubPageMode) EpubChangePage(delta);
        else if (epubMode) RunEpubScript($"window.scrollBy(0, {(delta > 0 ? "" : "-")}window.innerHeight * 0.9);");
        else if (editorHost.Visible && mainEditor.IsHandleCreated)
            SendMessage(mainEditor.Handle, 0x0115 /*WM_VSCROLL*/, (IntPtr)(delta > 0 ? 3 /*SB_PAGEDOWN*/ : 2 /*SB_PAGEUP*/), IntPtr.Zero);
    }

    /// <summary>"파일" 목록에서 바로 앞 · 뒤 파일을 연다. 폴더의 끝이면 [ ] 키와 같이 설정의
    /// "처음 · 마지막 파일에서"를 따른다(다시 처음으로 · 옆 폴더로 등).</summary>
    private void OpenNavNeighbor(bool forward)
    {
        var files = NavFolderFiles(out int index);
        int next = index + (forward ? 1 : -1);
        if (files != null && index >= 0 && next >= 0 && next < files.Count) OpenPath(files[next]);
        else GoAdjacentFile(forward);
    }

    /// <summary>"파일" 버튼 — 같은 폴더의 책 파일 목록. 고르면 이 창에서 연다.</summary>
    private void ShowNavFileList()
    {
        var files = NavFolderFiles(out int index);
        if (files == null || files.Count == 0) return;
        navBar.ShowList(forFiles: true, files.Select(Path.GetFileName).ToList()!, index, i => OpenPath(files[i]));
    }

    /// <summary>지금 파일이 있는 폴더의 책 파일들(txt · epub · pdf · zip, 이름의 자연 순서). 지금 파일이 낱장 이미지면 그 폴더의 이미지들.
    /// zip에서 꺼낸 임시 파일처럼 "폴더" 개념이 없으면 null.</summary>
    private List<string>? NavFolderFiles(out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(currentPath)) return null;
        try
        {
            string full = Path.GetFullPath(currentPath);
            string tempZipRoot = Path.Combine(Path.GetTempPath(), "CoffeeViewerZip");
            if (full.StartsWith(tempZipRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            string? dir = Path.GetDirectoryName(full);
            if (dir == null || !Directory.Exists(dir)) return null;

            bool image = ZipImageSet.IsImageExtension(full);
            var files = Directory.EnumerateFiles(dir)
                .Where(f => image ? ZipImageSet.IsImageExtension(f) : NavFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, NaturalStringComparer.Instance)
                .ToList();
            index = files.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            return files;
        }
        catch { return null; }
    }
}
