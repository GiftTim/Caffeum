using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CoffeeViewer;

/// <summary>
/// 나만의 소설뷰어에서 가져온 기능들: 서재(책장) · 낭독(TTS) · 테마/설정창 · 몰입 모드 · 읽던 자리 기억 ·
/// 끌어놓기로 열기 · 우클릭 메뉴/웹 검색 · 토스트 알림 · 도움말.
/// 원래 MainForm.cs는 이미 크기가 커서, 새 기능은 이 파일에 모으고 MainForm.cs에는 연결 지점만 남겼다.
/// </summary>
public partial class MainForm
{
    // 설정과 서재 기록은 창이 여러 개여도 하나를 같이 쓴다(AppShared).
    private readonly AppSettings settings = AppShared.Settings;
    private readonly LibraryStore library = AppShared.Library;
    private ReaderTheme theme = Themes.All[0];

    /// <summary>지금 이 읽기 창에 열려 있는 파일의 전체 경로(같은 책을 또 열 때 새 창 대신 이 창을 앞으로 가져오려고).</summary>
    public string? CurrentFilePath => string.IsNullOrEmpty(currentPath) ? null : Path.GetFullPath(currentPath);
    private readonly ToastLabel toast = new();
    private readonly TtsEngine tts = new();
    private readonly System.Windows.Forms.Timer positionTimer = new() { Interval = 4000 };
    private readonly System.Windows.Forms.Timer immersionHoverTimer = new() { Interval = 200 };

    private ToolStripMenuItem? autoPlayToggleMenuItem; // 자동 재생 시작/정지(F5) — 만화는 슬라이드쇼, 소설은 낭독
    private ToolStripMenuItem? ttsRateMenu;
    private ToolStripMenuItem? pdfDarkMenuItem;
    private ToolStripMenuItem? immersionMenuItem;
    private ToolStripMenuItem? recentMenu;
    private ToolStripMenuItem? ttsVoiceMenu;
    private readonly List<ToolStripMenuItem> themeMenuItems = new();
    private readonly List<ToolStripMenuItem> ttsRateMenuItems = new();

    // ── 읽던 자리 ──
    private int epubRatioPermille;          // epub: 챕터 안 위치(‰). 주기 타이머가 웹뷰에서 읽어온 마지막 값
    private int? epubRestorePermille;       // 챕터를 새로 불러온 직후 이 위치로 옮긴다
    private int lastEpubTrackedChapter = -1;
    private bool positionQueryBusy;

    // ── 낭독 ──
    private bool ttsActive;
    private int ttsPos;                     // txt: 전체 글 기준 / epub: 챕터 본문 기준, 다음에 읽을 글자 위치
    private string ttsEpubText = "";
    private bool ttsAwaitingChapter;        // 다음 챕터로 넘기는 중 — 로딩이 끝나면 처음부터 이어 읽는다
    private bool ttsInternalNav;            // 낭독 스스로 페이지/챕터를 넘기는 중(직접 넘김 = 낭독 중지 규칙에서 제외)
    private Point editorContextPoint;

    // ── 몰입 모드 ──
    private bool immersive;
    internal bool Immersive => immersive; // 머리 막대의 몰입 버튼 그림이 이 값을 따른다
    private Rectangle immersiveRestoreBounds;
    private FormWindowState immersiveRestoreState;
    private IntPtr immersiveRestoreStyle;

    // ───────────────────────── 메뉴 ─────────────────────────

    private void BuildFeatureMenus(MenuStrip menu, ToolStripMenuItem fileMenu, ToolStripMenuItem viewMenu)
    {
        // 파일: 서재 / 최근 파일 (열기 바로 위·아래)
        fileMenu.DropDownItems.Insert(0, new ToolStripMenuItem("볼트 열기…", null, (_, _) => ShowLibrary()) { ShortcutKeyDisplayString = "Ctrl+L" });
        recentMenu = new ToolStripMenuItem("최근 파일");
        recentMenu.DropDownItems.Add(new ToolStripMenuItem("(없음)") { Enabled = false }); // 드롭다운 화살표가 뜨게 자리만 잡아둔다
        recentMenu.DropDownOpening += (_, _) => FillRecentMenu();
        fileMenu.DropDownItems.Insert(2, recentMenu);
        // 설정은 보기 메뉴가 아니라 파일 메뉴 맨 아래에 둔다(메모장처럼).
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("설정…", null, (_, _) => ShowSettings()) { ShortcutKeyDisplayString = "Ctrl+," });

        // 보기: 테마 / PDF 야간 모드 / 몰입 모드 / 설정
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        var themeMenu = new ToolStripMenuItem("테마");
        foreach (var t in Themes.All)
        {
            var item = new ToolStripMenuItem(t.Name, null, (_, _) => { settings.Theme = t.Name; settings.Save(); AppShared.NotifySettingsChanged("theme"); });
            themeMenuItems.Add(item);
            themeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(themeMenu);
        pdfDarkMenuItem = new ToolStripMenuItem("PDF 야간 모드", null, (_, _) =>
        {
            settings.PdfDarkMode = !settings.PdfDarkMode;
            settings.Save();
            AppShared.NotifySettingsChanged("pdf");
        });
        viewMenu.DropDownItems.Add(pdfDarkMenuItem);
        immersionMenuItem = new ToolStripMenuItem("몰입 모드", null, (_, _) => ToggleImmersion()) { ShortcutKeyDisplayString = "F11" };
        viewMenu.DropDownItems.Add(immersionMenuItem);

        // 자동 재생 — 만화의 슬라이드쇼와 소설의 낭독을 한 메뉴·한 단축키(F5)로 묶었다.
        var ttsMenu = new ToolStripMenuItem("자동 재생");
        autoPlayToggleMenuItem = new ToolStripMenuItem("시작", null, (_, _) => ToggleAutoPlay()) { ShortcutKeyDisplayString = "F5" };
        ttsMenu.DropDownItems.Add(autoPlayToggleMenuItem);
        ttsMenu.DropDownItems.Add(new ToolStripSeparator());
        var rateMenu = ttsRateMenu = new ToolStripMenuItem("낭독 속도");
        foreach (int rate in new[] { -5, -3, -1, 0, 1, 2, 3, 5 })
        {
            var item = new ToolStripMenuItem(rate == 0 ? "보통" : rate > 0 ? $"빠르게 +{rate}" : $"느리게 {rate}", null, (_, _) =>
            {
                settings.TtsRate = rate;
                settings.Save();
                AppShared.NotifySettingsChanged("tts");
            }) { Tag = rate };
            ttsRateMenuItems.Add(item);
            rateMenu.DropDownItems.Add(item);
        }
        ttsMenu.DropDownItems.Add(rateMenu);
        ttsVoiceMenu = new ToolStripMenuItem("낭독 목소리");
        ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem("(불러오는 중)") { Enabled = false });
        ttsVoiceMenu.DropDownOpening += (_, _) => FillVoiceMenu();
        ttsMenu.DropDownItems.Add(ttsVoiceMenu);
        ttsMenu.DropDownItems.Add(slideshowMenu); // "만화 넘김 간격"(MainForm.cs에서 만들어 둔 것)
        menu.Items.Add(ttsMenu);

        var helpMenu = new ToolStripMenuItem("도움말");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("사용법", null, (_, _) => ShowHelp()) { ShortcutKeyDisplayString = "F1" });
        menu.Items.Add(helpMenu);
    }

    private void FillRecentMenu()
    {
        if (recentMenu == null) return;
        recentMenu.DropDownItems.Clear();
        var items = library.Recent(12).Where(b => File.Exists(b.Path)).Take(10).ToList();
        if (items.Count == 0)
        {
            recentMenu.DropDownItems.Add(new ToolStripMenuItem("(없음)") { Enabled = false });
            return;
        }
        for (int i = 0; i < items.Count; i++)
        {
            string path = items[i].Path;
            string label = $"{i + 1}. {Path.GetFileName(path)}".Replace("&", "&&");
            recentMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => OpenPath(path)) { ToolTipText = path });
        }
    }

    private void FillVoiceMenu()
    {
        if (ttsVoiceMenu == null) return;
        ttsVoiceMenu.DropDownItems.Clear();
        var voices = TtsEngine.InstalledVoices();
        if (voices.Count == 0)
        {
            ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem("(설치된 음성이 없습니다)") { Enabled = false });
            return;
        }
        string current = settings.TtsVoice != null && voices.Contains(settings.TtsVoice) ? settings.TtsVoice : voices[0];
        foreach (var v in voices)
        {
            string name = v;
            ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem(name.Replace("&", "&&"), null, (_, _) =>
            {
                settings.TtsVoice = name;
                settings.Save();
                AppShared.NotifySettingsChanged("tts");
            }) { Checked = name == current });
        }
    }

    /// <summary>모드(txt/epub/pdf/만화)에 따라 자동 재생·낭독·PDF 야간 모드 메뉴를 켜고 끈다. SetContentMode 끝에서 부른다.</summary>
    private void UpdateFeatureMenus(ContentKind kind)
    {
        bool text = kind is ContentKind.Txt or ContentKind.Epub;
        if (autoPlayToggleMenuItem != null) autoPlayToggleMenuItem.Enabled = text || kind == ContentKind.Image;
        if (ttsRateMenu != null) ttsRateMenu.Enabled = text;
        if (ttsVoiceMenu != null) ttsVoiceMenu.Enabled = text;
        UpdateAutoPlayMenu();
        if (pdfDarkMenuItem != null) pdfDarkMenuItem.Enabled = kind == ContentKind.Pdf;
    }

    // ───────────────────────── 시작 시 초기화 ─────────────────────────

    private long lastWheelTurnTick;

    private void InitFeatures()
    {
        ApplyLook();
        mainEditor.HideSelection = false; // 낭독 중 표시한 문장이 본문에 포커스가 없어도 계속 보이게
        mainEditor.BorderStyle = BorderStyle.None; // 테마 색이 바뀌어도 흰 테두리가 남지 않게(창 핸들이 만들어지기 전인 지금 한 번만 바꾼다)
        mainEditor.HandleCreated += (_, _) => ApplySideMargin();
        mainEditor.SizeChanged += (_, _) => { if (EffPaper != null) ApplySideMargin(); };
        mainEditor.MouseWheel += (_, e) =>
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!CanZoomText) return;
                ViewZoomBy(e.Delta > 0 ? 1 : -1);
                if (e is HandledMouseEventArgs h) h.Handled = true; // 본문이 같이 스크롤되지 않게
                return;
            }
        };
        // (옛) 페이지 방식에서는 휠이 본문을 스크롤하지 않고(PageLockTextBox가 막는다), 한 칸에 한 페이지씩 넘긴다.
        mainEditor.WheelTurned += delta =>
        {
            if (!pageMode || delta == 0) return;
            // 터치 패닝이나 정밀 휠은 작은 값이 연달아 오므로, 한 번 넘긴 뒤 잠깐은 무시한다.
            if (Environment.TickCount64 - lastWheelTurnTick < 250) return;
            lastWheelTurnTick = Environment.TickCount64;
            ChangePage(delta < 0 ? 1 : -1);
        };
        zoomTimer.Tick += (_, _) => { zoomTimer.Stop(); ApplyPendingZoom(); };
        FormClosed += (_, _) => zoomTimer.Dispose();
        ApplyEditorWrap(); // 저장해 둔 자동 줄바꿈 설정

        tts.VoiceName = settings.TtsVoice;
        tts.Rate = settings.TtsRate;
        tts.SentenceFinished += OnTtsSentenceFinished;
        SyncTtsMenu();

        Controls.Add(toast);
        AllowDrop = true;
        DragEnter += OnFileDragEnter;
        DragDrop += OnFileDrop;
        mainEditor.ContextMenuStrip = BuildEditorContextMenu();

        positionTimer.Tick += (_, _) => { CapturePosition(); library.SaveIfDirty(); };
        positionTimer.Start();
        immersionHoverTimer.Tick += (_, _) => ImmersionHoverTick(); // 몰입 모드에 들어갈 때만 돌린다(ToggleImmersion)
        FormClosing += (_, _) =>
        {
            StopTts();
            CapturePosition();
            library.SaveIfDirty();
            settings.Save();
        };
        // 설정창·메뉴에서 바뀐 설정은 열려 있는 모든 읽기 창이 각자 반영한다(AppShared가 알려준다).
        AppShared.SettingsChanged += OnSettingsChanged;
        FormClosed += (_, _) =>
        {
            AppShared.SettingsChanged -= OnSettingsChanged;
            tts.Dispose();
            positionTimer.Dispose();
            immersionHoverTimer.Dispose();
        };

        ApplyMargins();
        ApplyTheme();
    }

    // ───────────────────────── 테마 · 여백 · 설정 반영 ─────────────────────────

    private void ApplyTheme()
    {
        theme = Themes.Find(settings.Theme);
        ToolStripManager.Renderer = Themes.CreateRenderer(theme);
        Themes.SetDarkTitleBar(this, theme.Dark);

        BackColor = theme.Chrome;
        ForeColor = theme.ChromeFore;
        statusLabel.BackColor = theme.Chrome;
        statusLabel.ForeColor = theme.ChromeFore;
        statusSegments?.ApplyColors(theme.Chrome, theme.ChromeFore);
        pageNavPanel.BackColor = theme.Chrome;
        pageIndicatorLabel.BackColor = theme.Chrome;
        pageIndicatorLabel.ForeColor = theme.ChromeFore;
        if (MainMenuStrip != null)
        {
            MainMenuStrip.BackColor = theme.Chrome;
            MainMenuStrip.ForeColor = theme.ChromeFore;
        }
        readerTitle?.SetColors(theme.Chrome, theme.ChromeFore);

        ApplyLookBarTheme();
        foreach (var host in new Control[] { contentHost, editorHost, epubHost, pdfHost }) host.BackColor = theme.Back;
        mainEditor.BackColor = theme.Back;
        mainEditor.ForeColor = theme.Fore;
        writerTitle?.SetColors(theme.Back, theme.Fore);
        SyncGoalWidget();
        if (epubView != null) epubView.DefaultBackgroundColor = theme.Back;
        if (epubMode) RunEpubScript(BuildEpubThemeScript());

        foreach (var item in themeMenuItems) item.Checked = item.Text == theme.Name;

        navBar.ApplyTheme(theme);
        findBar.ApplyTheme(theme);
        // 서재·설정 창의 색은 AppShared.NotifySettingsChanged가 바꾼다.
    }

    private void ApplyMargins()
    {
        // 상하 여백은 본문을 감싼 자리의 안쪽 여백으로, 좌우 여백은 텍스트박스 내부 여백으로 준다.
        // (좌우를 바깥 패딩으로 주면 세로 스크롤바가 창 끝이 아니라 본문 안쪽에 떠서 어색하다.)
        var paper = EffPaper;
        editorHost.Padding = paper == null
            ? new Padding(0, settings.VerticalMargin, 0, settings.VerticalMargin)
            : new Padding(0, MmToPx(paper.Top), 0, MmToPx(paper.Bottom));
        ApplySideMargin();
    }

    private void ApplySideMargin()
    {
        if (!mainEditor.IsHandleCreated) return;
        int m = Math.Clamp(settings.SideMargin, 0, 300);
        if (EffPaper is { } paper)
        {
            // 용지 칸 폭만큼만 글이 놓이게, 남는 폭을 좌우 여백으로 나눈다(창이 좁으면 0).
            int usable = MmToPx(paper.ContentWidth) * ViewZoom / 100;
            m = Math.Clamp((mainEditor.ClientSize.Width - usable) / 2, 0, 2000);
        }
        else if (WriterLayout) m = WriterSideMargin(m); // 회차 글쓰기: 글을 가운데 일정한 폭의 칸에
        mainEditor.SetSideMargins(m);
        SyncWriterTitle(m);
    }

    /// <summary>설정창의 챕터 나누기 미리보기용: 지금 txt를 보고 있으면 그 전체 글과 이름을 돌려준다.</summary>
    internal bool TryGetTxtForPreview(out string text, out string name)
    {
        bool ok = shownKind == ContentKind.Txt && fullText.Length > 0;
        text = ok ? fullText : "";
        name = Path.GetFileNameWithoutExtension(currentPath ?? "");
        return ok;
    }

    /// <summary>설정창(또는 메뉴)에서 값이 바뀌었을 때 화면·엔진에 즉시 반영한다.</summary>
    private void OnSettingsChanged(string kind)
    {
        switch (kind)
        {
            case "theme":
                ApplyTheme();
                break;
            case "toolbar":
                ApplyToolbarVisibility();
                ApplyLookBarShown();
                break;
            case "novelreading":
                // 보고 있는 epub · 웹 화면 글에도 바로(바로 쓰는 txt · 수정 중에는 건드리지 않는다).
                if (epubMode && !(epubEditable && !txtWeb))
                    SetReadingMode(settings.NovelReadingMode switch { NovelReadingMode.Page => ReadingMode.Page, NovelReadingMode.PageTouch => ReadingMode.PageTouch, _ => ReadingMode.Scroll });
                break;
            case "font":
                if (mainEditor.Font.Name != EffFont || Math.Abs(mainEditor.Font.SizeInPoints - fontSize) > 0.01f) mainEditor.Font = new Font(EffFont, fontSize);
                if (epubMode) ApplyEpubFontSize(keepPlace: true); // 글자 크기와 같이, 다시 불러오지 않고 보던 자리에서
                else ReflowAfterLookChange();
                break;
            case "fontsize":
                ApplyLook();
                break;
            case "margin":
                marginBoxesFilled = false;
                ApplyMargins();
                ReflowAfterLookChange();
                break;
            case "tts":
                tts.Rate = settings.TtsRate;
                tts.VoiceName = settings.TtsVoice;
                SyncTtsMenu();
                break;
            case "pdf":
                if (pdfHost.Visible && !string.IsNullOrEmpty(currentPath)) NavigatePdf(currentPath);
                break;
            case "imagelayout":
                ApplyImageLayout();
                break;
            case "imagemargin":
                marginBoxesFilled = false;
                ApplyImageMargins();
                break;
            case "anim":
                imageCanvas.RestartAnimation();
                break;
            case "imagereading":
                if (imageMode) SetReadingMode(settings.ImagePageTouch ? ReadingMode.PageTouch : ReadingMode.Page); // 보고 있는 만화에도 바로
                break;
            case "txtsplit":
                // 열려 있는 txt는 챕터 목록만 다시 나눈다(본문과 읽던 위치는 그대로). 수정 중이거나 zip에서 꺼낸 글은 다시 열 때 적용된다.
                if (shownKind == ContentKind.Txt && !txtFromZip && mainEditor.ReadOnly && fullText.Length > 0)
                {
                    chapters = TxtChapterSplitter.Split(fullText, Path.GetFileNameWithoutExtension(currentPath ?? ""), TxtSplitOptions.From(settings));
                    statusLabel.Text = $"{chapters.Count}개 챕터 · 총 {fullText.Length:N0}자";
                    if (txtWeb) ShowTxtWeb(txtWebTopOffset); // 웹 화면의 문서 나눔도 새 챕터대로 다시 만든다
                }
                break;
        }
    }

    private void SyncTtsMenu()
    {
        foreach (var item in ttsRateMenuItems) item.Checked = item.Tag is int r && r == settings.TtsRate;
    }

    /// <summary>글꼴·여백이 바뀌어 한 화면에 들어가는 글이 달라졌으니, 페이지 모드면 페이지를 다시 잰다.</summary>
    private void ReflowAfterLookChange()
    {
        if (pageMode)
        {
            currentPageEnd = FindPageEnd(currentPageStart);
            ShowCurrentPage();
            mainEditor.SelectionStart = 0;
        }
        if (epubMode) ApplyEpubLook();
    }

    private void ApplyEpubLook()
    {
        if (epubPageMode)
        {
            ReloadEpubKeepingPlace(); // 다단 배치를 새 글꼴·여백 기준으로 다시 잰다
        }
        else
        {
            ApplyEpubFontSize();
            RunEpubScript(BuildEpubResetColumnsScript());
        }
    }

    private async void RunEpubScript(string script)
    {
        if (epubView?.CoreWebView2 == null) return;
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { /* 문서가 아직 준비 안 됐으면 무시 */ }
    }

    // ───────────────────────── 읽던 자리 기억 ─────────────────────────

    /// <summary>파일을 열 때마다 서재 기록(마지막으로 연 시각)을 남긴다. 최근 파일 메뉴도 여기서 나온다.</summary>
    private void OnBookOpened(string path)
    {
        lookRec = library.Touch(path);
        library.SaveIfDirty();
        ApplyLook(); // 이 파일에 기억해 둔 서식으로
        // txt는 SetContentMode가 currentPath를 바꾸기 전에 불리므로, 탐색 막대의 "파일" 목록은 여기서 다시 맞춘다.
        ConfigureNavBar(navKind);
    }

    /// <summary>지금 보고 있는 위치를 서재 기록에 적는다(메모리에만; 디스크에는 타이머·종료 때 저장).</summary>
    private void CapturePosition()
    {
        string? path = currentPath;
        if (string.IsNullOrEmpty(path)) return;

        if (imageMode)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && imagePages.Count > 0)
                library.SavePosition(path, imageIndex, 0, (imageIndex + 1.0) / imagePages.Count);
        }
        else if (txtWeb)
        {
            // 예전(텍스트 상자) 기록과 같은 형식 — 전체 글 기준 글자 위치 — 으로 남겨서 어느 쪽으로 열어도 이어 읽힌다.
            if (txtBook != null && fullText.Length > 0)
            {
                library.SavePosition(path, txtWebTopOffset, 0, (double)txtWebTopOffset / fullText.Length);
                QueryTxtTop(); // 다음 기록 때 쓸 최신 위치를 웹뷰에서 받아둔다
            }
        }
        else if (epubMode)
        {
            if (epubBook != null && epubBook.Chapters.Count > 0)
            {
                double progress = (epubChapterIndex + epubRatioPermille / 1000.0) / epubBook.Chapters.Count;
                library.SavePosition(path, epubChapterIndex, epubRatioPermille, progress);
                QueryEpubRatio(); // 다음 기록 때 쓸 최신 위치를 웹뷰에서 받아둔다
            }
        }
        else if (editorHost.Visible)
        {
            int offset = TxtTopOffset();
            int length = pageMode ? fullText.Length : mainEditor.TextLength;
            library.SavePosition(path, offset, 0, length == 0 ? 0 : (double)offset / length);
        }
    }

    private async void QueryEpubRatio()
    {
        if (epubView?.CoreWebView2 == null || positionQueryBusy) return;
        positionQueryBusy = true;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync(
                "(window.__clrTotalPages !== undefined)" +
                "? Math.round(1000 * window.__clrPageIndex / Math.max(1, window.__clrTotalPages - 1))" +
                ": Math.round(1000 * window.scrollY / Math.max(1, document.documentElement.scrollHeight - window.innerHeight))");
            if (int.TryParse(json, out int v)) epubRatioPermille = Math.Clamp(v, 0, 1000);
        }
        catch { /* 문서가 바뀌는 중이면 이번 값은 건너뛴다 */ }
        finally { positionQueryBusy = false; }
    }

    /// <summary>txt에서 지금 화면 맨 위에 보이는 글자의 전체 글 기준 위치.</summary>
    private int TxtTopOffset()
    {
        if (pageMode) return currentPageStart;
        if (!mainEditor.IsHandleCreated) return 0;
        int firstLine = (int)SendMessage(mainEditor.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        return Math.Max(0, (int)SendMessage(mainEditor.Handle, EM_LINEINDEX, (IntPtr)firstLine, IntPtr.Zero));
    }

    /// <summary>
    /// 클릭한 자리(mainEditor 좌표)의 줄 첫 글자 위치. 텍스트박스의 글자 위치 조회 메시지(EM_CHARFROMPOS)는 결과가
    /// 16비트로 잘려서 글이 6만 자를 넘으면 틀리므로, 줄 번호로 계산한다: 맨 위 줄 + (클릭 높이 ÷ 실제 줄 간격).
    /// </summary>
    private int CharIndexAtPoint(Point p)
    {
        if (!mainEditor.IsHandleCreated) return 0;
        var h = mainEditor.Handle;
        int first = (int)SendMessage(h, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        int firstIdx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)first, IntPtr.Zero);
        int nextIdx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)(first + 1), IntPtr.Zero);
        if (firstIdx < 0) return 0;

        int y0 = mainEditor.GetPositionFromCharIndex(firstIdx).Y;
        int lineHeight = nextIdx > firstIdx ? mainEditor.GetPositionFromCharIndex(nextIdx).Y - y0 : mainEditor.Font.Height;
        if (lineHeight <= 0) lineHeight = Math.Max(1, mainEditor.Font.Height);

        int line = first + Math.Max(0, (int)Math.Floor((p.Y - y0) / (double)lineHeight));
        int idx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)line, IntPtr.Zero);
        return idx < 0 ? Math.Max(0, mainEditor.TextLength - 1) : idx;
    }

    /// <summary>mainEditor에서 index가 있는 줄이 화면 맨 위에 오도록 스크롤한다(ScrollToCaret은 그 줄이 맨 아래에 걸리기도 해서).</summary>
    private void ScrollCharToTop(int index)
    {
        if (!mainEditor.IsHandleCreated) return;
        var h = mainEditor.Handle;
        int line = (int)SendMessage(h, EM_LINEFROMCHAR, (IntPtr)index, IntPtr.Zero);
        int first = (int)SendMessage(h, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        if (line != first) SendMessage(h, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(line - first));
    }

    /// <summary>epub을 열 때 읽던 챕터 번호를 돌려주고, 챕터 안 위치는 챕터가 뜬 뒤에 복원하도록 예약해둔다.</summary>
    private int TakeSavedEpubStart(string path)
    {
        var rec = library.Find(path);
        int chapter = 0, ratio = 0;
        if (rec != null && epubBook != null && epubBook.Chapters.Count > 0)
        {
            chapter = Math.Clamp(rec.PosA, 0, epubBook.Chapters.Count - 1);
            ratio = Math.Clamp(rec.PosB, 0, 1000);
        }
        lastEpubTrackedChapter = chapter;
        epubRatioPermille = ratio;
        epubRestorePermille = ratio > 0 ? ratio : null;
        if (chapter > 0 || ratio > 0) ShowToast("이어서 읽습니다");
        return chapter;
    }

    private int SavedImagePage(string path)
    {
        var rec = library.Find(path);
        if (rec == null || rec.PosA <= 0) return 0;
        ShowToast("이어서 읽습니다");
        return rec.PosA;
    }

    /// <summary>epub 챕터가 다 뜬 직후(모든 스크립트를 심은 뒤): 읽던 위치로 옮기고, 낭독 중이었다면 이어서 읽는다.</summary>
    private async Task AfterEpubChapterReady()
    {
        if (epubView?.CoreWebView2 == null) return;

        if (epubRestorePermille is int ratio)
        {
            epubRestorePermille = null;
            string js = "(function(r){" +
                " if (window.__clrGoTo && window.__clrTotalPages !== undefined) window.__clrGoTo(Math.round(r / 1000 * (window.__clrTotalPages - 1)));" +
                " else window.scrollTo(0, Math.min(r, 980) / 1000 * Math.max(0, document.documentElement.scrollHeight - window.innerHeight));" +
                "})(" + ratio + ");";
            try { await epubView.CoreWebView2.ExecuteScriptAsync(js); } catch { }
        }

        // txt 웹 화면: 문서가 뜨면 기다리던 원본 위치로 옮기거나, 찾은 글을 표시한다.
        if (txtWeb && txtPendingSelect is var (selStart, selLength))
        {
            txtPendingSelect = null;
            txtPendingOffset = null;
            try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTxtSel && window.__clrTxtSel({selStart},{selLength});"); } catch { }
        }
        else if (txtWeb && txtPendingOffset is int pending)
        {
            txtPendingOffset = null;
            try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTxtGo && window.__clrTxtGo({pending});"); } catch { }
            txtWebTopOffset = pending;
        }
        else if (txtWeb && txtBook != null && epubChapterIndex >= 0 && epubChapterIndex < txtBook.Ranges.Count)
        {
            txtPendingOffset = null;
            QueryTxtTop(); // 앞 · 뒤 문서로 넘어온 경우(페이지 끝 · 스크롤 바닥) — 실제로 보이는 자리를 읽어 둔다
        }
        RefreshNavPosition();

        if (ttsActive && ttsAwaitingChapter)
        {
            ttsAwaitingChapter = false;
            if (!await PrepareEpubTts()) { StopTts(); return; }
            ttsPos = 0;
            SpeakNextEpub();
        }
    }

    // ───────────────────────── 서재 · 끌어놓기 · 설정/도움말 창 ─────────────────────────

    // 서재는 이제 프로그램의 시작 화면이라 읽기 창이 소유하지 않는다 — 창을 열고 닫는 건 AppShared가 맡는다.
    // 서재 안에 들어가 있으면(embedded) 서재 목록 화면으로 돌아가고, 따로 뜬 창이면 서재 창을 앞으로 가져온다.
    private void ShowLibrary()
    {
        if (embedded) BackToLibraryRequested?.Invoke();
        else AppShared.PickAndOpenVault(this);
    }

    private void ShowSettings() => AppShared.ShowSettings(this);

    private void ShowHelp()
    {
        using var help = new HelpForm();
        help.ApplyTheme(theme);
        help.TopMost = HostForm.TopMost;
        help.ShowDialog(this);
    }

    private void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>파일을 끌어다 놓으면 그 책을 열고, 폴더를 놓으면 그 폴더를 볼트로 연다.</summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var dirs = paths.Where(Directory.Exists).ToList();
        if (dirs.Count > 0)
        {
            AppShared.AddFoldersToLibrary(dirs);
            return;
        }

        string file = paths.First(File.Exists);
        OpenPath(file);
    }

    // ───────────────────────── 우클릭 메뉴 · 웹 검색 ─────────────────────────

    private ContextMenuStrip BuildEditorContextMenu()
    {
        var readHere = new ToolStripMenuItem("여기서부터 낭독", null, (_, _) =>
        {
            StartTts((pageMode ? currentPageStart : 0) + CharIndexAtPoint(editorContextPoint));
        });
        var copy = new ToolStripMenuItem("복사", null, (_, _) => mainEditor.Copy());
        var selectAll = new ToolStripMenuItem("모두 선택", null, (_, _) => mainEditor.SelectAll());
        var naver = new ToolStripMenuItem("선택한 글 네이버로 검색", null, (_, _) => WebSearch(mainEditor.SelectedText, naver: true));
        var google = new ToolStripMenuItem("선택한 글 구글로 검색", null, (_, _) => WebSearch(mainEditor.SelectedText, naver: false));
        var undo = new ToolStripMenuItem("실행 취소", null, (_, _) => mainEditor.Undo());
        var cut = new ToolStripMenuItem("잘라내기", null, (_, _) => mainEditor.Cut());
        var paste = new ToolStripMenuItem("붙여넣기", null, (_, _) => mainEditor.Paste());
        var delete = new ToolStripMenuItem("삭제", null, (_, _) => { if (mainEditor.SelectionLength > 0) mainEditor.SelectedText = ""; });
        var editSeparator = new ToolStripSeparator();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            readHere, new ToolStripSeparator(), copy, selectAll, new ToolStripSeparator(), naver, google,
            editSeparator, undo, cut, paste, delete,
        });
        menu.Opening += (_, _) =>
        {
            editorContextPoint = mainEditor.PointToClient(Cursor.Position);
            bool editing = !mainEditor.ReadOnly;
            bool hasSelection = mainEditor.SelectionLength > 0;
            readHere.Enabled = !editing;
            copy.Enabled = hasSelection;
            naver.Enabled = google.Enabled = hasSelection;
            // 잘라내기·붙여넣기 같은 건 수정 모드일 때만 보여준다.
            editSeparator.Visible = undo.Visible = cut.Visible = paste.Visible = delete.Visible = editing;
            cut.Enabled = delete.Enabled = hasSelection;
        };
        return menu;
    }

    private static void WebSearch(string text, bool naver)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text[..200];
        string url = (naver ? "https://search.naver.com/search.naver?query=" : "https://www.google.com/search?q=") + Uri.EscapeDataString(text);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnEpubContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (epubView?.CoreWebView2 == null) return;
        var env = epubView.CoreWebView2.Environment;
        string selected = e.ContextMenuTarget.HasSelection ? e.ContextMenuTarget.SelectionText : "";

        var read = env.CreateContextMenuItem("여기서부터 낭독", null, CoreWebView2ContextMenuItemKind.Command);
        read.IsEnabled = !epubEditable;
        read.CustomItemSelected += async (_, _) =>
        {
            int offset = -1;
            try
            {
                string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTts ? window.__clrTts.lastCtx : -1");
                int.TryParse(json, out offset);
            }
            catch { }
            StartTts(offset >= 0 ? offset : null);
        };

        int at = 0;
        e.MenuItems.Insert(at++, read);
        if (selected.Length > 0)
        {
            var naver = env.CreateContextMenuItem("선택한 글 네이버로 검색", null, CoreWebView2ContextMenuItemKind.Command);
            naver.CustomItemSelected += (_, _) => WebSearch(selected, naver: true);
            var google = env.CreateContextMenuItem("선택한 글 구글로 검색", null, CoreWebView2ContextMenuItemKind.Command);
            google.CustomItemSelected += (_, _) => WebSearch(selected, naver: false);
            e.MenuItems.Insert(at++, naver);
            e.MenuItems.Insert(at++, google);
        }
        e.MenuItems.Insert(at, env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
    }

    // ───────────────────────── 토스트 ─────────────────────────

    private void ShowToast(string text, int milliseconds = 2200)
    {
        toast.ShowMessage(text, milliseconds);
        toast.Left = Math.Max(0, (ClientSize.Width - toast.Width) / 2);
        toast.Top = Math.Max(0, contentHost.Bottom - toast.Height - 24);
    }

    // ───────────────────────── PDF ─────────────────────────

    /// <summary>
    /// PDF를 연다. 야간 모드가 꺼져 있으면 예전처럼 웹뷰가 PDF 파일 자체를 연다. 켜져 있으면 PDF를 담은 얇은 웹 페이지를
    /// 만들어 화면 전체에 색 반전(invert + hue-rotate)을 걸어서, 흰 종이가 검게·검은 글씨가 희게 보이게 한다.
    /// </summary>
    private void NavigatePdf(string path)
    {
        var core = pdfView!.CoreWebView2;
        if (!settings.PdfDarkMode)
        {
            core.Navigate(new Uri(path).AbsoluteUri);
            return;
        }

        // 웹 페이지에서 로컬 파일을 file://로 직접 못 가리키니, PDF가 있는 폴더를 가상 호스트 이름으로 연결해서 쓴다.
        core.SetVirtualHostNameToFolderMapping("coffee-pdf.local", Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
        string url = "https://coffee-pdf.local/" + Uri.EscapeDataString(Path.GetFileName(path));
        core.NavigateToString(
            "<!DOCTYPE html><html><body style=\"margin:0;background:#000;overflow:hidden\">" +
            $"<embed src=\"{url}\" type=\"application/pdf\" style=\"width:100vw;height:100vh;border:0;filter:invert(1) hue-rotate(180deg);\">" +
            "</body></html>");
    }

    // ───────────────────────── 몰입 모드(전체 화면) ─────────────────────────

    /// <summary>
    /// 메뉴·상태줄·제목표시줄을 숨기고 화면 전체로 본다. FormBorderStyle을 바꾸면 창 핸들이 다시 만들어져서
    /// 안에 든 WebView2(epub·PDF)가 깨질 수 있어, 창 스타일 비트만 직접 바꾸는 방식으로 한다.
    /// </summary>
    /// <summary>실제 창. 서재 안에 들어가 있으면(embedded) 서재 창, 아니면 이 창 자신 — 몰입 모드 · 활성 여부는 이 창 기준이어야 한다.</summary>
    private Form HostForm => embedded && TopLevelControl is Form f ? f : this;

    /// <summary>이 읽기 화면이 들어 있는 창이 지금 앞에 있는지(서재 안에 들어가 있으면 ActiveForm은 서재 창이다).</summary>
    private bool IsHostActive => ActiveForm == HostForm;

    /// <summary>서재 창(머리 막대)의 메뉴 · 몰입 버튼이 부르는 길.</summary>
    /// <summary>머리 막대 ☰ 버튼 바로 밑에 문서 메뉴를 띄운다. 화면에서 숨겨 둔 항목의 드롭다운은 위치를 못 잡고 화면 가운데로 가서,
    /// 항목들을 잠깐 일반 팝업 메뉴로 옮겨 띄우고 닫히면 제자리에 돌려놓는다(단축키는 원래 항목에 그대로 걸려 있다).</summary>
    internal ContextMenuStrip? ShowMainMenu(Control anchor, Point at)
    {
        if (toolbarMore == null) return null;
        var items = toolbarMore.DropDownItems.Cast<ToolStripItem>().ToList();
        if (items.Count == 0) return null;
        var cm = new ContextMenuStrip { Font = toolbarMore.DropDown.Font };
        toolbarMore.DropDownItems.Clear();
        cm.Items.AddRange(items.ToArray());
        cm.Closed += (_, _) => BeginInvoke(() =>
        {
            cm.Items.Clear();
            toolbarMore.DropDownItems.AddRange(items.ToArray());
            cm.Dispose();
        });
        cm.Show(anchor, at);
        MenuGuard.CloseOnOutsideClick(cm);
        return cm;
    }
    internal void ToggleImmersionFromHost() => ToggleImmersion();

    private void ToggleImmersion()
    {
        immersive = !immersive;
        var host = HostForm;
        if (immersive)
        {
            immersiveRestoreState = host.WindowState;
            immersiveRestoreBounds = host.WindowState == FormWindowState.Normal ? host.Bounds : host.RestoreBounds;
            if (host.WindowState == FormWindowState.Maximized) host.WindowState = FormWindowState.Normal;

            immersiveRestoreStyle = GetWindowLongPtr(host.Handle, GWL_STYLE);
            SetWindowLongPtr(host.Handle, GWL_STYLE, (IntPtr)((long)immersiveRestoreStyle & ~(long)(WS_CAPTION | WS_THICKFRAME)));
            var screen = Screen.FromControl(host).Bounds;
            SetWindowPos(host.Handle, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);

            SetToolbarVisible(false);
            statusLabel.Visible = false;
            immersionHoverTimer.Start();
            ShowToast("몰입 모드 — F11 또는 Esc로 나갑니다");
        }
        else
        {
            SetWindowLongPtr(host.Handle, GWL_STYLE, immersiveRestoreStyle);
            var r = immersiveRestoreBounds;
            SetWindowPos(host.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            if (immersiveRestoreState == FormWindowState.Maximized) host.WindowState = FormWindowState.Maximized;

            immersionHoverTimer.Stop();
            SetToolbarVisible(true);
            statusLabel.Visible = true;
        }
        if (immersionMenuItem != null) immersionMenuItem.Checked = immersive;
        // 몰입 중에는 "나가기" 모양(안쪽으로 모이는 화살표), 아닐 때는 "들어가기" 모양(바깥으로 퍼지는 화살표)
        if (toolbarItems.TryGetValue("immersion", out var immItem)) immItem.Text = immersive ? "" : GlyphFullScreen;
        RepositionOverlays();
    }

    // ───────────────────────── 단축키 ─────────────────────────

    /// <summary>F5 자동 재생(만화 슬라이드쇼 · 소설 낭독) · F11 몰입 · F1 도움말 · Ctrl+L 서재 · Ctrl+, 설정. 메뉴가 숨겨진 몰입 모드나 웹뷰에 포커스가 있을 때도 되도록 메뉴 단축키가 아니라 여기서 직접 처리한다.</summary>
    private bool TryHandleFeatureKey(Keys key)
    {
        if (BlockedByLock(key)) return true; // 잠금 모드: 쪽을 넘기거나 움직이는 키는 막는다
        switch (key)
        {
            case Keys.F5: ToggleAutoPlay(); return true;
            case Keys.F11: ToggleImmersion(); return true;
            case Keys.Escape when edgePrompt.Visible: HideEdgePrompt(); return true; // 끝 · 처음 안내 상자부터 닫는다
            case Keys.Escape when immersive: ToggleImmersion(); return true;
            case Keys.F1: ShowHelp(); return true;
            case Keys.OemCloseBrackets when CanUseBracketKeys(): GoAdjacentFile(forward: true); return true; // ]
            case Keys.OemOpenBrackets when CanUseBracketKeys(): GoAdjacentFile(forward: false); return true; // [
            case Keys.Control | Keys.L: ShowLibrary(); return true;
            case Keys.Control | Keys.S when directEdit && !mainEditor.ReadOnly: FlushAutoSave(); return true; // 쓰는 중인 글은 Ctrl+S로 바로 저장
            case Keys.Control | Keys.Oemcomma: ShowSettings(); return true;
            // 글자 크기: Ctrl+- 축소, Ctrl++(= 키·숫자패드 포함) 확대, Ctrl+0 기본. txt · epub만 — PDF · 만화는 그대로 통과시킨다.
            case Keys.Control | Keys.OemMinus or Keys.Control | Keys.Subtract when CanZoomText: ViewZoomBy(-1); return true;
            case Keys.Control | Keys.Oemplus or Keys.Control | Keys.Shift | Keys.Oemplus or Keys.Control | Keys.Add when CanZoomText: ViewZoomBy(1); return true;
            case Keys.Control | Keys.D0 or Keys.Control | Keys.NumPad0 when CanZoomText: ViewZoomReset(); return true;
            case Keys.Control | Keys.O when immersive: OpenFileDialog(); return true;
            // txt 웹 화면: 웹뷰에 포커스가 있어도 웹뷰 자체 찾기 대신 우리 찾기 바(글 전체 검색)로.
            case Keys.Control | Keys.F when txtWeb: findBar.Show(); return true;
            // ← → : 만화 · 소설 페이지 방식에서 이전 · 다음 페이지. 서재 안에 들어가 있으면 방향키를 서재 창이 "다음 칸으로
            // 포커스 옮기기"로 먼저 가져가서 만화가 안 넘어갔다 — 그래서 포커스가 어디 있든 여기서 먼저 받는다.
            case Keys.Left when CanUseArrowPaging(): TurnPage(-1); return true;
            case Keys.Right when CanUseArrowPaging(): TurnPage(1); return true;
        }
        return false;
    }

    /// <summary>← → 를 페이지 넘기기로 쓸지: 만화, 또는 소설 페이지 방식(고치는 중이 아닐 때). 찾기 칸처럼 글을 입력하는 칸에 커서가 있으면 그 칸이 쓴다.</summary>
    private bool CanUseArrowPaging()
    {
        if (!(imageMode || (epubMode && epubPageMode && !epubEditable))) return false;
        Control focused = this;
        while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
        return focused is not TextBoxBase { ReadOnly: false };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        TryHandleFeatureKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>웹뷰(epub·PDF)에 포커스가 있으면 키 입력이 윈폼까지 안 올라오므로(F5는 웹뷰가 새로고침까지 해버린다) 웹뷰 쪽에서 가로챈다.</summary>
    private void HookWebViewKeys(WebView2 view)
    {
        // WinForms 래퍼(1.0.4191)는 CoreWebView2Controller를 공개하지 않아서, 안에 든 필드를 리플렉션으로 꺼내 쓴다.
        // 패키지 버전을 고정해 두었으니 안전하고, 혹시 실패하면(버전이 바뀌어 필드가 없어지면) epub은 문서 안 JS가
        // 같은 키를 대신 처리하고 PDF는 몰입 모드의 "마우스를 맨 위로" 탈출구가 남는다.
        try
        {
            var field = typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(view) is not CoreWebView2Controller controller) return;
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                var key = (Keys)(int)e.VirtualKey;
                if ((ModifierKeys & Keys.Control) != 0) key |= Keys.Control;
                if ((ModifierKeys & Keys.Shift) != 0 && key is Keys.OemOpenBrackets or Keys.OemCloseBrackets) return; // { } 는 넘기기 키가 아니다
                if (TryHandleFeatureKey(key)) e.Handled = true;
            };
        }
        catch { }
    }

    /// <summary>몰입 모드에서 마우스를 화면 맨 위 가장자리에 대면 메뉴가 잠깐 나타난다(키보드 없이도 빠져나올 수 있게).</summary>
    private void ImmersionHoverTick()
    {
        if (!immersive || MainMenuStrip == null) return;
        var menu = MainMenuStrip;
        var p = PointToClient(Cursor.Position);
        bool insideWindow = p.X >= 0 && p.X < ClientSize.Width;
        if (!toolbarShown)
        {
            if (insideWindow && p.Y >= 0 && p.Y <= 2 && IsHostActive) { SetToolbarVisible(true); RepositionOverlays(); }
        }
        else if (p.Y > (ToolbarVisibilityHost != null ? 0 : menu.Height) + 12 && !menu.Items.OfType<ToolStripMenuItem>().Any(i => i.DropDown.Visible))
        {
            SetToolbarVisible(false);
            RepositionOverlays();
        }
    }

    // ───────────────────────── 낭독(TTS) ─────────────────────────

    /// <summary>F5·"자동 재생 > 시작/정지". 만화는 슬라이드쇼를, 그 밖(소설)은 낭독을 켜고 끈다.</summary>
    private void ToggleAutoPlay()
    {
        if (imageMode) ToggleSlideshow();
        else ToggleTts();
    }

    private void ToggleTts()
    {
        if (ttsActive) StopTts(announce: true);
        else StartTts();
    }

    private void UpdateAutoPlayMenu()
    {
        if (autoPlayToggleMenuItem != null) autoPlayToggleMenuItem.Text = ttsActive || slideshowTimer != null ? "정지" : "시작";
        UpdateToolbar();
    }

    /// <summary>
    /// 낭독을 시작한다. fromOffset이 있으면 그 글자부터(우클릭 "여기서부터 낭독"), 없으면 드래그로 고른 자리나
    /// 지금 화면 맨 위부터 읽는다. txt는 전체 글 기준 위치, epub은 챕터 본문 기준 위치다.
    /// </summary>
    private async void StartTts(int? fromOffset = null)
    {
        if (ttsActive) StopTts();

        if (epubMode)
        {
            if (epubEditable) { ShowToast("읽기 전용 상태에서만 낭독할 수 있습니다"); return; }
            ttsActive = true;
            UpdateAutoPlayMenu();
            if (!await PrepareEpubTts()) { StopTts(); return; }
            if (!ttsActive) return;

            int start;
            if (fromOffset is int f) start = f;
            else
            {
                start = 0;
                try
                {
                    string json = await epubView!.CoreWebView2.ExecuteScriptAsync("window.__clrTts.firstVisible()");
                    int.TryParse(json, out start);
                }
                catch { }
            }
            if (!ttsActive) return;
            ttsPos = Math.Clamp(start, 0, ttsEpubText.Length);
            ShowToast("낭독 시작");
            SpeakNextEpub();
        }
        else if (editorHost.Visible)
        {
            if (!mainEditor.ReadOnly) { ShowToast("읽기 전용 상태에서만 낭독할 수 있습니다"); return; }
            if (fullText.Length == 0) return;

            int start;
            if (fromOffset is int f) start = f;
            else
            {
                // 보이는 곳에 표시된 선택(직접 고른 글, 또는 방금 읽던 문장)이 있으면 거기부터, 아니면 화면 맨 위부터.
                int baseOffset = pageMode ? currentPageStart : 0;
                bool selectionVisible = false;
                if (mainEditor.SelectionLength > 0)
                {
                    int y = mainEditor.GetPositionFromCharIndex(mainEditor.SelectionStart).Y;
                    selectionVisible = y >= 0 && y < mainEditor.ClientSize.Height;
                }
                start = selectionVisible ? baseOffset + mainEditor.SelectionStart : (pageMode ? currentPageStart : TxtTopOffset());
            }
            ttsActive = true;
            ttsPos = Math.Clamp(start, 0, fullText.Length);
            UpdateAutoPlayMenu();
            ShowToast("낭독 시작");
            SpeakNextTxt();
        }
        else
        {
            ShowToast("이 형식은 낭독을 지원하지 않습니다");
        }
    }

    private void StopTts(bool announce = false)
    {
        if (!ttsActive && !ttsAwaitingChapter) return;
        ttsActive = false;
        ttsAwaitingChapter = false;
        tts.Stop();
        ClearEpubTtsMark();
        UpdateAutoPlayMenu();
        if (announce) ShowToast("낭독 정지");
    }

    private void FinishTts(string message)
    {
        ttsActive = false;
        ttsAwaitingChapter = false;
        ClearEpubTtsMark();
        UpdateAutoPlayMenu();
        ShowToast(message);
    }

    private void OnTtsSentenceFinished()
    {
        if (!ttsActive) return;
        if (epubMode) SpeakNextEpub();
        else SpeakNextTxt();
    }

    /// <summary>txt: 다음 문장을 찾아 표시하고 읽는다. 화면(페이지)의 끝에 닿으면 다음 페이지로 넘긴 뒤 이어서 읽는다.</summary>
    private void SpeakNextTxt()
    {
        while (ttsActive)
        {
            int limit = pageMode ? currentPageEnd : fullText.Length;
            var sentence = TtsText.NextSentence(fullText, ttsPos, limit);
            if (sentence == null)
            {
                if (pageMode && currentPageEnd < fullText.Length)
                {
                    ttsInternalNav = true;
                    try { ChangePage(1); }
                    finally { ttsInternalNav = false; }
                    ttsPos = currentPageStart;
                    continue;
                }
                FinishTts("끝까지 읽었습니다");
                return;
            }

            var (start, length) = sentence.Value;
            ttsPos = start + length;
            HighlightTxt(start, length);

            string spoken = TtsText.Clean(fullText.Substring(start, length));
            if (!TtsText.HasSpeakable(spoken)) continue; // "* * *" 같은 구분선은 건너뛴다
            tts.Speak(spoken);
            return;
        }
    }

    private void HighlightTxt(int start, int length)
    {
        int local = start - (pageMode ? currentPageStart : 0);
        mainEditor.Select(local, length);
        if (pageMode) return; // 페이지 모드는 페이지 안에 다 보인다
        // 읽는 문장이 화면 아래쪽에 닿으면 그 줄을 맨 위로 올려서, 문장마다 한 줄씩 밀리지 않고 화면 단위로 넘어가게 한다.
        int y = mainEditor.GetPositionFromCharIndex(local).Y;
        if (y < 0 || y > mainEditor.ClientSize.Height * 0.7) ScrollCharToTop(local);
    }

    /// <summary>epub: 챕터에 낭독 스크립트가 있는지 확인하고 본문 텍스트를 받아온다.</summary>
    private async Task<bool> PrepareEpubTts()
    {
        if (epubView?.CoreWebView2 == null) return false;
        try
        {
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubTtsScript); // 이미 심겨 있으면 그대로 둔다
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTts.text()");
            ttsEpubText = JsonSerializer.Deserialize<string>(json) ?? "";
            return true;
        }
        catch { return false; }
    }

    private async void SpeakNextEpub()
    {
        while (ttsActive)
        {
            var sentence = TtsText.NextSentence(ttsEpubText, ttsPos, ttsEpubText.Length);
            if (sentence == null)
            {
                // 이 챕터를 다 읽었다 — 다음 챕터가 있으면 넘어가서 처음부터 이어 읽는다(글이 없는 이미지 챕터는 자동으로 건너뛰게 된다).
                if (epubBook != null && epubChapterIndex + 1 < epubBook.Chapters.Count)
                {
                    ttsAwaitingChapter = true;
                    ttsInternalNav = true;
                    try { ShowEpubChapter(epubChapterIndex + 1); }
                    finally { ttsInternalNav = false; }
                    return;
                }
                FinishTts("끝까지 읽었습니다");
                return;
            }

            var (start, length) = sentence.Value;
            ttsPos = start + length;
            try { await epubView!.CoreWebView2.ExecuteScriptAsync($"window.__clrTts.mark({start},{start + length})"); }
            catch { }
            if (!ttsActive) return;

            string spoken = TtsText.Clean(ttsEpubText.Substring(start, length));
            if (!TtsText.HasSpeakable(spoken)) continue;
            tts.Speak(spoken);
            return;
        }
    }

    private void ClearEpubTtsMark()
    {
        if (epubMode) RunEpubScript("window.__clrTts && window.__clrTts.clear();");
    }

    /// <summary>
    /// epub 문서 안에서 낭독을 돕는 스크립트. 본문의 글자 노드를 이어 붙인 텍스트(text)와 그 안의 위치 ↔ 화면 위치 대응을
    /// 만든다. 읽는 문장은 DOM을 건드리지 않는 CSS Custom Highlight로 표시해서(수정 모드에서 저장하는 HTML에 섞이지 않게),
    /// 필요하면 그 문장이 있는 페이지/스크롤 위치로 화면을 옮긴다. 문서(챕터)마다 새로 심어야 한다.
    /// </summary>
    private const string EpubTtsScript = @"
(function(){
  if (window.__clrTts) return;
  var nodes = [], full = '';
  var BLOCK = 'p,div,li,h1,h2,h3,h4,h5,h6,blockquote,section,article,tr,td,th,pre,dd,dt';

  function collect() {
    nodes = []; full = '';
    var lastBlock = null;
    var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
    var n;
    while ((n = w.nextNode())) {
      var p = n.parentElement;
      if (!p) continue;
      var tag = p.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'RT' || tag === 'RP') continue;
      var v = n.nodeValue;
      if (!v) continue;
      var blk = p.closest(BLOCK) || document.body;
      var brBefore = n.previousSibling && n.previousSibling.nodeName === 'BR';
      // 문단이 바뀌거나 줄바꿈(BR)이 있으면 문장도 거기서 끊기도록 줄바꿈 문자를 끼워 넣는다.
      if (full.length > 0 && (blk !== lastBlock || brBefore)) full += '\n';
      lastBlock = blk;
      nodes.push({ n: n, s: full.length, l: v.length });
      full += v;
    }
    return full;
  }

  function locate(off) {
    var lo = 0, hi = nodes.length - 1, ans = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (nodes[mid].s <= off) { ans = mid; lo = mid + 1; } else { hi = mid - 1; }
    }
    if (ans < 0) return null;
    var e = nodes[ans];
    if (off >= e.s + e.l) {
      if (ans + 1 < nodes.length) return { n: nodes[ans + 1].n, o: 0 };
      return { n: e.n, o: e.l };
    }
    return { n: e.n, o: off - e.s };
  }

  function rangeOf(start, end) {
    var a = locate(start), b = locate(end > start ? end - 1 : end);
    if (!a || !b) return null;
    var r = document.createRange();
    r.setStart(a.n, a.o);
    r.setEnd(b.n, Math.min(b.n.nodeValue.length, b.o + (end > start ? 1 : 0)));
    return r;
  }

  var st = document.createElement('style');
  st.id = '__clr_tts_style';
  st.textContent = '::highlight(clr-tts) { background-color: #ffe066; color: #000000; }';
  document.head.appendChild(st);

  function ensureVisible(r) {
    var rc = r.getBoundingClientRect();
    if (window.__clrGoTo && window.__clrTotalPages !== undefined) {
      // 페이지 모드: body를 translateX로 밀고 있으니, 밀린 만큼(전환 애니메이션 중간값 포함)을 빼서 원래 x 위치를 구한다.
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      var page = Math.floor((rc.left - tx) / window.__clrPageWidth);
      if (page !== window.__clrPageIndex) window.__clrGoTo(page);
    } else if (rc.top < 0 || rc.bottom > window.innerHeight) {
      window.scrollTo(0, window.scrollY + rc.top - window.innerHeight * 0.25);
    }
  }

  function mark(start, end) {
    var r = rangeOf(start, end);
    if (!r) return;
    try { CSS.highlights.set('clr-tts', new Highlight(r)); } catch (e) {}
    ensureVisible(r);
  }

  function clear() { try { CSS.highlights.delete('clr-tts'); } catch (e) {} }

  // 지금 화면에 보이는 첫 글자의 위치 — 스크롤 모드든 페이지 모드든 똑같이, 화면 안에 들어온 첫 글자를 찾는다.
  function firstVisible() {
    collect();
    var vw = window.innerWidth, vh = window.innerHeight;
    var r = document.createRange();
    for (var i = 0; i < nodes.length; i++) {
      var e = nodes[i];
      if (!e.l || !/\S/.test(e.n.nodeValue)) continue;
      r.selectNodeContents(e.n);
      var b = r.getBoundingClientRect();
      if (b.bottom < 0 || b.top > vh || b.right < 0 || b.left > vw) continue;
      for (var o = 0; o < e.l; o += 8) {
        r.setStart(e.n, o); r.setEnd(e.n, Math.min(e.l, o + 1));
        var c = r.getBoundingClientRect();
        if (c.width === 0 && c.height === 0) continue;
        if (c.left >= -1 && c.top >= -1 && c.top < vh && c.left < vw) return e.s + o;
      }
    }
    return 0;
  }

  function offsetAt(x, y) {
    collect();
    var rg = document.caretRangeFromPoint ? document.caretRangeFromPoint(x, y) : null;
    if (!rg) return -1;
    for (var i = 0; i < nodes.length; i++) if (nodes[i].n === rg.startContainer) return nodes[i].s + rg.startOffset;
    return -1;
  }

  var api = { text: collect, firstVisible: firstVisible, mark: mark, clear: clear, lastCtx: -1 };
  document.addEventListener('contextmenu', function(e) { api.lastCtx = offsetAt(e.clientX, e.clientY); }, true);
  // 웹뷰 키 가로채기(HookWebViewKeys)가 안 먹는 경우를 위한 예비 — F5는 안 막으면 웹뷰가 챕터를 새로고침해버린다.
  document.addEventListener('keydown', function(e) {
    if (e.key === 'F5' || e.key === 'F11') {
      e.preventDefault();
      window.chrome.webview.postMessage(JSON.stringify({ type: 'key', key: e.key }));
    } else if ((e.key === ']' || e.key === '[') && !e.ctrlKey && !e.altKey && !e.metaKey && !(document.body && document.body.isContentEditable)) {
      // 다음(]) · 이전([) 파일 — 글을 고치는 중이 아닐 때만
      window.chrome.webview.postMessage(JSON.stringify({ type: 'key', key: e.key }));
    }
  });
  // Ctrl+휠: 브라우저 확대(웹뷰 기본 동작) 대신 글자 크기를 바꾼다.
  document.addEventListener('wheel', function(e) {
    if (!e.ctrlKey) return;
    e.preventDefault();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'zoom', delta: e.deltaY < 0 ? 1 : -1 }));
  }, { passive: false });
  window.__clrTts = api;
})();
";

    // ───────────────────────── Win32 ─────────────────────────

    private const int EM_SETMARGINS = 0x00D3, EM_LINEFROMCHAR = 0x00C9, EM_GETFIRSTVISIBLELINE = 0x00CE,
        EM_LINESCROLL = 0x00B6, EM_LINEINDEX = 0x00BB;
    private const int EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
