using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CoffeeViewer;

/// <summary>
/// 소설 · 만화 뷰어 겸 간단한 에디터.
/// txt는 읽을 때 epub과 같은 웹 화면(TxtWebBook)으로 보여주고, "수정하기"를 켰을 때만 파일 전체를 담는
/// 편집 가능한 텍스트박스(mainEditor)로 바꾼다. 맨 위는 메뉴바 대신 아이콘 툴바다(MainForm.Toolbar.cs).
/// 챕터 · 이미지 고르기와 위치 이동은 아래쪽 탐색 막대(보기 > 탐색, MainForm.Nav.cs)가 맡는다.
/// </summary>
public partial class MainForm : Form
{
    private readonly PageLockTextBox mainEditor;
    private readonly Panel editorHost; // mainEditor를 감싸는 자리(Dock=Fill). 화살표는 폭을 줄이지 않고 이 위에 그냥 겹쳐 그린다.
    private readonly Panel epubHost; // epubView를 감싸는 자리(Dock=Fill). 마찬가지로 화살표는 그 위에 겹쳐 그린다.
    private readonly Panel imageHost; // imageCanvas(만화/이미지 뷰어)를 담는다.
    private readonly Panel pdfHost; // pdfView를 감싸는 자리(Dock=Fill).
    private readonly ImageCanvas imageCanvas;
    private readonly Panel contentHost; // editorHost(txt)·epubHost(epub)·imageHost(만화)·pdfHost(pdf) 중 하나만 보여준다.
    private readonly Label statusLabel;

    private readonly ToolStripMenuItem showImagesMenuItem;
    // 화면 반쪽 탭 넘기기 여부는 "읽기 방식" 메뉴 안에 "페이지 터치"로 들어가 있다(스크롤/페이지/페이지
    // 터치 세 개 중 하나) — 만화(이미지)는 스크롤 개념만 없을 뿐, 페이지/페이지 터치는 그대로 고른다.
    private bool tapToTurnEnabled = true;
    private ContentKind? shownKind; // 지금 보여주는 종류(아직 아무것도 안 열었으면 null) — 종류가 바뀔 때만 기본 읽기 방식을 적용하려고(SetContentMode)
    private readonly List<ToolStripMenuItem> fontSizeMenuItems = new();
    private readonly List<ToolStripItem> editOnlyMenuItems = new();
    private readonly List<ToolStripItem> txtOnlyMenuItems = new(); // epub을 보는 중엔 잠가둘 mainEditor 전용 메뉴들
    private readonly ToolStripMenuItem editModeMenuItem;
    private readonly ToolStripMenuItem saveMenuItem;
    private readonly ToolStripMenuItem saveAsMenuItem;
    private readonly ToolStripMenuItem findMenuItem;
    private readonly ToolStripMenuItem replaceMenuItem;
    private readonly ToolStripMenuItem readingModeMenu;
    private readonly ToolStripMenuItem fontSizeMenu;
    private readonly ToolStripMenuItem orderMenu;
    private readonly ToolStripMenuItem layoutMenu;
    private readonly ToolStripMenuItem viewModeMenu;
    private readonly ToolStripMenuItem slideshowMenu;
    private readonly ToolStripMenuItem endOfBookMenu;
    private readonly ToolStripMenuItem editMenu;

    private List<TxtChapter> chapters = new();
    private string? currentPath;
    private float fontSize = 11f;

    private readonly FindBar findBar;
    private readonly LoadingOverlay loadingOverlay; // 큰 파일을 불러오는 동안 게이지 + 남은 시간 추정을 보여준다
    private CancellationTokenSource? loadCts; // 지금 진행 중인 불러오기 하나당 하나 — 게이지의 "취소"가 이걸 취소시킨다

    // ── epub ──
    private WebView2? epubView;
    private EpubBook? epubBook;
    private bool epubMode = false;
    private int epubChapterIndex = 0;
    private bool lastKindDirect; // 직전 화면이 바로 쓰는 txt였는지(다음 epub이 기본 읽기 방식으로 시작하게)
    private int epubPageIdx, epubPageTotal; // 페이지 방식에서 지금 챕터의 현재/전체 페이지(네비게이션 바 숫자용)
    private bool epubPageMode = false; // epub 전용 읽기 방식(스크롤/페이지) — txt의 pageMode와는 별개
    private bool epubPendingStartAtLastPage = false; // "이전 챕터"로 넘어갈 때, 새 챕터의 마지막 페이지부터 시작
    private bool epubEditable = false; // epub 수정하기 on/off (document.body.contentEditable)
    private string? epubExtractDir; // epub을 통째로 풀어둔 임시 폴더 — 저장할 때 이걸 다시 zip으로 묶는다

    // ── pdf ──
    // WebView2(크로미움 엔진)에 로컬 파일을 그대로 navigate시키면 엣지와 똑같은 내장 PDF 뷰어(페이지 넘기기·
    // 확대/축소·검색·인쇄까지 자체 툴바로 다 제공)가 뜬다. epub처럼 우리가 직접 다단/페이지 로직을 만들
    // 필요가 없다 — PDF는 애초에 고정 레이아웃이라 그런 재배치가 안 맞기도 하고.
    private WebView2? pdfView;

    // ── 만화/이미지(zip 또는 이미지 파일) ──
    private bool imageMode = false;
    private string imageTitleBase = ""; // 만화 모드 제목표시줄에 쓰는 책/파일 이름("[3/120] 이름 — {AppShared.AppDisplayName}"의 "이름")
    private IImageSet? imageSet;
    private int imageIndex = 0; // imagePages 안에서의 위치(실제 파일 순서가 아니라 화면에 넘겨 보여줄 페이지 순서)

    /// <summary>옵시디언 플러그인의 "순서 / 나누기" 설정과 동일.</summary>
    private enum ReadingOrder { Ltr, Rtl, LtrSpread, RtlSpread }
    // Pair = 두쪽 보기: ItemIndex번 그림과 그 다음 그림을 나란히 이어 붙인 한 화면
    private enum ImageHalf { None, Left, Right, Pair }
    private bool imageDouble; // 두쪽 보기 여부(설정 ImageDoublePage와 같은 값)
    private readonly List<ToolStripMenuItem> imageLayoutMenuItems = new();
    private ReadingOrder imageOrder = ReadingOrder.Ltr;
    private readonly List<(int ItemIndex, ImageHalf Half)> imagePages = new();
    private readonly List<ToolStripMenuItem> imageOrderMenuItems = new();
    private readonly List<ToolStripMenuItem> imageViewModeMenuItems = new();
    private readonly PageScrubberBar pageScrubberBar; // 만화 하단의 위치 막대(꿀뷰처럼 드래그로 페이지 이동)

    // ── 슬라이드쇼(만화 자동 넘김) ──
    private System.Windows.Forms.Timer? slideshowTimer;
    private int slideshowIntervalSeconds = 3;
    private readonly List<ToolStripMenuItem> slideshowIntervalMenuItems = new();

    // ── 마지막 페이지에서(화살표로 더 넘기려 하거나 슬라이드쇼가 끝에 닿았을 때) ──
    private enum EndOfBookAction { Stop, Ask, NextFile }
    private EndOfBookAction endOfBookAction = EndOfBookAction.Ask;
    private readonly List<ToolStripMenuItem> endOfBookMenuItems = new();

    // ── 이미지 미리 불러오기(프리페치) 캐시 ──
    // 화살표로 넘기기 전에 앞뒤 몇 장을 백그라운드 스레드에서 미리 디코딩해둬서, 실제로 넘기는 순간엔
    // 압축 풀기·디코딩을 기다리지 않고 바로 뜨게 한다. imageSet.Load()는 zip 안 공유 스트림을 건드리므로
    // 여러 스레드가 동시에 부르면 깨질 수 있어, 실제 로딩은 항상 imageLoadLock으로 한 번에 하나씩만 실행한다.
    private readonly object imageLoadLock = new();
    private readonly object imageCacheLock = new(); // imagePageCache/pendingPrefetch 자체를 보호(짧게만 잡는다)
    private readonly Dictionary<int, Image> imagePageCache = new();
    private readonly HashSet<int> pendingPrefetch = new(); // 지금 백그라운드에서 디코딩 중인 페이지 번호 — 같은 페이지를 중복으로 예약하지 않으려고 둔다
    private const int PrefetchAhead = 3; // 다음 방향으로 미리 불러올 장 수
    private const int PrefetchBehind = 1; // 이전 방향으로 미리 불러올 장 수
    private const int CacheKeepRadius = 5; // 지금 페이지에서 이 범위를 벗어난 캐시는 정리한다(메모리 절약)

    // ── 읽기 방식(스크롤/페이지) ──
    private readonly ToolStripMenuItem scrollModeMenuItem;
    private readonly ToolStripMenuItem pageModeMenuItem;
    private readonly ToolStripMenuItem pageTouchModeMenuItem;
    private readonly Panel pageNavPanel;
    private readonly Label pageIndicatorLabel;
    // txt/epub/만화 공용 — 본문 위에 그대로 얹은 동그란 넘김 버튼(사각 배경 없이 본문/그림 위에 뜬다).
    // 자리만 차지하고 폭을 줄이지 않도록, editorHost/epubHost에 여백을 주지 않고 그냥 맨 위에 겹쳐 그린다.
    // 평소엔 숨겨두고, 마우스가 버튼 근처에 오면 그쪽 버튼만 나타난다(ArrowHoverTick).
    private readonly ArrowOverlayButton prevNavBtn;
    private readonly ArrowOverlayButton nextNavBtn;
    private readonly System.Windows.Forms.Timer arrowHoverTimer = new() { Interval = 80 };
    private DateTime prevNavLastNear, nextNavLastNear;
    // 화면 반쪽 탭으로 페이지 넘기기(txt page 모드) 판정용 — MouseDown 때 좌표를 담아뒀다가 MouseUp에서
    // 얼마나 끌렸는지 비교한다. 6px는 ImageCanvas의 TapMoveThreshold와 맞춘 값.
    private Point mainEditorTapDownLocation;
    private const int TapMoveThresholdSq = 6 * 6;
    private readonly System.Windows.Forms.Timer mainEditorTapTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private int pendingMainEditorTapDelta;
    private string fullText = ""; // 페이지 모드에서는 mainEditor.Text가 현재 페이지만 담으므로, 전체는 따로 들고 있는다.
    private bool pageMode = false;
    private int currentPageStart = 0;
    private int currentPageEnd = 0;

    // ── txt 웹 화면 ──
    // 읽기 전용 txt는 메모장 같은 mainEditor 대신 epubView 위에 "가상 epub"(TxtWebBook)으로 띄운다 — 줄 간격·
    // 문단 간격·다단 페이지·테마·낭독을 epub과 같은 길로 쓴다. mainEditor는 "수정하기"를 켰을 때만 쓴다.
    // 이때 epubMode도 같이 켜져 있으므로, txt와 epub을 갈라야 하는 곳은 txtWeb을 먼저 본다.
    private bool txtWeb;
    private TxtWebBook? txtBook;
    private bool epubDocReady; // 지금 epubView에 뜬 문서에 스크립트까지 다 심겼는지(같은 문서 안 이동은 다시 불러오지 않으려고)
    private int? txtPendingOffset; // 문서가 다 뜬 직후 이 원본 위치로 옮긴다
    private (int Start, int Length)? txtPendingSelect; // 문서가 다 뜬 직후 이 구간을 찾은 글로 표시한다
    private int txtWebTopOffset; // 웹 화면 맨 위 줄의 원본 위치(QueryTxtTop이 주기적으로 읽어 둔다)
    private int txtFindStart, txtFindEnd; // 웹 화면에서 마지막으로 찾은 구간(찾기 바의 "다음 · 이전" 기준)

    /// <summary>서재 창 안에 넣어 쓰는 읽기 화면인지(LibraryForm.OpenBook). 툴바 맨 앞에 [‹ 서재]가 생기고, 서재로 돌아가기 ·
    /// 몰입 모드 · 창 활성 여부를 서재 창 기준으로 처리한다.</summary>
    private readonly bool embedded;

    /// <summary>[‹ 서재] · Ctrl+L — 서재 안에 들어가 있을 때 목록 화면으로 돌아가 달라는 요청.</summary>
    public event Action? BackToLibraryRequested;

    public MainForm(string? initialPath, bool embedded = false)
    {
        this.embedded = embedded;
        AutoScaleMode = AutoScaleMode.None; // DPI 배율에 따른 좌표 재계산으로 컨트롤이 겹치는 걸 방지
        Icon = AppShared.LoadAppIcon(); // exe에 심어둔 아이콘(csproj의 ApplicationIcon)을 그대로 창/작업표시줄에도 사용
        Text = AppShared.AppDisplayName;
        Width = 900;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        mainEditor = new PageLockTextBox
        {
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("맑은 고딕", fontSize),
            MaxLength = int.MaxValue,
            AcceptsTab = true,
            ReadOnly = true, // 기본값: 읽기 전용. "편집" 버튼을 눌러야 고칠 수 있다.
        };
        editorHost = new Panel { Dock = DockStyle.Fill };
        editorHost.Controls.Add(mainEditor);
        epubHost = new Panel { Dock = DockStyle.Fill, Visible = false }; // epubView는 나중에(비동기 초기화 후) 여기 넣는다
        imageCanvas = new ImageCanvas { Dock = DockStyle.Fill };
        ApplyImageMargins();
        imageDouble = settings.ImageDoublePage;
        imageCanvas.PageRequested += delta => ChangeImage(delta);
        imageCanvas.ZoomPercentChanged += pct => { if (imageMode) ShowZoomBubble($"{pct}%"); }; // 원본 대비 몇 %인지(100% = 원본 크기)
        imageHost = new Panel { Dock = DockStyle.Fill, Visible = false };
        imageHost.Controls.Add(imageCanvas);
        pdfHost = new Panel { Dock = DockStyle.Fill, Visible = false }; // pdfView는 나중에(비동기 초기화 후) 여기 넣는다
        contentHost = new Panel { Dock = DockStyle.Fill };
        contentHost.Controls.Add(editorHost);
        contentHost.Controls.Add(epubHost);
        contentHost.Controls.Add(imageHost);
        contentHost.Controls.Add(pdfHost);

        statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        // 페이지 모드일 때만 보이는 위치 표시줄(하단, 가운데 "약 N% 지점"). 만화(이미지) 모드에서는
        // 이 자리에 pageIndicatorLabel 대신 pageScrubberBar(꿀뷰 스타일 위치 막대)를 보여준다.
        pageIndicatorLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        pageScrubberBar = new PageScrubberBar { Dock = DockStyle.Fill, Visible = false };
        pageScrubberBar.PageSeek += pageIndex => ShowImagePage(pageIndex);
        pageNavPanel = new Panel { Dock = DockStyle.Bottom, Height = 36, Visible = false };
        pageNavPanel.Controls.Add(pageIndicatorLabel);
        pageNavPanel.Controls.Add(pageScrubberBar);

        // 옵시디언 플러그인처럼, 페이지 넘김은 본문 좌우 가장자리에 뜨는 화살표로 — txt/epub/만화 모두 공용.
        prevNavBtn = new ArrowOverlayButton(pointsLeft: true) { Visible = false };
        nextNavBtn = new ArrowOverlayButton(pointsLeft: false) { Visible = false };
        prevNavBtn.Activated += () => { if (imageMode) ChangeImage(-1); else if (epubMode) EpubChangePage(-1); else ChangePage(-1); };
        nextNavBtn.Activated += () => { if (imageMode) ChangeImage(1); else if (epubMode) EpubChangePage(1); else ChangePage(1); };
        arrowHoverTimer.Tick += (_, _) => ArrowHoverTick();

        // txt 페이지 모드(읽기 전용일 때만): 화면을 셋으로 나눠서 왼쪽을 탭하면 뒤로, 오른쪽을
        // 탭하면 앞으로, 가운데는 무시(TapZone) — 화살표를 정확히 안 눌러도 되는, 터치에 훨씬 편한 방식. 편집 중이거나
        // (커서를 옮기려는 클릭일 수 있으니) 스크롤 모드일 때는(페이지 개념이 없으니) 끄고 원래
        // 클릭 동작 그대로 둔다. 살짝이라도 끌었으면(텍스트를 선택하려던 것) 탭이 아니므로 무시한다.
        // 더블클릭(단어 선택)의 첫 번째 클릭까지 곧장 넘겨버리면 안 되니, 시스템 더블클릭 시간만큼
        // 기다렸다가 그사이 두 번째 클릭이 안 왔을 때만(=진짜 한 번 탭한 것으로 판명 났을 때) 넘긴다.
        mainEditorTapTimer.Tick += (_, _) => { mainEditorTapTimer.Stop(); ChangePage(pendingMainEditorTapDelta); };
        mainEditor.MouseDown += (_, e) => mainEditorTapDownLocation = e.Location;
        mainEditor.MouseUp += (_, e) =>
        {
            if (!tapToTurnEnabled || !pageMode || !mainEditor.ReadOnly) return;
            if (e.Clicks > 1) { mainEditorTapTimer.Stop(); return; } // 더블클릭의 두 번째 클릭 — 단어 선택은 원래대로 둔다
            int dx = e.X - mainEditorTapDownLocation.X, dy = e.Y - mainEditorTapDownLocation.Y;
            if (dx * dx + dy * dy > TapMoveThresholdSq) return;
            pendingMainEditorTapDelta = TapZone.Direction(e.X, mainEditor.Width);
            if (pendingMainEditorTapDelta == 0) return; // 가운데 칸은 넘기지 않는다
            mainEditorTapTimer.Stop();
            mainEditorTapTimer.Start();
        };
        // 만화/이미지는 ImageCanvas가 자기 안에서 같은 방식(끌지 않고 반쪽 탭)으로 직접 처리해서
        // PageRequested로 알려준다(위 134번째 줄에서 ChangeImage로 연결). epub은 WebView2 안에서
        // JS로 처리한다(EpubPaginateScript).

        // 페이지 모드에서는 방향키로도 앞뒤 페이지를 넘긴다(옵시디언 플러그인과 동일).
        mainEditor.KeyDown += (_, e) =>
        {
            if (!pageMode) return;
            if (e.KeyCode == Keys.Left) { ChangePage(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Right) { ChangePage(1); e.Handled = true; e.SuppressKeyPress = true; }
            // 페이지 방식에는 스크롤이 없다 — PgUp/PgDn/Space는 페이지 넘김으로, 그 밖의 스크롤 키는 막는다.
            else if (e.KeyCode == Keys.PageDown || (e.KeyCode == Keys.Space && !e.Shift)) { ChangePage(1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.PageUp || (e.KeyCode == Keys.Space && e.Shift)) { ChangePage(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (!e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)) { e.Handled = true; e.SuppressKeyPress = true; }
        };

        // 윈도우 11 메모장처럼, 본문 위에 떠 있는 찾기 바(알약 모양). 본문 폭은 안 건드린다.
        // 페이지 모드에서는 mainEditor.Text가 지금 페이지 조각만 담고 있으므로, 찾기는 항상
        // fullText(전체 글) 기준으로 검색하고 찾은 위치가 다른 페이지에 있으면 그 페이지로 이동한다.
        // txt 웹 화면에서는 찾기도 전체 글(fullText) 기준으로 하고, 처음 찾을 땐 지금 보이는 자리부터 찾는다.
        findBar = new FindBar(mainEditor,
            getText: () => pageMode || txtWeb ? fullText : mainEditor.Text,
            getSelectionStart: () => txtWeb ? (txtFindEnd > 0 ? txtFindStart : txtWebTopOffset)
                : pageMode ? currentPageStart + mainEditor.SelectionStart : mainEditor.SelectionStart,
            getSelectionEnd: () => txtWeb ? (txtFindEnd > 0 ? txtFindEnd : txtWebTopOffset)
                : pageMode ? currentPageStart + mainEditor.SelectionStart + mainEditor.SelectionLength
                : mainEditor.SelectionStart + mainEditor.SelectionLength,
            selectRange: SelectInDocument,
            focusBody: () => { if (txtWeb) epubView?.Focus(); else mainEditor.Focus(); }
        ) { Visible = false };

        loadingOverlay = new LoadingOverlay { Visible = false };
        // 취소 버튼이 눌렸을 때 실제로 멈추는 일 — 이미 끝났거나 막 끝나려는 참이면 cts가 먼저
        // Dispose됐을 수 있어서, 그 경우(ObjectDisposedException)는 그냥 무시한다.
        loadingOverlay.CancelRequested += () => { try { loadCts?.Cancel(); } catch (ObjectDisposedException) { } };

        // ── 메뉴: 파일 / 편집 / 보기 ──
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("파일");
        // 단축키 있는 메뉴는 ScopedMenuItem — 볼트 창의 탭마다 메뉴가 따로 있어서, 지금 보이는 탭의 메뉴만 단축키를 받게 한다.
        fileMenu.DropDownItems.Add(new ScopedMenuItem("열기...", (_, _) => OpenFileDialog(), () => Visible) { ShortcutKeys = Keys.Control | Keys.O });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        saveMenuItem = new ScopedMenuItem("저장", (_, _) => SaveFile(false), () => Visible) { ShortcutKeys = Keys.Control | Keys.S };
        saveAsMenuItem = new ToolStripMenuItem("다른 이름으로 저장...", null, (_, _) => SaveFile(true));
        fileMenu.DropDownItems.Add(saveMenuItem);
        fileMenu.DropDownItems.Add(saveAsMenuItem);

        editMenu = new ToolStripMenuItem("편집");
        // txt든 epub이든 "수정하기"는 같은 메뉴를 쓰고, 지금 어느 쪽을 보고 있는지에 따라 SetEditMode가 알아서 나눠 보낸다.
        editModeMenuItem = new ToolStripMenuItem("수정하기", null, (_, _) => SetEditMode(epubMode && !txtWeb ? !epubEditable : mainEditor.ReadOnly));
        // 실행 취소 · 잘라내기 · 복사 · 붙여넣기 · 모두 선택은 메뉴에 단축키를 걸지 않고(표시만) 포커스 있는 글 상자가 직접 처리하게 둔다.
        // 메뉴에 걸면 탭이 여럿일 때 다른(숨은) 탭의 본문으로 가버리고, 찾기 칸 · 검색 칸에서 누른 Ctrl+V도 본문에 붙었다.
        var undoItem = new ToolStripMenuItem("실행 취소", null, (_, _) => mainEditor.Undo()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        var cutItem = new ToolStripMenuItem("잘라내기", null, (_, _) => mainEditor.Cut()) { ShortcutKeyDisplayString = "Ctrl+X" };
        var copyItem = new ToolStripMenuItem("복사", null, (_, _) => mainEditor.Copy()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var pasteItem = new ToolStripMenuItem("붙여넣기", null, (_, _) => mainEditor.Paste()) { ShortcutKeyDisplayString = "Ctrl+V" };
        var deleteItem = new ToolStripMenuItem("삭제", null, (_, _) => { if (mainEditor.SelectionLength > 0) mainEditor.SelectedText = ""; });
        var selectAllItem = new ToolStripMenuItem("모두 선택", null, (_, _) => mainEditor.SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" };
        findMenuItem = new ScopedMenuItem("찾기", (_, _) => findBar.Show(), () => Visible) { ShortcutKeys = Keys.Control | Keys.F };
        replaceMenuItem = new ScopedMenuItem("바꾸기", (_, _) => findBar.ShowReplace(), () => Visible) { ShortcutKeys = Keys.Control | Keys.H };
        editMenu.DropDownItems.Add(editModeMenuItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(undoItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(cutItem);
        editMenu.DropDownItems.Add(copyItem);
        editMenu.DropDownItems.Add(pasteItem);
        editMenu.DropDownItems.Add(deleteItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(findMenuItem);
        editMenu.DropDownItems.Add(replaceMenuItem);
        editMenu.DropDownItems.Add(selectAllItem);
        // 실행취소/잘라내기/붙여넣기/삭제는 읽기 전용 상태에서는 의미가 없으니 편집 모드일 때만 활성화한다.
        editOnlyMenuItems.AddRange(new ToolStripItem[] { undoItem, cutItem, pasteItem, deleteItem });
        // 찾기/복사/모두 선택은 mainEditor를 대상으로 하는 기능이라, epub을 보는 중엔 의미가 없어서 잠가둔다
        // (epub 안에서 직접 클릭해서 선택하고 Ctrl+C로 복사하는 건 브라우저가 알아서 처리한다).
        txtOnlyMenuItems.AddRange(new ToolStripItem[] { findMenuItem, replaceMenuItem, copyItem, selectAllItem });

        var viewMenu = new ToolStripMenuItem("보기");
        showImagesMenuItem = new ToolStripMenuItem("이미지 표시", null, (_, _) => ApplyShowImages()) { CheckOnClick = true, Checked = true };
        viewMenu.DropDownItems.Add(showImagesMenuItem); // 맨 위 "탐색"은 InitNavBar가 끼워 넣는다
        viewMenu.DropDownItems.Add(new ToolStripSeparator());

        // 스크롤/페이지/페이지 터치 — 셋 중 하나. "페이지 터치"를 고르면 화면 반쪽을 탭해서 넘길 수 있고,
        // 화살표 버튼은 그 대신 숨긴다(누를 자리가 화면 전체이니 굳이 작은 화살표가 따로 있을 필요가
        // 없다). 만화(이미지)는 스크롤 개념이 없어서 그 항목만 잠근다(아래 SetContentMode).
        readingModeMenu = new ToolStripMenuItem("읽기 방식");
        scrollModeMenuItem = new ToolStripMenuItem("스크롤", null, (_, _) => SetReadingMode(ReadingMode.Scroll)) { Checked = true };
        pageModeMenuItem = new ToolStripMenuItem("페이지", null, (_, _) => SetReadingMode(ReadingMode.Page));
        pageTouchModeMenuItem = new ToolStripMenuItem("페이지 터치", null, (_, _) => SetReadingMode(ReadingMode.PageTouch));
        readingModeMenu.DropDownItems.Add(scrollModeMenuItem);
        readingModeMenu.DropDownItems.Add(pageModeMenuItem);
        readingModeMenu.DropDownItems.Add(pageTouchModeMenuItem);
        viewMenu.DropDownItems.Add(readingModeMenu);

        fontSizeMenu = new ToolStripMenuItem("글자 크기");
        // 실제 키 처리는 TryHandleFeatureKey(Ctrl+- / Ctrl++ / Ctrl+0)와 Ctrl+휠이 맡고, 여기는 눌러서도 쓰고 단축키를 알려주는 용도.
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 확대", null, (_, _) => ViewZoomBy(1)) { ShortcutKeyDisplayString = "Ctrl++" });
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 축소", null, (_, _) => ViewZoomBy(-1)) { ShortcutKeyDisplayString = "Ctrl+-" });
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 배율 100%", null, (_, _) => ViewZoomReset()) { ShortcutKeyDisplayString = "Ctrl+0" });
        fontSizeMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int size in new[] { 10, 11, 12, 14, 16, 18, 20, 24, 28, 32 })
        {
            var item = new ToolStripMenuItem($"{size}pt", null, (_, _) => SetFontSize(size)) { Checked = size == (int)fontSize };
            fontSizeMenuItems.Add(item);
            fontSizeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(fontSizeMenu);

        // 옵시디언 Image & Zip Viewer 플러그인의 "순서 / 나누기"와 동일 — 만화(zip/이미지) 모드에서만 쓰인다.
        orderMenu = new ToolStripMenuItem("순서 / 나누기");
        AddImageOrderItem(orderMenu, "왼쪽 → 오른쪽", ReadingOrder.Ltr);
        AddImageOrderItem(orderMenu, "오른쪽 → 왼쪽 (일본식)", ReadingOrder.Rtl);
        AddImageOrderItem(orderMenu, "왼쪽 → 오른쪽 (2개로 나누기)", ReadingOrder.LtrSpread);
        AddImageOrderItem(orderMenu, "오른쪽 → 왼쪽 (2개로 나누기)", ReadingOrder.RtlSpread);
        viewMenu.DropDownItems.Add(orderMenu);

        // 만화(이미지) 모드 전용 — 한 번에 한 장(한쪽 보기) / 두 장을 나란히(두쪽 보기).
        layoutMenu = new ToolStripMenuItem("보기 방식");
        foreach (var (label, dbl) in new[] { ("한쪽 보기", false), ("두쪽 보기", true) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => ChooseImageLayout(dbl)) { Tag = dbl, Checked = dbl == settings.ImageDoublePage };
            imageLayoutMenuItems.Add(item);
            layoutMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(layoutMenu);

        // 만화(이미지) 모드 전용 — 이미지를 창에 맞추는 네 가지 방식.
        viewModeMenu = new ToolStripMenuItem("보기 모드");
        AddImageViewModeItem(viewModeMenu, "원본 크기로 보기", ImageViewMode.ActualSize);
        AddImageViewModeItem(viewModeMenu, "꽉차게 보기", ImageViewMode.FitWindow);
        AddImageViewModeItem(viewModeMenu, "꽉차게 보기 (폭 맞춤)", ImageViewMode.FitWidth);
        AddImageViewModeItem(viewModeMenu, "작은 그림도 꽉차게 보기", ImageViewMode.FitWindowUpscale);
        viewMenu.DropDownItems.Add(viewModeMenu);

        // 만화(이미지) 모드 전용 — 슬라이드쇼(일정 간격으로 자동 넘김)의 간격. 시작/정지는 낭독과 같은 F5 하나로
        // 묶여 있어서(자동 재생 메뉴, MainForm.Features.cs) 여기서는 메뉴만 만들고, 붙이는 건 그쪽에서 한다.
        slideshowMenu = new ToolStripMenuItem("만화 넘김 간격");
        foreach (int sec in new[] { 1, 2, 3, 5, 10 })
        {
            var item = new ToolStripMenuItem($"{sec}초마다", null, (_, _) => SetSlideshowInterval(sec)) { Checked = sec == slideshowIntervalSeconds };
            slideshowIntervalMenuItems.Add(item);
            slideshowMenu.DropDownItems.Add(item);
        }

        // 만화·소설(페이지 방식) — 마지막 페이지에서 더 넘기려 하면(또는 첫 페이지에서 더 되돌리려 하면) 옆 파일로 어떻게 할지.
        // 옆 파일이 없는 폴더의 끝 파일일 때는 설정창의 "처음 · 마지막 파일에서"가 정한다.
        endOfBookMenu = new ToolStripMenuItem("마지막 · 첫 페이지에서");
        AddEndOfBookItem(endOfBookMenu, "여기서 멈추기", EndOfBookAction.Stop);
        AddEndOfBookItem(endOfBookMenu, "안내 띄우기", EndOfBookAction.Ask);
        AddEndOfBookItem(endOfBookMenu, "자동으로 다음 · 이전 파일 열기", EndOfBookAction.NextFile);
        viewMenu.DropDownItems.Add(endOfBookMenu);

        menu.Items.Add(fileMenu);
        menu.Items.Add(editMenu);
        menu.Items.Add(viewMenu);
        menu.Items.Add(BuildFormatMenu()); // 파일 / 편집 / 보기 / 서식 / (자동 재생) / 도움말 — 뒤의 둘은 BuildFeatureMenus가 붙인다
        BuildFeatureMenus(menu, fileMenu, viewMenu); // 서재·테마·설정·낭독·도움말 (MainForm.Features.cs)

        // 메뉴바 오른쪽 끝에 "항상 위" 토글 — 드롭다운 없이 바로 눌러서 켜고 끈다.
        var alwaysOnTopItem = new ToolStripMenuItem("항상 위") { CheckOnClick = true, Alignment = ToolStripItemAlignment.Right };
        alwaysOnTopItem.CheckedChanged += (_, _) => { HostForm.TopMost = alwaysOnTopItem.Checked; UpdatePinLook(alwaysOnTopItem); }; // 탭 안에 들어 있으면 창(볼트 창)에 건다
        menu.Items.Add(alwaysOnTopItem);

        MainMenuStrip = menu;
        if (embedded) { menu.AutoSize = false; menu.Height = 0; } // 서재 안에서는 메뉴 줄을 안 쓴다(메뉴는 칸 머리의 ⋯로) — 남겨 두면 탭 밑에 빈 띠가 생긴다

        SetEditMode(false); // 시작은 항상 읽기 전용

        // WinForms는 Dock 컨트롤을 "나중에 추가한 것부터" 배치하므로,
        // Fill(editorHost) → Bottom(페이지 이동줄 → 상태바) → Top(메뉴) 순으로 추가해야
        // 위에서부터 [본문][페이지 이동줄][상태바] 순서로 쌓인다.
        // findBar는 Dock 없이 contentHost 위에 떠 있는 오버레이라 맨 나중에 추가해서 위로 올린다.
        Controls.Add(contentHost);
        Controls.Add(pageNavPanel);
        Controls.Add(statusLabel);
        InitStatusSegments();
        InitLookBar();
        Controls.Add(menu);
        Controls.Add(findBar);
        Controls.Add(prevNavBtn);
        Controls.Add(nextNavBtn);
        Controls.Add(loadingOverlay);
        InitNavBar(viewMenu, 0); // 테마를 입히는 InitFeatures보다 먼저 — 막대도 같이 칠해지게
        BuildToolbar(menu, alwaysOnTopItem);
        InitEdgePrompt();
        InitFeatures();
        InitHwp();
        InitTxtEdit();
        InitWriter();

        // 시작할 파일 열기는 Load 이후로 미룬다 — 생성자 도중(창 핸들이 아직 없어 Progress<T>가 UI
        // 스레드로 못 돌아오는 시점)에 곧장 열면, 이 파일이 커서 게이지가 뜨는 경우 진행률 보고가
        // 엉뚱한 스레드에서 컨트롤을 건드리게 될 수 있다.
        Load += (_, _) =>
        {
            RepositionOverlays();
            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                OpenPath(initialPath);
        };
        Resize += (_, _) => RepositionOverlays();
        // 프리페치 캐시에 들고 있던 이미지들이 창을 닫을 때 같이 정리되도록.
        FormClosed += (_, _) => { loadCts?.Cancel(); imageSet?.Dispose(); ClearImageCache(); StopSlideshow(); mainEditorTapTimer.Dispose(); arrowHoverTimer.Dispose(); };

        if (string.IsNullOrEmpty(initialPath) || !File.Exists(initialPath))
            statusLabel.Text = "파일 > 열기로 .txt / .epub / .pdf 파일을 선택하세요.";
    }

    /// <summary>확장자를 보고 txt/epub/pdf/zip(만화)/이미지 중 어느 쪽으로 열지 정한다(더블클릭, 명령줄 인자, 파일 열기 대화상자 공용).</summary>
    public void OpenPath(string path)
    {
        // 다른 책으로 넘어가기 전에, 읽던 자리를 기록해두고 낭독도 멈춘다.
        HideEdgePrompt();
        CapturePosition();
        StopTts();
        FlushAutoSave(); // 다른 파일로 가기 전에 고친 글을 저장(경로가 바뀐 뒤엔 엉뚱한 파일에 쓰이지 않게)
        // 슬라이드쇼 중이면 새 책이 뜰 때까지 타이머만 잠깐 세운다(큰 zip은 여는 동안에도 옛 책이 마지막 페이지라 다시 넘김이 일어나
        // 같은 파일을 또 여는 일이 없게). 새 만화가 뜨면 ShowImagePage가 다시 돌리고, 만화가 아니면 SetContentMode가 끝낸다.
        slideshowTimer?.Stop();
        if (hwpWatchedPath != null && !string.Equals(hwpWatchedPath, path, StringComparison.OrdinalIgnoreCase)) StopHwpWatch(); // 다른 책으로 가면 한글 저장 지켜보기도 끝
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (HwpDocument.IsHwpExtension(path)) LoadHwpFile(path); // 한글 문서: 글을 뽑아 txt처럼 (MainForm.Hwp.cs)
        else if (ext == ".epub") LoadEpubFile(path);
        else if (ext == ".pdf") LoadPdfFile(path);
        else if (ext == ".zip") LoadZip(path); // zip 안이 소설이면 소설로, 그림이면 만화로 (MainForm.Zip.cs)
        else if (ZipImageSet.IsImageExtension(path)) LoadSingleImageFile(path);
        else LoadFile(path);
    }

    // ───────────────────────── 파일 열기/저장 ─────────────────────────

    private void OpenFileDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "지원 파일 (*.txt;*.epub;*.pdf;*.hwp;*.hwpx;*.zip;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp)|*.txt;*.epub;*.pdf;*.hwp;*.hwpx;*.zip;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp" +
                "|텍스트 파일 (*.txt)|*.txt|EPUB (*.epub)|*.epub|PDF (*.pdf)|*.pdf|한글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx|zip (소설·만화) (*.zip)|*.zip|이미지 파일|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp|모든 파일|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenPath(dlg.FileName);
    }

    private async void LoadFile(string path)
    {
        string text;
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("파일을 여는 중…");
        try
        {
            var progress = new Progress<double>(loadingOverlay.Report);
            var (t, enc) = await Task.Run(() => ReadTextDetectEncoding(path, progress, cts.Token));
            text = t;
            txtSaveEncoding = enc;
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"파일을 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }

        ShowLoadedText(path, text, prepared: null, fromZip: false);
    }

    /// <summary>
    /// 읽어들인 글을 화면에 올린다(txt 파일과 zip 안의 소설이 함께 쓴다). prepared는 zip 소설처럼 이미
    /// 줄바꿈 정리와 챕터 나누기를 마친 경우에 넘기고, 없으면 여기서 정리하고 나눈다.
    /// </summary>
    private void ShowLoadedText(string path, string text, List<TxtChapter>? prepared, bool fromZip)
    {
        // WinForms TextBox(Win32 Edit 컨트롤)는 줄바꿈을 \r\n으로 넣어야 확실히 인식한다.
        // 파일이 \n만 쓰는 유닉스/맥 방식이면 그냥 다 붙어 보이므로, 어떤 방식이든 \r\n으로 통일한다.
        if (prepared == null) text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        FlushAutoSave(); // 앞서 쓰던 파일에 고친 게 남았으면 먼저 저장
        txtFromZip = fromZip;
        hwpSource = !fromZip && HwpDocument.IsHwpExtension(path);
        if (fromZip || hwpSource) txtSaveEncoding = new UTF8Encoding(false);
        // 카페움 오피스: 보통 txt는 메모장처럼 바로 쓸 수 있게 텍스트 상자로 연다(zip · 한글 문서에서 뽑은 글은 예전처럼 읽기 화면).
        // 참고 자료 폴더의 txt는 남의 글이라 바로 고치게 열지 않는다: 읽기 화면으로 열고 [수정하기]를 눌러야 고칠 수 있다.
        directEdit = !fromZip && !hwpSource && !IsReferenceFile(path);
        if (directEdit)
        {
            txtWeb = false;
            txtBook = null;
            txtFindStart = txtFindEnd = 0;
            SetContentMode(ContentKind.Txt);
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            epubEditable = false;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n'); // 글 상자 안에서는 줄바꿈이 \n 한 글자(저장할 때만 \r\n)
            fullText = text;
            chapters = TxtChapterSplitter.Split(text, Path.GetFileNameWithoutExtension(path), TxtSplitOptions.From(settings));
            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"총 {text.Length:N0}자";
            mainEditor.Text = text;
            if (directEdit) TryLoadFormatSidecar(text); // 이 글에 입혀 둔 서식이 있으면 불러온다
            mainEditor.ApplyParaFormat(EditorPara(), force: true); // 글을 새로 넣으면 문단 서식이 기본으로 돌아가니 다시 건다
            formatDirty = false;
            savedSnapshot = mainEditor.Text;
            ApplySideMargin(); // 회차 제목을 새 파일 이름으로
            SetTxtEditMode(true);
            ApplyManuscriptProtection(); // 집필 완료한 회차는 원고 보호로 잠근다
            var saveRec = library.Find(path);
            int pos = saveRec != null && saveRec.PosA > 0 && saveRec.PosA < text.Length ? saveRec.PosA : 0;
            GoToOffset(pos);
            mainEditor.Focus();
            return;
        }

        txtWeb = true; // 읽기는 웹 화면으로(SetContentMode가 epubHost를 보여준다)
        txtBook = null;
        txtFindStart = txtFindEnd = 0;
        SetContentMode(ContentKind.Txt); // 이전에 epub/이미지를 보고 있었다면 txt 화면으로 되돌린다
        imageSet?.Dispose();
        imageSet = null;
        ClearImageCache();
        currentPath = path;
        OnBookOpened(path);
        ApplyLookBarShown(); // 열린 파일이 참고 자료인지에 따라 서식창을 고른다(경로가 정해진 뒤에)
        // 새 파일을 열면 항상 읽기 전용으로 시작한다. SetEditMode를 거치면 "수정을 마쳤다"로 보고 옛 글로 웹 화면을
        // 다시 만들므로 상자만 잠그고, 앞서 보던 epub의 수정하기도 끈다(같은 웹뷰에 txt 문서가 뜨니까).
        SetTxtEditMode(false);
        epubEditable = false;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        fullText = text;
        chapters = prepared ?? TxtChapterSplitter.Split(text, Path.GetFileNameWithoutExtension(path), TxtSplitOptions.From(settings));
        Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
        statusLabel.Text = $"{chapters.Count}개 챕터 · 총 {text.Length:N0}자";

        var rec = library.Find(path);
        int saved = rec != null && rec.PosA > 0 && rec.PosA < fullText.Length ? rec.PosA : 0;
        if (saved > 0) ShowToast($"이어서 읽습니다 ({(int)Math.Round(100.0 * saved / fullText.Length)}%)");
        mainEditor.Text = ""; // 큰 글을 보이지도 않는 상자에 들고 있지 않게(수정하기를 켤 때 다시 넣는다)
        ShowTxtWeb(saved);
    }

    /// <summary>
    /// txt를 웹 화면으로 띄운다: 글을 문서 여러 개로 나눠 임시 폴더에 쓰고(TxtWebBook) epub과 같은 길로 보여준 뒤 offset 위치로 옮긴다.
    /// WebView2 런타임이 없는 PC처럼 웹 화면을 못 쓰면 예전처럼 텍스트 상자로 보여준다.
    /// </summary>
    private async void ShowTxtWeb(int offset)
    {
        string path = currentPath!;
        try
        {
            await EnsureEpubViewAsync();
            string text = fullText, title = Path.GetFileNameWithoutExtension(path);
            var chs = chapters;
            var book = await Task.Run(() => TxtWebBook.Build(text, chs, title, path));
            if (!txtWeb || currentPath != path) return; // 만드는 사이 다른 파일을 열었거나 수정하기를 켰다
            txtBook = book;
        }
        catch (Exception ex)
        {
            if (currentPath != path) return;
            txtWeb = false;
            txtBook = null;
            SetContentMode(ContentKind.Txt);
            mainEditor.Text = fullText;
            GoToOffset(offset);
            statusLabel.Text = $"웹 화면을 쓸 수 없어 기본 화면으로 엽니다: {ex.Message}";
            return;
        }

        epubBook = txtBook.Book;
        epubExtractDir = null;
        epubChapterIndex = -1; // 예전 epub의 같은 번호 챕터를 "이미 떠 있는 문서"로 착각하지 않게
        lastEpubTrackedChapter = -1;
        ShowTxtWebAt(offset);
        epubView?.Focus(); // 방향키 · 휠이 바로 먹도록
    }

    /// <summary>txt 웹 화면에서 원본 글자 위치(offset)로 간다. 이미 그 문서가 떠 있으면 다시 불러오지 않고 그 안에서 옮긴다.</summary>
    private void ShowTxtWebAt(int offset, bool reload = false)
    {
        if (txtBook == null) return;
        offset = Math.Clamp(offset, 0, fullText.Length);
        txtWebTopOffset = offset;
        int chunk = txtBook.ChunkAt(offset);
        if (!reload && chunk == epubChapterIndex && epubDocReady)
        {
            RunEpubScript($"window.__clrTxtGo && window.__clrTxtGo({offset});");
            RefreshNavPosition();
            return;
        }
        txtPendingOffset = offset;
        epubRestorePermille = null;
        ShowEpubChapter(chunk);
    }

    /// <summary>txt 웹 화면에서 찾은 글(전체 글 기준 구간)을 표시하고 그 자리로 간다.</summary>
    private void TxtWebSelect(int start, int length)
    {
        if (txtBook == null) return;
        txtFindStart = start;
        txtFindEnd = start + length;
        txtWebTopOffset = start;
        int chunk = txtBook.ChunkAt(start);
        if (chunk == epubChapterIndex && epubDocReady)
        {
            RunEpubScript($"window.__clrTxtSel && window.__clrTxtSel({start},{length});");
            RefreshNavPosition();
            return;
        }
        txtPendingSelect = (start, length);
        epubRestorePermille = null;
        ShowEpubChapter(chunk);
    }

    /// <summary>txt 웹 화면 맨 위 줄의 원본 위치를 읽어 둔다(읽던 자리 저장 · 탐색 막대 · 글꼴 바꾼 뒤 제자리 찾기에 쓴다).</summary>
    private async void QueryTxtTop()
    {
        if (!txtWeb || epubView?.CoreWebView2 == null || positionQueryBusy || !epubDocReady || txtPendingOffset != null) return;
        positionQueryBusy = true;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTxtTop ? window.__clrTxtTop() : -1");
            if (txtWeb && epubDocReady && int.TryParse(json, out int v) && v >= 0) txtWebTopOffset = Math.Min(v, fullText.Length);
        }
        catch { /* 문서가 바뀌는 중이면 이번 값은 건너뛴다 */ }
        finally { positionQueryBusy = false; }
        RefreshNavPosition();
    }

    /// <summary>글꼴 · 여백 · 읽기 방식이 바뀌어 다단 배치를 새로 해야 할 때, 지금 문서를 다시 불러오되 읽던 자리 근처에 머문다.</summary>
    private void ReloadEpubKeepingPlace()
    {
        if (txtWeb) { ShowTxtWebAt(txtWebTopOffset, reload: true); return; }
        epubRestorePermille = epubRatioPermille > 0 ? epubRatioPermille : null;
        ShowEpubChapter(epubChapterIndex);
    }

    /// <summary>작품 폴더의 "참고 자료" 폴더 안 파일인가.</summary>
    private bool IsReferenceFile(string? path) =>
        InPip /* 탐색기 "Caffeum으로 보기"로 연 PIP 창의 문서는 어느 폴더의 파일이든 참고 자료처럼 다룬다(참고 자료 서식창 · 읽기 전용) */ || !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), NovelStore.ReferenceFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>수정하기를 켜면: 웹 화면에서 보던 자리 그대로 텍스트 상자(mainEditor)로 넘어간다.</summary>
    private void LeaveTxtWebForEditing()
    {
        int offset = txtWebTopOffset;
        StopTts();
        txtWeb = false;
        if (IsReferenceFile(currentPath)) { directEdit = true; savedSnapshot = fullText; } // 참고 자료: 고치는 동안만 자동 저장
        SetContentMode(ContentKind.Txt);
        mainEditor.Text = fullText;
        GoToOffset(offset);
    }

    /// <summary>수정을 마치면: 고친 글로 챕터를 다시 나누고 웹 화면으로 돌아온다(보던 자리 유지).</summary>
    private void ReturnToTxtWeb()
    {
        int offset = TxtTopOffset();
        if (IsReferenceFile(currentPath)) { FlushAutoSave(); directEdit = false; } // 참고 자료: 마치면 저장하고 다시 읽기 전용
        chapters = TxtChapterSplitter.Split(fullText, Path.GetFileNameWithoutExtension(currentPath ?? ""), TxtSplitOptions.From(settings));
        txtWeb = true;
        txtBook = null;
        SetContentMode(ContentKind.Txt);
        mainEditor.Text = "";
        ShowTxtWeb(offset);
    }

    /// <summary>File.ReadAllText와 결과는 같지만, 청크 단위로 읽으면서 파일 크기 대비 읽은 바이트로
    /// 진행률을 보고한다. 큰 txt(수십~수백 MB 소설 등)를 열 때 게이지를 보여주려고 둔 것이다.</summary>
    private static string ReadAllTextWithProgress(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        long total = fs.Length;
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(total > 0 ? (int)Math.Min(total, int.MaxValue) : 0);
        char[] buffer = new char[1 << 16];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.Append(buffer, 0, read);
            // fs.Position은 StreamReader 내부 버퍼링 때문에 실제 디코딩 진행과 정확히 비례하진 않지만,
            // 게이지용 대략치로는 충분하다.
            if (total > 0) progress?.Report((double)fs.Position / total);
        }
        return sb.ToString();
    }

    private async void SaveFile(bool forceDialog)
    {
        if (epubMode && !txtWeb) { await SaveEpubFile(forceDialog); return; }

        string? path = currentPath;
        // zip 안에서 꺼내 보는 소설은 currentPath가 zip 파일이라, 그대로 저장하면 zip이 글자 파일로 덮어써진다 — 항상 새 이름으로.
        // 한글 문서에서 뽑은 글도 마찬가지로 원본(.hwp)을 글자 파일로 덮어쓰면 안 되니 txt로 "다른 이름으로"만.
        if (txtFromZip || hwpSource) forceDialog = true;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        // 페이지 모드에서는 mainEditor.Text가 현재 페이지 조각뿐이니, 그럴 땐 fullText(전체)를 저장한다.
        string content = pageMode || txtWeb ? fullText : mainEditor.Text;
        try
        {
            File.WriteAllText(path!, ToCrlf(content), txtSaveEncoding);
            if (directEdit) savedSnapshot = content;
            currentPath = path;
            txtFromZip = false; // 이제 진짜 txt 파일로 저장됐다
            hwpSource = false;
            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"저장했습니다: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장에 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 지금 보고 있는 챕터의 편집 내용을 반영한 뒤, 풀어둔 폴더 전체를 다시 zip(epub)으로 묶는다.
    /// 다른 챕터에서 이미 편집했던 내용은 챕터를 넘어갈 때마다 이미 파일에 반영돼 있어서 그대로 같이 묶인다.
    /// </summary>
    private async Task SaveEpubFile(bool forceDialog)
    {
        if (epubBook == null || epubExtractDir == null)
        {
            MessageBox.Show(this, "저장할 epub이 열려 있지 않습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? path = currentPath;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog { Filter = "EPUB (*.epub)|*.epub" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        try
        {
            if (epubEditable) await FlushEpubChapterEdits();
            RepackEpub(epubExtractDir, path!);
            currentPath = path;
            Text = $"{epubBook.Title} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"저장했습니다: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장에 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>풀어둔 epub 폴더를 다시 zip으로 묶는다. epub 규격상 mimetype은 압축 없이 맨 처음 항목이어야 한다.</summary>
    private static void RepackEpub(string extractDir, string targetPath)
    {
        if (File.Exists(targetPath)) File.Delete(targetPath);
        using var zip = System.IO.Compression.ZipFile.Open(targetPath, System.IO.Compression.ZipArchiveMode.Create);

        string mimetypePath = Path.Combine(extractDir, "mimetype");
        if (File.Exists(mimetypePath))
        {
            var entry = zip.CreateEntry("mimetype", System.IO.Compression.CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            using var src = File.OpenRead(mimetypePath);
            src.CopyTo(entryStream);
        }

        foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(extractDir, file).Replace('\\', '/');
            if (rel == "mimetype") continue;
            zip.CreateEntryFromFile(file, rel, System.IO.Compression.CompressionLevel.Optimal);
        }
    }

    // ───────────────────────── 읽기 ↔ 편집 모드 ─────────────────────────

    /// <summary>편집 메뉴의 "수정하기"는 txt/epub 공용이라, 지금 어느 쪽을 보고 있는지에 따라 알맞은 쪽으로 보낸다.</summary>
    private void SetEditMode(bool editable)
    {
        if (editable && lockMode) { ShowToast("잠금 모드에서는 수정할 수 없어요 — 잠금을 풀어 주세요"); return; }
        // 한글 문서는 서식(표 · 그림 · 글자 모양)을 지키려고 한컴 한글에서 고친다 — 커피뷰어 안의 글 상자로는 들어가지 않는다.
        if (editable && hwpSource) { EditHwpInHancom(); return; }
        if (editable) StopTts(); // 고치는 중에는 낭독하지 않는다(글이 바뀌면 읽던 위치가 어긋난다)
        else FlushAutoSave(); // 읽기 전용으로 돌아가기 전에 고친 글을 저장
        if (epubMode && !txtWeb) { SetEpubEditMode(editable); return; }

        // txt는 웹 화면에서 읽다가, 고칠 때만 텍스트 상자로 넘어갔다가 다 고치면 돌아온다.
        if (editable && txtWeb) LeaveTxtWebForEditing();
        bool wasEditing = !mainEditor.ReadOnly;
        SetTxtEditMode(editable);
        if (!editable && wasEditing && shownKind == ContentKind.Txt) ReturnToTxtWeb();
    }

    /// <summary>기본은 읽기 전용이고, 편집 메뉴의 "수정하기"를 눌러야만 내용을 고칠 수 있게 한다.</summary>
    private void SetTxtEditMode(bool editable)
    {
        if (!editable && !mainEditor.ReadOnly)
        {
            // 편집 중이었다면, 잠그기 전에 지금까지 고친 내용을 fullText에 반영해둔다.
            fullText = mainEditor.Text;
        }
        mainEditor.ReadOnly = !editable;
        editModeMenuItem.Text = editable ? "읽기 전용으로" : hwpSource ? "한글에서 수정하기" : "수정하기";
        foreach (var item in editOnlyMenuItems) item.Enabled = editable;
        pageModeMenuItem.Enabled = !editable; // 편집 중엔 페이지(글 조각) 모드로 못 바꾼다
        pageTouchModeMenuItem.Enabled = !editable;

        if (editable && pageMode) SetReadingMode(ReadingMode.Scroll); // 편집을 시작하면 안전하게 스크롤(전체 글)로 되돌린다
    }

    /// <summary>epub 챕터를 웹 페이지 그대로 직접 고쳐 쓸 수 있게(contentEditable) 켜고 끈다.</summary>
    private async void SetEpubEditMode(bool editable)
    {
        epubEditable = editable;
        editModeMenuItem.Text = editable ? "읽기 전용으로" : "수정하기";

        if (editable && epubPageMode) SetReadingMode(ReadingMode.Scroll); // 편집 중엔 다단으로 잘려 보이면 위험하니 스크롤로

        if (epubView?.CoreWebView2 == null) return;
        string script = editable ? "document.body.contentEditable = 'true';" : "document.body.contentEditable = 'false';";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    // ───────────────────────── 챕터 · 이미지 목록(탐색 막대의 "챕터" / "이미지 선택") ─────────────────────────

    private List<string> CurrentChapterNames()
    {
        if (imageMode && imageSet != null)
            return Enumerable.Range(0, imageSet.Count).Select(i => imageSet.Name(i)).ToList();
        if (epubMode && !txtWeb)
            return epubBook?.Chapters.Select(c => c.Name).ToList() ?? new();
        return chapters.Select(c => c.Name).ToList();
    }

    /// <summary>탐색 막대의 목록에서 챕터(이미지)를 골랐을 때. 이미지면 그 장으로, epub이면 그 챕터 파일로, txt면 본문에서 그 위치로 이동한다.</summary>
    private void OnChapterActivated(int index)
    {
        HideEdgePrompt();
        if (imageMode)
        {
            JumpToImageItem(index);
        }
        else if (epubMode && !txtWeb)
        {
            ShowEpubChapter(index);
        }
        else if (index >= 0 && index < chapters.Count)
        {
            GoToOffset(chapters[index].Start);
            if (txtWeb) epubView?.Focus(); else mainEditor.Focus();
        }
        HostForm.Activate(); // 본문 창을 앞으로 가져온다(서재 안에 들어가 있으면 서재 창을)
    }

    /// <summary>전체 글 기준 위치(offset)로 이동한다. 페이지 모드면 그 위치부터 새로 한 페이지를 잡아서 연다.</summary>
    private void GoToOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, fullText.Length);
        if (txtWeb) { ShowTxtWebAt(offset); return; }
        if (pageMode)
        {
            currentPageStart = offset;
            currentPageEnd = FindPageEnd(currentPageStart);
            ShowCurrentPage();
            mainEditor.SelectionStart = 0;
        }
        else
        {
            mainEditor.SelectionStart = offset;
            mainEditor.SelectionLength = 0;
            ScrollCharToTop(offset); // 막대로 옮기면 그 자리가 화면 맨 위에 오게(ScrollToCaret은 맨 아래에 걸린다)
            return;
        }
        mainEditor.SelectionLength = 0;
        mainEditor.ScrollToCaret();
    }

    /// <summary>
    /// 찾기 결과(전체 글 fullText 기준 offset)를 화면에 보여준다. 스크롤 모드면 그냥 그 위치를 선택하면
    /// 되지만, 페이지 모드에서는 mainEditor가 지금 페이지 조각만 담고 있으므로 찾은 위치가 지금 페이지
    /// 범위 밖이면 그 위치부터 시작하는 새 페이지로 먼저 이동한 뒤 그 안에서 선택한다.
    /// </summary>
    private void SelectInDocument(int start, int length)
    {
        if (txtWeb) { TxtWebSelect(start, length); return; }
        if (pageMode)
        {
            if (start < currentPageStart || start + length > currentPageEnd)
            {
                currentPageStart = start;
                currentPageEnd = FindPageEnd(currentPageStart);
                ShowCurrentPage();
            }
            mainEditor.Focus();
            mainEditor.Select(start - currentPageStart, length);
        }
        else
        {
            mainEditor.Focus();
            mainEditor.Select(start, length);
        }
        mainEditor.ScrollToCaret();
    }

    // ───────────────────────── epub ─────────────────────────

    /// <summary>
    /// epub은 zip 안에 XHTML+이미지+CSS가 들어있는 진짜 웹 문서라 TextBox로는 못 그린다.
    /// 그래서 WebView2(엣지와 같은 크로미움 엔진)를 따로 붙여서 그 위에 XHTML을 그대로 띄운다.
    /// txt 전용 기능(수정하기·찾기·글자 크기)은 epub에는 아직 없어서 메뉴에서 잠가둔다.
    /// 읽기 방식(스크롤/페이지)만은 옵시디언 플러그인처럼 CSS 다단 레이아웃을 그대로 써서 epub에도 넣었다.
    /// </summary>
    private async void LoadEpubFile(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("EPUB을 여는 중…");
        try
        {
            await EnsureEpubViewAsync();
            epubExtractDir = Path.Combine(Path.GetTempPath(), "CoffeeViewerEpub",
                Path.GetFileNameWithoutExtension(path) + "_" + Math.Abs(path.GetHashCode()));
            string extractDir = epubExtractDir;
            var progress = new Progress<double>(loadingOverlay.Report);
            // 이미지가 많이 든 큰 epub은 통째로 풀어두는(EpubBook.Load 안의 압축 해제) 부분이 시간이
            // 걸려서, UI 스레드를 막지 않게 백그라운드에서 돌리고 진행률만 받아온다.
            epubBook = await Task.Run(() => EpubBook.Load(path, extractDir, progress, cts.Token));
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Epub);
            SetEditMode(false); // 새 epub을 열면 항상 읽기 전용으로 시작한다
            ShowEpubChapter(TakeSavedEpubStart(path)); // 읽던 챕터가 있으면 거기부터, 없으면 0

            Text = $"{epubBook.Title} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"{epubBook.Chapters.Count}개 챕터 (epub)";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"epub을 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// PDF는 애초에 페이지 크기·배치가 고정된 문서라, epub처럼 CSS 다단으로 재배치할 이유가 없다.
    /// WebView2를 파일에 그대로 navigate시키면 엣지와 같은 크로미움 내장 PDF 뷰어(페이지 넘기기·확대,
    /// 검색·인쇄까지)가 그 안에서 자체 툴바로 다 떠서, 우리가 따로 페이지 넘김·읽기 방식을 구현할 필요가 없다.
    /// </summary>
    private async void LoadPdfFile(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("PDF를 여는 중…");
        try
        {
            await EnsurePdfViewAsync();
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Pdf);
            NavigatePdf(path);

            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = "PDF (페이지 넘기기·확대·검색은 뷰어 자체 툴바를 이용하세요)";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PDF를 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }
    }

    private async Task EnsurePdfViewAsync()
    {
        if (pdfView != null) return;
        pdfView = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false }; // 파일을 끌어놓으면 웹뷰가 그 파일로 이동해버리지 않게(창 전체 드롭이 처리한다)
        pdfHost.Controls.Add(pdfView);
        await pdfView.EnsureCoreWebView2Async();
        HookWebViewKeys(pdfView);
    }

    private async Task EnsureEpubViewAsync()
    {
        if (epubView != null) return;
        epubView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = theme.Back, AllowExternalDrop = false };
        epubHost.Controls.Add(epubView);
        await epubView.EnsureCoreWebView2Async();
        // 배경색을 안 정해둔 책이 시스템 다크 모드 때문에 자동으로 반전(검은 배경)되지 않도록,
        // 항상 밝은 배색으로 강제한다(실제 색은 아래 BuildEpubThemeScript가 테마대로 덮어쓴다).
        epubView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        epubView.CoreWebView2.Settings.IsZoomControlEnabled = false; // Ctrl+휠 · Ctrl+-/+ 는 브라우저 확대가 아니라 글자 크기로(ZoomBy)
        epubView.CoreWebView2.NavigationCompleted += async (_, _) => await OnEpubNavigationCompleted();
        epubView.CoreWebView2.WebMessageReceived += OnEpubWebMessage;
        HookWebViewKeys(epubView);
        epubView.CoreWebView2.ContextMenuRequested += OnEpubContextMenuRequested;
    }

    /// <summary>
    /// startAtLastPage: "이전 챕터" 방향으로 넘어왔을 때, 앞쪽이 아니라 그 챕터의 마지막 페이지부터 보여준다
    /// (스크롤 모드에서는 의미 없고, 페이지 모드에서만 쓰인다).
    /// 지금 챕터를 편집 중이었다면, 떠나기 전에 고친 내용을 먼저 파일에 반영해둔다(안 그러면 그냥 날아간다).
    /// </summary>
    private async void ShowEpubChapter(int index, bool startAtLastPage = false)
    {
        if (epubBook == null || epubView?.CoreWebView2 == null) return;
        if (index < 0 || index >= epubBook.Chapters.Count) return;

        if (ttsActive && !ttsInternalNav) StopTts(); // 낭독 중에 직접 챕터를 옮기면(화살표·목차·글자 크기 변경 등) 낭독은 멈춘다
        if (epubEditable) await FlushEpubChapterEdits();

        epubChapterIndex = index;
        epubDocReady = false;
        RefreshNavPosition();
        if (index != lastEpubTrackedChapter) { epubRatioPermille = 0; lastEpubTrackedChapter = index; } // 다른 챕터로 옮기면 챕터 안 위치는 처음부터
        epubPendingStartAtLastPage = startAtLastPage;
        epubView.CoreWebView2.Navigate(new Uri(epubBook.Chapters[index].FilePath).AbsoluteUri);
    }

    /// <summary>지금 화면에 떠 있는 챕터의 (편집됐을 수도 있는) HTML을 읽어와서 풀어둔 폴더의 원본 파일에 덮어쓴다.</summary>
    private async Task FlushEpubChapterEdits()
    {
        if (epubBook == null || epubView?.CoreWebView2 == null) return;
        if (epubChapterIndex < 0 || epubChapterIndex >= epubBook.Chapters.Count) return;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            string? html = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrEmpty(html)) return;
            File.WriteAllText(epubBook.Chapters[epubChapterIndex].FilePath, "<!DOCTYPE html>\n" + html, new UTF8Encoding(false));
        }
        catch { /* 편집 중이 아니었거나 문서가 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>챕터를 새로 불러올 때마다: 이미지 표시 여부 반영 + (페이지 모드면 다단 배치, 스크롤 모드면 바닥 감지) 스크립트를 심는다.</summary>
    private async Task OnEpubNavigationCompleted()
    {
        if (epubView?.CoreWebView2 == null) return;
        ApplyShowImages();
        ApplyEpubFontSize();

        try
        {
            // 책 자체가 어두운 배경/밝은 글자로 스타일을 박아둔 경우(다크 테마로 만든 책 등)도 있어서,
            // 브라우저의 다크모드 방지만으론 부족하다. 배경·글자색을 지금 테마 색으로 강제로 덮어쓴다.
            await epubView.CoreWebView2.ExecuteScriptAsync(BuildEpubThemeScript());
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubTtsScript); // 낭독·우클릭 메뉴가 쓰는 도우미(문서마다 새로 심는다)
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubKeepPlaceScript); // 글자 크기를 바꿔도 읽던 자리 지키기
        }
        catch { }

        if (epubEditable)
        {
            // 챕터를 새로 불러오면(다음/이전 챕터 이동 포함) 문서가 통째로 바뀌므로 매번 다시 켜줘야 한다.
            try { await epubView.CoreWebView2.ExecuteScriptAsync("document.body.contentEditable = 'true';"); } catch { }
        }

        try
        {
            if (epubPageMode)
            {
                string startPage = epubPendingStartAtLastPage ? "999999" : "0";
                epubPendingStartAtLastPage = false;
                string script = EpubPaginateScript.Replace("%START_PAGE%", startPage)
                    .Replace("%TAP_ENABLED%", tapToTurnEnabled ? "true" : "false")
                    .Replace("%TAP_CENTER%", TapZone.CenterRatio.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("%PAD_L%", WebMargins().L.ToString()).Replace("%PAD_R%", WebMargins().R.ToString())
                    .Replace("%PAD_T%", WebMargins().T.ToString()).Replace("%PAD_B%", WebMargins().B.ToString())
                    .Replace("%COLS%", (epubMode && settings.EpubDoublePage) ? "2" : "1");
                await epubView.CoreWebView2.ExecuteScriptAsync(script);
            }
            else
            {
                // 스크롤 모드에서도, 웹소설을 변환한 epub 등 책 자체 CSS가 화면 넓을 때 스스로 다단
                // (column)을 켜는 경우가 있어 그대로 두면 글이 여러 단으로 쪼개져 뒤죽박죽으로 보인다.
                // 우리는 항상 세로로 쭉 이어지는 한 단짜리 스크롤을 기대하므로 강제로 꺼둔다.
                await epubView.CoreWebView2.ExecuteScriptAsync(BuildEpubResetColumnsScript());

                // 스크롤할 내용이 있는 보통 챕터라면 화살표는 원래대로 숨긴다 — 스크롤할 게 아예 없는
                // 챕터(이미지 한 장이 화면에 꽉 차는 경우 등)일 때만 스크립트가 곧 다시 보여달라고
                // 알려온다(아래 'scroll-dead-end' 처리) — 그런 챕터는 스크롤 이벤트 자체가 안 일어나서
                // 다음 챕터로 넘어갈 방법이 화살표(또는 키보드) 말고는 없기 때문이다.
                UpdatePageNavVisibility(false);
                if (epubPendingStartAtLastPage)
                {
                    // 위로 올려 이전 챕터에 왔으면 그 챕터의 끝(바닥 바로 위)에서 이어 읽는다.
                    // 바닥에 딱 붙이면 곧바로 "바닥 도달"로 다음 챕터가 열려 버리니 한 화면 가까이 위에 둔다.
                    epubPendingStartAtLastPage = false;
                    await epubView.CoreWebView2.ExecuteScriptAsync(
                        "window.scrollTo(0, Math.max(0, document.documentElement.scrollHeight - window.innerHeight - 60));");
                }
                await epubView.CoreWebView2.ExecuteScriptAsync(EpubScrollWatchScript);
            }
        }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }

        try
        {
            // 만화/이미지 뷰어(ImageCanvas)와 같은 조작감으로, 본문 속 이미지도 클릭하면 확대해서 보게 한다.
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubImageZoomScript);
        }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }

        epubDocReady = true;
        await AfterEpubChapterReady(); // 읽던 위치 복원, 낭독 이어가기 (MainForm.Features.cs)
    }

    /// <summary>보기 > 이미지 표시 체크를 껐다 켰다 하면 지금 열려 있는 epub 챕터에 CSS를 꽂아 이미지를 숨기거나 되돌린다.</summary>
    private async void ApplyShowImages()
    {
        if (!epubMode || epubView?.CoreWebView2 == null) return;
        string css = showImagesMenuItem.Checked ? "" : "img, svg, image { display: none !important; }";
        string script = "(function(){var s=document.getElementById('__clr_hide_img');" +
            "if(!s){s=document.createElement('style');s.id='__clr_hide_img';document.head.appendChild(s);}" +
            "s.textContent=" + JsonSerializer.Serialize(css) + ";})();";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>지금 열려 있는 epub 챕터에 켜진 스크립트에 터치 켜짐 여부만 살짝 갱신한다 — 페이지 ↔
    /// 페이지 터치 전환처럼 다단 배치 자체는 안 바뀔 때, 챕터를 통째로 다시 불러와 위치가 처음으로
    /// 되돌아가는 것을 피하려고 쓴다.</summary>
    private async void PushEpubTapEnabled()
    {
        if (epubView?.CoreWebView2 == null) return;
        try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTapEnabled = {(tapToTurnEnabled ? "true" : "false")};"); }
        catch { /* 문서가 아직 준비 안 됐으면(또는 스크롤 모드라 스크립트 자체가 없으면) 그냥 무시 */ }
    }

    /// <summary>보기 > 글자 크기를 지금 열려 있는 epub 챕터에도 그대로 적용한다(책 자체 글꼴 크기와 무관하게 강제).</summary>
    /// <summary>keepPlace: 읽는 도중 글자 크기 · 글꼴을 바꿀 때. 문서를 다시 불러오지 않고 그 자리에서 바꾸되, 바꾸기 전 화면 맨 위의
    /// 글자가 바꾼 뒤에도 보이게(페이지 방식 = 그 글자가 든 페이지로, 스크롤 = 같은 높이로) 맞춘다(EpubKeepPlaceScript).
    /// 예전엔 페이지 방식에서 문서를 통째로 다시 불러와 읽던 자리를 문단 단위로 어림잡느라, Ctrl+휠을 돌릴 때마다 앞뒤 페이지로 튀었다.</summary>
    private async void ApplyEpubFontSize(bool keepPlace = false)
    {
        if (!epubMode || epubView?.CoreWebView2 == null) return;
        epubView.ZoomFactor = ViewZoom / 100.0;
        // 글자 크기와 글꼴(설정에서 고른 것)을 책 자체 스타일과 무관하게 강제한다.
        // 줄 간격 · (txt의) 문단 간격도 여기서 같이 강제한다 — 설정창의 "줄 간격 · 문단 간격".
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string css = "* { font-size: " + fontSize.ToString(inv) + "pt !important; " +
            "font-family: " + (EffFontLatin is { } latin ? "\"" + latin.Replace("\"", "") + "\", " : "") + "\"" + EffFont.Replace("\"", "") + "\", sans-serif !important; " +
            "line-height: " + (EffLine / 100.0).ToString(inv) + " !important; }" +
            (EffAlign is "center" or "right" or "justify" ? " p { text-align: " + EffAlign + " !important; }" : EffAlign is "distribute" or "divide" ? " p { text-align: justify !important; text-align-last: justify !important; }" : "") +
            PaperCss() +
            " body.clr-txt p { margin-bottom: " + Math.Clamp(settings.ParagraphSpacing, 0, 60) + "px !important; }" + ParaCss();
        string apply = "var s=document.getElementById('__clr_font_size');" +
            "if(!s){s=document.createElement('style');s.id='__clr_font_size';document.head.appendChild(s);}" +
            "s.textContent=" + JsonSerializer.Serialize(css) + ";";
        string script = keepPlace && epubDocReady
            ? "(function(){var apply=function(){" + apply + "}; if (window.__clrKeepPlace) window.__clrKeepPlace(apply); else apply();})();"
            : "(function(){" + apply + "})();";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>
    /// 글자 크기 · 글꼴을 바꿀 때 읽던 자리를 지키는 도우미(문서마다 심는다). 바꾸기 전에 화면 왼쪽 위 근처의 글자 하나를 집어 두고
    /// (caretRangeFromPoint — 페이지 방식의 translateX도 반영된 화면 좌표로 집힌다), 바꾼 다음 다단을 다시 나눈 뒤 그 글자가 있는
    /// 페이지로 간다. 스크롤 방식이면 그 글자가 전과 같은 높이에 오도록 스크롤한다. 글자를 못 집으면(그림만 있는 화면 등) 그냥 바꾼다.
    /// </summary>
    private const string EpubKeepPlaceScript = @"
(function(){
  if (window.__clrKeepPlace) return;
  function probe() {
    var w = window.innerWidth, h = window.innerHeight;
    for (var y = 6; y < h * 0.5; y += 14) {
      for (var x = 6; x < w * 0.6; x += 28) {
        var r = document.caretRangeFromPoint ? document.caretRangeFromPoint(x, y) : null;
        if (r && r.startContainer && r.startContainer.nodeType === 3 && /\S/.test(r.startContainer.nodeValue)) {
          var n = r.startContainer, o = Math.min(r.startOffset, n.nodeValue.length - 1);
          var k = document.createRange();
          k.setStart(n, Math.max(0, o)); k.setEnd(n, Math.max(0, o) + 1);
          // 빈 자리(여백)를 찍으면 caretRangeFromPoint는 가장 가까운 글자를 돌려주는데, 페이지 방식에서는 그게 화면 밖
          // 앞 페이지의 글자일 수 있다(그래서 글자 크기를 바꾸면 첫 페이지로 튀었다). 실제로 화면 안에 보이는 글자만 쓴다.
          var b = rectOf(k);
          if (b.width > 0 && b.left >= 0 && b.right <= w + 1 && b.top >= 0 && b.bottom <= h + 1) return k;
        }
      }
    }
    return null;
  }
  function rectOf(r) { var rs = r.getClientRects(); return rs.length ? rs[0] : r.getBoundingClientRect(); }
  function paged() { return window.__clrGoTo && window.__clrTotalPages !== undefined; }
  // 글자 크기를 연달아 바꾸는 동안(그사이 페이지를 넘기거나 스크롤하지 않았으면) 처음 집은 글자를 계속 기준으로 쓴다 — 작게 했다가
  // 다시 키울 때 매번 새로 집으면, 작을 때 화면 맨 위에 올라온 앞쪽 글(장 제목 등)로 기준이 밀려 점점 앞으로 간다.
  var anchor = null, anchorPage = -1, anchorY = -1;
  window.__clrKeepPlace = function(apply) {
    var here = anchor && (paged() ? window.__clrPageIndex === anchorPage : Math.abs(window.scrollY - anchorY) < 2);
    var r = here ? anchor : probe(), before = r ? rectOf(r) : null;
    apply();
    if (window.__clrRelayout) window.__clrRelayout();
    if (!r) { anchor = null; return; }
    var after = rectOf(r);
    if (paged()) {
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      window.__clrGoTo(Math.floor((after.left - tx + 1) / window.__clrPageWidth));
    } else if (before) {
      window.scrollBy(0, after.top - before.top);
    }
    anchor = r; anchorPage = window.__clrPageIndex; anchorY = window.scrollY;
  };
})();
";

    /// <summary>epub 안에서 온 메시지 처리: 페이지 번호 갱신, 페이지 모드에서 챕터 경계를 넘어갔을 때 다음/이전 챕터로,
    /// 스크롤 모드에서 바닥까지 밀었을 때 다음 챕터로.</summary>
    private void OnEpubWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string type = doc.RootElement.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "next-chapter":
                    // 스크롤 모드에서 낭독이 화면 맨 아래 문장에 닿으면 스크롤 감시가 "바닥 도달"로 다음 챕터를
                    // 넘겨버려 남은 문장을 못 읽게 된다 — 낭독 중엔 챕터 넘김을 낭독 쪽이 직접 맡는다.
                    if (ttsActive && !epubPageMode) break;
                    // 페이지 방식에서 마지막 챕터의 마지막 페이지를 넘기려 한 것 — 옆 파일 처리로(스크롤 방식은 바닥 감시가 반복해서 알리므로 제외).
                    if (epubPageMode && epubBook != null && epubChapterIndex + 1 >= epubBook.Chapters.Count) { HandleBookEdge(forward: true); break; }
                    ShowEpubChapter(epubChapterIndex + 1);
                    break;
                case "prev-chapter":
                    if (epubPageMode && epubChapterIndex - 1 < 0) { HandleBookEdge(forward: false); break; }
                    ShowEpubChapter(epubChapterIndex - 1, startAtLastPage: true);
                    break;
                case "page":
                    int index = doc.RootElement.GetProperty("index").GetInt32();
                    int total = doc.RootElement.GetProperty("total").GetInt32();
                    pageIndicatorLabel.Text = $"{index + 1} / {total} 페이지";
                    epubPageIdx = index; epubPageTotal = total;
                    RefreshNavPosition(); // 아래 네비게이션 바의 숫자도 현재 페이지 / 전체 페이지로
                    if (txtWeb) QueryTxtTop(); // 넘길 때마다 읽던 자리 · 탐색 막대를 따라가게
                    break;
                case "key":
                    TryHandleFeatureKey(doc.RootElement.GetProperty("key").GetString() switch
                    {
                        "F11" => Keys.F11,
                        "]" => Keys.OemCloseBrackets,
                        "[" => Keys.OemOpenBrackets,
                        _ => Keys.F5,
                    });
                    break;
                case "zoom":
                    ViewZoomBy(doc.RootElement.GetProperty("delta").GetInt32());
                    break;
                case "scroll-dead-end":
                    // 스크롤 모드인데 스크롤할 내용이 없는 챕터 — 스크롤로는 절대 다음 챕터로 못 넘어가니,
                    // 이때만 예외적으로 화살표를 보여준다(눌러서 챕터를 넘기게).
                    UpdatePageNavVisibility(true);
                    break;
            }
        }
        catch { /* 형식이 이상한 메시지는 무시 */ }
    }

    /// <summary>epub에서 화살표 버튼을 눌렀을 때. 페이지 모드면 페이지 넘기기(경계를 넘어가면 스크립트가
    /// 알아서 다음/이전 챕터를 요청한다) — 스크롤 모드에서 화살표가 보이는 건 스크롤할 내용이 아예
    /// 없는 챕터(scroll-dead-end)일 때뿐이라, 그때는 페이지 개념이 없으니 챕터 자체를 바로 넘긴다.</summary>
    private async void EpubChangePage(int delta)
    {
        if (epubView?.CoreWebView2 == null) return;
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        if (ttsActive) StopTts(); // 화살표로 직접 넘기면 낭독은 멈춘다
        string fn = epubPageMode
            ? (delta > 0 ? "window.__clrNext && window.__clrNext();" : "window.__clrPrev && window.__clrPrev();")
            : (delta > 0
                ? "window.chrome.webview.postMessage(JSON.stringify({type:'next-chapter'}));"
                : "window.chrome.webview.postMessage(JSON.stringify({type:'prev-chapter'}));");
        try { await epubView.CoreWebView2.ExecuteScriptAsync(fn); } catch { }
    }

    /// <summary>
    /// 책 자체 CSS가 어두운 배경/밝은 글자(다크 테마)로 박혀 있어도, 배경과 글자는 항상 지금 고른 테마 색이
    /// 되도록 강제로 덮어쓴다. 개별 요소에 걸린 배경색은 투명 처리해서 body의 배경이 그대로 비치게 하고,
    /// 글자색은 전부 body 색을 물려받게 한다.
    /// </summary>
    private string BuildEpubThemeScript()
    {
        string back = Themes.ToCss(theme.Back), fore = Themes.ToCss(theme.Fore), link = Themes.ToCss(theme.Link);
        string border = Themes.ToCss(Themes.Blend(theme.Back, theme.Fore, 0.25f));
        return @"
(function(){
  var s = document.getElementById('__clr_force_light');
  if (!s) { s = document.createElement('style'); s.id = '__clr_force_light'; document.head.appendChild(s); }
  s.textContent =
    'html, body { background-color: " + back + @" !important; color: " + fore + @" !important; }' +
    ' * { background-color: transparent !important; color: inherit !important; border-color: " + border + @" !important; }' +
    ' a, a:visited { color: " + link + @" !important; }';
})();
";
    }

    /// <summary>
    /// 스크롤 모드용: 책 자체 CSS가 화면이 넓을 때 스스로 다단(column) 레이아웃을 켜는 경우(웹소설을
    /// 변환한 epub 등에 흔함)가 있어, 그대로 두면 세로 스크롤이어야 할 글이 여러 단으로 쪼개져
    /// 뒤죽박죽으로 보인다. !important로 늘 단일 컬럼(사실상 컬럼 없음)으로 강제한다. 글자가 화면
    /// 양 끝에 바짝 붙어 읽기 불편하지 않도록 좌우 여백도 여기서 강제한다(EpubPaginateScript의
    /// __CLR_PAGE_PADDING과 같은 값 — 스크롤/페이지 모드를 오가도 여백이 안 달라지게).
    /// </summary>
    private const string EpubResetColumnsScript = @"
(function(){
  var s = document.getElementById('__clr_reset_columns');
  if (!s) { s = document.createElement('style'); s.id = '__clr_reset_columns'; document.head.appendChild(s); }
  s.textContent = 'html, body { column-width: auto !important; column-count: auto !important; }' +
    ' body { padding: %PAD_T%px %PAD_R%px %PAD_B%px %PAD_L%px !important; box-sizing: border-box !important; }' +
    ' img, svg, image { max-width: 100% !important; }';
})();
";

    /// <summary>설정의 좌우/상하 여백을 스크롤 모드 스크립트에 채워 넣는다(페이지 모드 스크립트도 같은 값을 쓴다).</summary>
    private string BuildEpubResetColumnsScript() => EpubResetColumnsScript
        .Replace("%PAD_L%", WebMargins().L.ToString()).Replace("%PAD_R%", WebMargins().R.ToString())
        .Replace("%PAD_T%", WebMargins().T.ToString()).Replace("%PAD_B%", WebMargins().B.ToString());

    /// <summary>
    /// 옵시디언 플러그인과 같은 방식: 본문을 화면 폭만큼의 CSS 다단(column)으로 흘려보내고
    /// translateX로 한 번에 한 칸씩 밀어서 "페이지"처럼 보이게 한다. 화면 안에 다 담기지 않는
    /// 다단 컬럼도 있을 수 있는데, epub 챕터 하나는 원래 크기가 작아서(우리가 txt에서 겪은
    /// "소설 전체가 한 파일" 문제가 애초에 없음) 성능 걱정 없이 이 방식을 그대로 쓸 수 있다.
    /// </summary>
    private const string EpubPaginateScript = @"
(function(){
  document.documentElement.style.margin = '0';
  document.documentElement.style.overflow = 'hidden';
  document.body.style.margin = '0';
  document.body.style.boxSizing = 'border-box';
  document.body.style.height = '100vh';
  document.body.style.transition = 'transform 0.15s ease';

  // 책 자체 CSS가 (특히 웹소설을 변환한 epub들이) 화면이 넓으면 자기 스타일시트로 다단(column)을
  // 스스로 켜는 경우가 있는데, 그게 !important로 박혀 있으면 아래 document.body.style.* 같은 인라인
  // 지정으로는 못 이긴다 — 그러면 우리가 원하는 '창 폭 = 1컬럼'이 아니라 책이 정한 컬럼 수/폭 그대로
  // 여러 단이 한 화면에 다 보여서 글이 뒤죽박죽으로 읽힌다. <style> 태그 + !important로 확실히
  // 덮어써서 항상 우리가 계산한 컬럼 폭 하나로 고정한다.
  var __clrColStyle = document.getElementById('__clr_page_columns');
  if (!__clrColStyle) { __clrColStyle = document.createElement('style'); __clrColStyle.id = '__clr_page_columns'; document.head.appendChild(__clrColStyle); }
  // 이미지가 페이지 경계에서 반으로 잘리지 않게 한다. break-inside만으로는 이미지가 페이지 세로
  // 길이보다 큰 경우 피할 데가 없어서 그대로 잘리므로, 세로 길이를 페이지 높이 안으로 미리 줄여둔다.
  var __clrImgStyle = document.getElementById('__clr_page_img');
  if (!__clrImgStyle) { __clrImgStyle = document.createElement('style'); __clrImgStyle.id = '__clr_page_img'; document.head.appendChild(__clrImgStyle); }

  // 사진 바로 밑에 '(설명)' · '커버접기' 같은 짧은 글이 있으면 사진과 한 덩어리로 묶어 같은 페이지에 두고, 그 글의
  // 높이만큼 사진을 줄여 한 칸(페이지)에 둘이 다 들어가게 한다. 안 그러면 사진이 페이지 높이를 꽉 채워서 글만 다음 페이지로 밀린다.
  // 책마다 구조가 달라서 세 가지를 다 본다:
  //   ① 사진과 설명이 따로 놓인 줄(<font><img></font><br><font>(설명)</font>)  ② 같은 상자 안(<div><img><div>커버접기</div></div>)
  //   ③ 사진 문단 바로 다음 문단(<p><img></p><p>(설명)</p>)
  function __clrCaptionText(t) {
    t = (t || '').replace(/\s+/g, ' ').trim();
    return t.length > 0 && t.length <= 300 && (/^[(（]/.test(t) || /^커버\s*(접기|펴기|보기)/.test(t));
  }
  function __clrFitCaptions(vPad) {
    var vh = window.innerHeight - vPad;
    // 책 맨 앞의 빈 줄바꿈(<br>)은 숨긴다 — 안 그러면 첫 페이지가 줄바꿈만으로 차서 표지가 다음 페이지로 밀린다.
    for (var c = document.body.firstChild; c; c = c.nextSibling) {
      if (c.nodeType === 1 && /^br$/i.test(c.tagName)) c.style.display = 'none';
      else if (c.nodeType === 3 && (c.textContent || '').trim() === '') continue;
      else break;
    }
    // 표지(사진 + 커버접기)는 따로 한 페이지를 쓴다 — 뒤따르는 본문은 다음 페이지부터.
    var covers = document.querySelectorAll('.cover-wrapper');
    for (var cv = 0; cv < covers.length; cv++) {
      // 표지 상자가 인라인 <font> 안에 들어 있으면(블록이 인라인 속에 든 꼴) 다단에서 위치가 어긋나므로 font 밖으로 꺼낸다.
      var pf = covers[cv].parentElement;
      if (pf && pf !== document.body && /^font$/i.test(pf.tagName) && pf.parentElement && pf.children.length === 1 &&
          (pf.textContent || '').replace(covers[cv].textContent || '', '').trim() === '') {
        pf.parentElement.insertBefore(covers[cv], pf);
        pf.style.display = 'none';
      }
      covers[cv].style.breakAfter = 'column';
      covers[cv].style.webkitColumnBreakAfter = 'always';
    }
    var imgs = document.querySelectorAll('img, svg, image');
    for (var n = 0; n < imgs.length; n++) {
      var img = imgs[n];
      img.style.maxHeight = '';
      // 사진만 달랑 들어 있는 바깥 상자(<font><img></font>, <p><img></p> 등)까지 올라가 한 덩어리의 시작으로 삼는다.
      var block = img;
      while (block.parentElement && block.parentElement !== document.body && !block.parentElement.classList.contains('__clr_fig') &&
             block.parentElement.children.length === 1 && (block.parentElement.textContent || '').trim() === '') block = block.parentElement;
      // ①③ 바깥 상자 뒤로 줄바꿈(<br>)과 빈 칸만 건너뛰어 처음 만나는 요소가 설명이면 한 덩어리
      var between = [], cap = null;
      for (var nx = block.nextSibling; nx; nx = nx.nextSibling) {
        if (nx.nodeType === 3) { if ((nx.textContent || '').trim() === '') { between.push(nx); continue; } break; }
        if (nx.nodeType !== 1) { between.push(nx); continue; }
        if (/^br$/i.test(nx.tagName)) { between.push(nx); continue; } // XHTML로 열리면 태그 이름이 소문자(br)라서 대소문자를 가리지 않는다
        cap = nx; break;
      }
      if (cap && !cap.querySelector('img, svg, image') && __clrCaptionText(cap.textContent) && block.parentElement) {
        var wrap = block.parentElement;
        if (!wrap.classList.contains('__clr_fig')) {
          wrap = document.createElement('div');
          wrap.className = '__clr_fig';
          wrap.style.cssText = 'break-inside: avoid; -webkit-column-break-inside: avoid;';
          block.parentElement.insertBefore(wrap, block);
          wrap.appendChild(block);
          for (var b = 0; b < between.length; b++) wrap.appendChild(between[b]);
          wrap.appendChild(cap);
        }
        var cs = getComputedStyle(cap);
        var capH = cap.getBoundingClientRect().height + (parseFloat(cs.marginTop) || 0) + (parseFloat(cs.marginBottom) || 0) + between.length * 4;
        img.style.maxHeight = Math.max(60, vh - capH - 24) + 'px';
        // 사진 줄 아래 빈틈(글줄 높이 · 줄바꿈 등) 때문에 묶음이 아직 한 칸보다 크면 그만큼 사진을 더 줄인다.
        var over1 = wrap.getBoundingClientRect().height - vh;
        if (over1 > 0) img.style.maxHeight = Math.max(60, parseFloat(img.style.maxHeight) - over1 - 6) + 'px';
        continue;
      }
      // ② 설명이 같은 상자 안, 사진 뒤에 이어지는 경우
      var par = img.parentElement;
      if (par && par !== document.body && !par.classList.contains('__clr_fig')) {
        var after = '';
        for (var s = img.nextSibling; s; s = s.nextSibling) after += (s.textContent || '') + ' ';
        if (__clrCaptionText(after)) {
          par.style.breakInside = 'avoid';
          par.style.webkitColumnBreakInside = 'avoid';
          var extra = par.getBoundingClientRect().height - img.getBoundingClientRect().height;
          img.style.maxHeight = Math.max(60, vh - extra - 16) + 'px';
          var over2 = par.getBoundingClientRect().height - vh;
          if (over2 > 0) img.style.maxHeight = Math.max(60, parseFloat(img.style.maxHeight) - over2 - 6) + 'px';
        }
      }
    }
  }

  // 컬럼 폭·이미지 최대 높이·총 페이지 수·페이지당 이동폭을 지금 창 크기 기준으로 다시 잰다.
  // 처음 배치할 때뿐 아니라, 창 크기가 바뀔 때도(아래 resize 리스너) 다시 불러야 한다 — 안 그러면
  // 옛날 창 크기에 맞춰 나뉜 컬럼 폭이 그대로 남아, 지금 창보다 넓은/좁은 컬럼 일부만 잘려 보이면서
  // 서로 다른 컬럼의 내용이 뒤섞여 보인다(창을 키웠다 줄였을 때 겪는 증상).
  function __clrRelayout() {
    // 글자가 화면 양 끝에 바짝 붙어 보이지 않도록 페이지 좌우에 여백(패딩)을 준다. 컬럼 하나짜리
    // 페이지 폭 전체(window.innerWidth)에서 여백만큼을 빼 실제 컬럼 폭으로 쓰고, 그만큼을 컬럼
    // 사이 간격(column-gap)으로 돌려 다음 컬럼도 똑같이 좌우 여백을 갖게 한다(간격의 절반은 이번
    // 컬럼의 오른쪽 여백, 나머지 절반은 다음 컬럼의 왼쪽 여백이 되는 셈) — 그리고 맨 처음 컬럼도
    // 똑같이 왼쪽 여백을 갖도록 body 자체에도 그만큼 왼쪽 패딩을 준다. 이렇게 해야 컬럼 사이 실제
    // 간격(컬럼 폭 + gap)이 여전히 window.innerWidth 그대로라 페이지 이동폭 계산이 안 어긋난다.
    var __clrPadL = %PAD_L%, __clrPadR = %PAD_R%;
    var __clrPadT = %PAD_T%, __clrPadB = %PAD_B%;
    var __clrCols = %COLS%; // 1 = 한쪽, 2 = 두쪽(한 화면에 2단)
    var __clrColWidth = Math.max(100, window.innerWidth / __clrCols - __clrPadL - __clrPadR);
    __clrColStyle.textContent = 'body { column-gap: ' + (__clrPadL + __clrPadR) + 'px !important; column-fill: auto !important;' +
      ' column-width: ' + __clrColWidth + 'px !important; column-count: auto !important;' +
      ' padding-left: ' + __clrPadL + 'px !important; padding-right: ' + __clrPadR + 'px !important;' +
      ' padding-top: ' + __clrPadT + 'px !important; padding-bottom: ' + __clrPadB + 'px !important; }';
    __clrImgStyle.textContent = 'img, svg, image { break-inside: avoid; -webkit-column-break-inside: avoid;' +
      ' max-height: ' + (window.innerHeight - __clrPadT - __clrPadB) + 'px; max-width: 100%; width: auto; height: auto; object-fit: contain; }';
    __clrFitCaptions(__clrPadT + __clrPadB); // 사진 밑 (설명)이 있으면 같은 페이지에 들어가게
    // column-width는 스펙상 최소 힌트일 뿐이라, 브라우저가 실제로 렌더링하는 컬럼 폭은 컨테이너를
    // 정확히 나눠 채우느라 window.innerWidth와 1px 안팎으로 다를 수 있다. 페이지 이동폭을 요청폭
    // 그대로 쓰면 넘어갈 때마다 오차가 누적되어(1페이지만 넘어가도 이미 어긋남) 옆 페이지 끝부분이
    // 살짝 겹쳐 보이므로, 실제 렌더된 컬럼 폭(scrollWidth / 컬럼수)을 구해서 그걸로 이동한다.
    window.__clrTotalPages = __clrCols > 1 ? Math.max(1, Math.ceil(document.body.scrollWidth / window.innerWidth - 0.15)) : Math.max(1, Math.round(document.body.scrollWidth / window.innerWidth));
    window.__clrPageWidth = __clrCols > 1 ? window.innerWidth : document.body.scrollWidth / window.__clrTotalPages;
  }
  __clrRelayout();
  window.__clrRelayout = __clrRelayout; // 글자 크기를 그 자리에서 바꿀 때(EpubKeepPlaceScript) 다단을 다시 나누려고 밖으로 꺼내 둔다
  window.__clrPageIndex = 0;

  window.__clrGoTo = function(i, silent) {
    i = Math.max(0, Math.min(window.__clrTotalPages - 1, i));
    window.__clrPageIndex = i;
    document.body.style.transform = 'translateX(-' + (i * window.__clrPageWidth) + 'px)';
    if (!silent) window.chrome.webview.postMessage(JSON.stringify({ type: 'page', index: i, total: window.__clrTotalPages }));
  };
  window.__clrNext = function() {
    if (window.__clrPageIndex >= window.__clrTotalPages - 1) { window.chrome.webview.postMessage(JSON.stringify({ type: 'next-chapter' })); return; }
    window.__clrGoTo(window.__clrPageIndex + 1);
  };
  window.__clrPrev = function() {
    if (window.__clrPageIndex <= 0) { window.chrome.webview.postMessage(JSON.stringify({ type: 'prev-chapter' })); return; }
    window.__clrGoTo(window.__clrPageIndex - 1);
  };

  // 창 크기가 바뀌면(최대화↔복원, 가장자리로 드래그 등) 다단을 다시 배치하고, 지금 보던 위치에
  // 최대한 가까운 새 페이지로 맞춘다. 드래그 중 연달아 발생하는 resize 이벤트마다 다시 배치하면
  // 버벅이므로 잠깐(150ms) 멎었을 때 한 번만 한다.
  var __clrResizeTimer = null;
  window.addEventListener('resize', function() {
    if (__clrResizeTimer) clearTimeout(__clrResizeTimer);
    __clrResizeTimer = setTimeout(function() {
      __clrResizeTimer = null;
      var oldIndex = window.__clrPageIndex, oldTotal = window.__clrTotalPages;
      __clrRelayout();
      var ratio = oldTotal > 1 ? oldIndex / (oldTotal - 1) : 0;
      window.__clrGoTo(Math.round(ratio * (window.__clrTotalPages - 1)));
    }, 150);
  });

  document.addEventListener('keydown', function(e) {
    if (e.key === 'ArrowRight') { window.__clrNext(); e.preventDefault(); }
    else if (e.key === 'ArrowLeft') { window.__clrPrev(); e.preventDefault(); }
    else if (!e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'PageDown' || (e.key === ' ' && !e.shiftKey))) { window.__clrNext(); e.preventDefault(); }
    else if (!e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'PageUp' || (e.key === ' ' && e.shiftKey))) { window.__clrPrev(); e.preventDefault(); }
    else if (!e.ctrlKey && ['ArrowUp', 'ArrowDown', 'Home', 'End'].indexOf(e.key) >= 0) e.preventDefault(); // 페이지 방식에는 스크롤이 없다
  });

  // 휠은 스크롤 대신 한 칸에 한 페이지씩 넘긴다(Ctrl+휠 글자 크기 조절은 따로 처리한다). 터치 패닝 · 정밀
  // 휠처럼 작은 값이 연달아 오는 경우를 위해 한 번 넘기고 나면 잠깐 무시한다.
  var __clrWheelLast = 0;
  document.addEventListener('wheel', function(e) {
    if (e.ctrlKey) return;
    e.preventDefault();
    var now = Date.now();
    if (now - __clrWheelLast < 250 || e.deltaY === 0) return;
    __clrWheelLast = now;
    if (e.deltaY > 0) window.__clrNext(); else window.__clrPrev();
  }, { passive: false });

  // 선택 끌기 · 포커스 이동 · 찾기처럼 코드가 스크롤을 일으켜도 항상 제자리로 되돌린다.
  window.addEventListener('scroll', function() { if (window.scrollX || window.scrollY) window.scrollTo(0, 0); });

  // 화면을 셋으로 나눠서, 끌지 않고 콕 찍었을 때만 왼쪽 = 뒤로, 오른쪽 = 앞으로, 가운데 = 무시
  // (가운데 칸 비율은 C#의 TapZone.CenterRatio를 넣어준다 — txt·만화와 같은 기준) — 화살표 대신
  // 터치로 넘기기 편하게. 이미지 위(확대가 대신 열림)·링크 위(그 링크로 이동해야 함)·확대창이 열려
  // 있을 때는 건드리지 않고, 살짝이라도 끌렸으면(텍스트를 선택하려던 것) 무시한다. 더블클릭(단어
  // 선택)의 첫 번째 클릭까지 곧장 넘기면 안 되니, 잠깐 기다렸다가 그사이 두 번째 클릭(e.detail > 1)이
  // 안 왔을 때만(=진짜 한 번 탭한 것으로 판명 났을 때) 넘긴다.
  window.__clrTapEnabled = %TAP_ENABLED%; // 보기 > 읽기 방식(페이지 터치)과 연동(PushEpubTapEnabled가 즉시 갱신도 해준다)
  var __clrTapDownX = 0, __clrTapDownY = 0, __clrTapTimer = null;
  document.addEventListener('mousedown', function(e) { __clrTapDownX = e.clientX; __clrTapDownY = e.clientY; });
  document.addEventListener('mouseup', function(e) {
    if (!window.__clrTapEnabled) return;
    var t = e.target;
    if (t && (t.tagName === 'IMG' || t.closest('a') || t.closest('#__clr_zoom_overlay'))) return;
    if (e.detail > 1) { if (__clrTapTimer) { clearTimeout(__clrTapTimer); __clrTapTimer = null; } return; }
    var dx = e.clientX - __clrTapDownX, dy = e.clientY - __clrTapDownY;
    if (dx * dx + dy * dy > 36) return;
    var side = window.innerWidth * (1 - %TAP_CENTER%) / 2;
    if (e.clientX >= side && e.clientX < window.innerWidth - side) return; // 가운데 칸은 넘기지 않는다
    var goLeft = e.clientX < side;
    if (__clrTapTimer) clearTimeout(__clrTapTimer);
    __clrTapTimer = setTimeout(function() {
      __clrTapTimer = null;
      if (goLeft) window.__clrPrev(); else window.__clrNext();
    }, 350);
  });

  window.__clrGoTo(%START_PAGE%, true);
  window.chrome.webview.postMessage(JSON.stringify({ type: 'page', index: window.__clrPageIndex, total: window.__clrTotalPages }));
})();
";

    /// <summary>스크롤 모드에서: 챕터 바닥까지 다 내리면 자동으로 다음 챕터로 넘어간다. 이미지 한 장이
    /// 화면에 꽉 차는 챕터처럼 스크롤할 내용이 아예 없으면 스크롤 이벤트 자체가 안 일어나서 다음 챕터로
    /// 갈 방법이 없어지는데, 그럴 때는 MainForm에 알려서 화살표 버튼을 예외적으로 보여달라고 한다.</summary>
    private const string EpubScrollWatchScript = @"
(function(){
  var triggered = false;
  window.addEventListener('scroll', function() {
    if (triggered) return;
    if ((window.scrollY + window.innerHeight) >= (document.body.scrollHeight - 4)) {
      triggered = true;
      window.chrome.webview.postMessage(JSON.stringify({ type: 'next-chapter' }));
    }
  });
  if (document.body.scrollHeight <= window.innerHeight + 4) {
    window.chrome.webview.postMessage(JSON.stringify({ type: 'scroll-dead-end' }));
  }
  // 맨 위에서 위로 더 올리면(휠 · ↑ · PageUp) 이전 챕터로 — 바닥에서 다음 챕터로 넘어가는 것의 반대 방향.
  var prevTriggered = false;
  function goPrev() {
    if (prevTriggered || window.scrollY > 0) return;
    prevTriggered = true;
    window.chrome.webview.postMessage(JSON.stringify({ type: 'prev-chapter' }));
  }
  window.addEventListener('wheel', function(e) { if (e.deltaY < 0 && !e.ctrlKey) goPrev(); }, { passive: true });
  window.addEventListener('keydown', function(e) {
    if ((e.key === 'ArrowUp' || e.key === 'PageUp') && !e.ctrlKey && !e.altKey && !e.shiftKey) {
      var t = e.target; if (t && (t.isContentEditable || /^(INPUT|TEXTAREA)$/.test(t.tagName))) return;
      goPrev();
    }
  });
})();
";

    /// <summary>
    /// epub 본문 속 이미지를 클릭하면, 만화/이미지 뷰어(ImageCanvas)와 같은 조작감(휠로 확대·축소,
    /// 드래그로 이동, 더블클릭으로 리셋)의 전체 화면 오버레이를 띄운다. 배율·휠 배수(1.15배)·
    /// 확대 한계(딱 맞는 배율의 0.3~8배)까지 ImageCanvas와 맞췄다 — 두 곳에서 "같은 이미지 보기"로
    /// 느껴지게 하려는 것. 챕터(=페이지 새로고침)마다 새로 심어야 해서 매번 다시 주입한다.
    /// </summary>
    private const string EpubImageZoomScript = @"
(function(){
  if (window.__clrZoomInit) return;
  window.__clrZoomInit = true;

  // 브라우저 기본 동작으로는 img를 누른 채 끌면(마우스가 살짝만 움직여도) 이미지를 통째로 끄는
  // OS 드래그가 시작된다 — 확대창을 열려고 클릭하다 이게 먼저 반응하면 이미지가 제자리에서
  // 벗어나 딸려가는 것처럼 보인다. 본문 이미지든 확대창 이미지든 다 막아서, 이미지는 항상 제자리에
  // 그대로 있고 클릭/휠/드래그는 오직 우리가 만든 확대·이동에만 쓰이게 한다.
  document.addEventListener('dragstart', function(e) {
    if (e.target && e.target.tagName === 'IMG') e.preventDefault();
  }, true);

  var overlay = document.createElement('div');
  overlay.id = '__clr_zoom_overlay';
  overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,0.85);display:none;' +
    'align-items:center;justify-content:center;z-index:2147483647;cursor:grab;overflow:hidden;';
  var img = document.createElement('img');
  img.draggable = false;
  img.style.cssText = 'transform-origin:center center;user-select:none;-webkit-user-drag:none;max-width:none;max-height:none;';
  overlay.appendChild(img);
  document.body.appendChild(overlay);

  var scale = 1, fitScale = 1, tx = 0, ty = 0, dragging = false, startX = 0, startY = 0, startTx = 0, startTy = 0;

  function apply() { img.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + scale + ')'; }

  function openFor(src, natW, natH) {
    if (!natW || !natH) return;
    img.src = src;
    img.style.width = natW + 'px';
    img.style.height = natH + 'px';
    fitScale = Math.min(window.innerWidth / natW, window.innerHeight / natH, 1); // ImageCanvas의 '꽉차게 보기'와 동일 — 창보다 작은 그림은 확대하지 않는다
    scale = fitScale; tx = 0; ty = 0;
    apply();
    overlay.style.display = 'flex';
  }
  function close() { overlay.style.display = 'none'; img.src = ''; }

  document.addEventListener('click', function(e) {
    var t = e.target;
    if (t && t.tagName === 'IMG' && t !== img) {
      e.preventDefault(); e.stopPropagation();
      openFor(t.currentSrc || t.src, t.naturalWidth, t.naturalHeight);
    }
  }, true);

  // 책 안의 커버접기 줄(.cover-wrapper 아래 .cover-text): 누르면 표지 그림을 접고, 다시 누르면 펼친다.
  // 이 줄은 책이 마크업만 넣어 둔 것이라 읽는 쪽이 동작을 붙여 줘야 한다.
  var coverStyle = document.createElement('style');
  coverStyle.textContent = '.cover-text{cursor:pointer;user-select:none;-webkit-user-select:none}.cover-text i.icon::after{content:\x27\\25B2\x27;font-style:normal;font-size:0.8em;display:inline-block}';
  document.head.appendChild(coverStyle);
  document.addEventListener('click', function(e) {
    var t = e.target && e.target.closest ? e.target.closest('.cover-text') : null;
    if (!t) return;
    var wrap = t.closest('.cover-wrapper') || t.parentNode;
    var pic = wrap.querySelector('.cover-img, img');
    var folded = wrap.getAttribute('data-clr-folded') === '1';
    if (pic) pic.style.display = folded ? '' : 'none';
    wrap.setAttribute('data-clr-folded', folded ? '0' : '1');
    var first = t.firstChild;
    if (first && first.nodeType === 3) first.nodeValue = folded ? '커버접기 ' : '커버펼치기 ';
    var arrow = t.querySelector('i');
    if (arrow) arrow.style.transform = folded ? '' : 'rotate(180deg)';
    e.preventDefault(); e.stopPropagation();
  }, true);

  overlay.addEventListener('click', function(e) { if (e.target === overlay) close(); });
  img.addEventListener('dblclick', function(e) { e.preventDefault(); scale = fitScale; tx = 0; ty = 0; apply(); });
  document.addEventListener('keydown', function(e) {
    if (overlay.style.display !== 'none' && e.key === 'Escape') close();
  });

  overlay.addEventListener('wheel', function(e) {
    if (overlay.style.display === 'none') return;
    e.preventDefault();
    var factor = e.deltaY < 0 ? 1.15 : (1 / 1.15); // ImageCanvas(OnMouseWheel)와 같은 배율
    scale = Math.max(fitScale * 0.3, Math.min(fitScale * 8, scale * factor));
    apply();
  }, { passive: false });

  overlay.addEventListener('mousedown', function(e) {
    if (overlay.style.display === 'none') return;
    dragging = true; startX = e.clientX; startY = e.clientY; startTx = tx; startTy = ty;
    overlay.style.cursor = 'grabbing';
  });
  window.addEventListener('mousemove', function(e) {
    if (!dragging) return;
    tx = startTx + (e.clientX - startX); ty = startTy + (e.clientY - startY);
    apply();
  });
  window.addEventListener('mouseup', function() { dragging = false; overlay.style.cursor = 'grab'; });
})();
";

    // ───────────────────────── 만화/이미지(zip 또는 이미지 파일) ─────────────────────────

    /// <summary>zip 안의 이미지들을 옵시디언 플러그인처럼 만화 보듯이 한 장씩 넘겨본다.</summary>
    private async void LoadZipImages(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("압축 파일을 여는 중…");
        try
        {
            imageSet?.Dispose();
            var progress = new Progress<double>(loadingOverlay.Report);
            // 화별로 zip이 하나씩 들어있는 "묶음 zip"(예: 노예 부부 이야기.zip 안에 1화.zip, 2화.zip, ...)일 수도 있다.
            // 이 판별 + 실제로 안쪽 zip들을 통째로 읽어들이는 부분이 큰 파일에서 시간이 걸리는 부분이라
            // UI 스레드를 막지 않게 백그라운드에서 돌리고, 진행률은 NestedZipImageSet 쪽에서 보고해준다.
            IImageSet set = await Task.Run(() => NestedZipImageSet.LooksNested(path)
                ? new NestedZipImageSet(path, progress, cts.Token)
                : (IImageSet)new ZipImageSet(path));
            if (set.Count == 0)
            {
                set.Dispose();
                MessageBox.Show(this, "이 zip 안에서 이미지를 찾지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            imageSet = set;
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Image);
            RebuildImagePages();
            imageTitleBase = Path.GetFileNameWithoutExtension(path);
            ShowImagePage(SavedImagePage(path));
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            ShowZipOpenError(path, ex);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
            if (slideshowTimer is { Enabled: false }) StopSlideshow(); // 열기에 실패·취소돼 타이머가 세워진 채면 슬라이드쇼도 끝난 것으로
        }
    }

    /// <summary>이미지 파일 하나를 직접 열면, 같은 폴더 안의 이미지들도 같이 넘겨볼 수 있게 묶는다.</summary>
    private void LoadSingleImageFile(string path)
    {
        try
        {
            imageSet?.Dispose();
            string? dir = Path.GetDirectoryName(path);
            var set = dir != null ? FolderImageSet.FromDirectory(dir) : null;
            if (set == null || set.Count == 0)
            {
                MessageBox.Show(this, "이미지를 열지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            imageSet = set;
            int rawIndex = Math.Max(0, set.IndexOfPath(path));
            currentPath = path;
            SetContentMode(ContentKind.Image);
            RebuildImagePages();
            imageTitleBase = Path.GetFileName(path);
            JumpToImageItem(rawIndex);
        }
        catch (Exception ex)
        {
            StopSlideshow(); // OpenPath가 세워둔 슬라이드쇼 타이머를 마저 정리
            MessageBox.Show(this, $"이미지를 여는 데 실패했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 옵시디언 플러그인의 "순서 / 나누기" 그대로: 실제로 넘겨가며 볼 "페이지" 목록을 미리 만들어둔다.
    /// 화살표/버튼은 항상 이 목록을 순서대로만 넘기고, 읽는 방향에 따른 뒤집기는 여기서 미리 반영해둔다.
    /// - 왼→오: 파일 순서 그대로
    /// - 오→왼(일본식): 2장씩 묶어서 그 안에서 순서를 뒤집는다(2,1,4,3…)
    /// - 왼→오(2개로 나누기): 이미지 1장을 좌/우로 쪼개 좌→우로(1좌,1우,2좌,2우…)
    /// - 오→왼(2개로 나누기): 위와 같되 반쪽 순서를 우→좌로(1우,1좌,2우,2좌…)
    /// </summary>
    private void RebuildImagePages()
    {
        // 페이지 번호 ↔ 실제 이미지 대응이 통째로 바뀌므로, 예전 번호로 채워둔 프리페치 캐시는 여기서 버린다.
        ClearImageCache();
        imagePages.Clear();
        if (imageSet == null) return;
        int n = imageSet.Count;
        bool spread = imageOrder is ReadingOrder.LtrSpread or ReadingOrder.RtlSpread;
        bool rtl = imageOrder is ReadingOrder.Rtl or ReadingOrder.RtlSpread;

        if (spread)
        {
            for (int i = 0; i < n; i++)
            {
                if (rtl) { imagePages.Add((i, ImageHalf.Right)); imagePages.Add((i, ImageHalf.Left)); }
                else { imagePages.Add((i, ImageHalf.Left)); imagePages.Add((i, ImageHalf.Right)); }
            }
        }
        else if (imageDouble)
        {
            // 두쪽 보기: 연속한 두 장을 한 화면으로(좌우 순서는 LoadPageImage에서 읽는 방향대로 이어 붙인다). 홀수 장이면 마지막은 혼자.
            for (int i = 0; i < n; i += 2)
                imagePages.Add((i, i + 1 < n ? ImageHalf.Pair : ImageHalf.None));
        }
        else if (rtl)
        {
            for (int i = 0; i < n; i += 2)
            {
                if (i + 1 < n) { imagePages.Add((i + 1, ImageHalf.None)); imagePages.Add((i, ImageHalf.None)); }
                else imagePages.Add((i, ImageHalf.None));
            }
        }
        else
        {
            for (int i = 0; i < n; i++) imagePages.Add((i, ImageHalf.None));
        }
    }

    /// <summary>챕터 목록에서 실제 이미지 파일(원래 순서 기준)을 골랐을 때: 지금 순서/나누기 설정에서
    /// 그 이미지가 있는 페이지를 찾아서 연다(옵시디언 플러그인의 pageIndexForItem과 동일).</summary>
    private void JumpToImageItem(int rawIndex)
    {
        // 두쪽 보기의 한 화면은 그림 두 장(ItemIndex, ItemIndex+1)이므로 뒤쪽 그림으로도 찾을 수 있어야 한다.
        int pageIdx = imagePages.FindIndex(p => p.ItemIndex == rawIndex || (p.Half == ImageHalf.Pair && p.ItemIndex + 1 == rawIndex));
        ShowImagePage(pageIdx >= 0 ? pageIdx : 0);
    }

    /// <summary>만화 모드 제목표시줄을 반디뷰처럼 "[현재/전체] 이름 — {AppShared.AppDisplayName}"로 맞춘다.</summary>
    private void UpdateImageTitle()
    {
        if (!imageMode || imagePages.Count == 0) return;
        Text = $"[{imageIndex + 1}/{imagePages.Count}] {imageTitleBase} — {AppShared.AppDisplayName}";
    }

    private void ShowImagePage(int pageIndex)
    {
        if (imageSet == null || imagePages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, imagePages.Count - 1);
        imageIndex = pageIndex;
        var (itemIndex, half) = imagePages[pageIndex];
        try
        {
            // 미리 불러오기 캐시에 이미 있으면(대부분의 경우) 그걸 바로 쓰고, 없으면(캐시가 못 따라왔으면)
            // 예전처럼 지금 이 자리에서 동기적으로 불러온다 — 안전망이라 항상 뜨는 건 보장된다.
            Image shown = TakeFromImageCache(pageIndex) ?? LoadPageImage(pageIndex);
            imageCanvas.SetImage(shown);
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"이미지를 불러오지 못했습니다: {ex.Message}";
            return;
        }

        string halfLabel = half == ImageHalf.Left ? " [좌측]" : half == ImageHalf.Right ? " [우측]" : "";
        string name = imageSet.Name(itemIndex) + halfLabel;
        if (half == ImageHalf.Pair && itemIndex + 1 < imageSet.Count) name += " · " + imageSet.Name(itemIndex + 1);
        pageIndicatorLabel.Text = $"{pageIndex + 1} / {imagePages.Count}";
        pageScrubberBar.SetProgress(pageIndex, imagePages.Count);
        statusLabel.Text = name;
        // 반디뷰처럼 "[현재/전체]"를 그림 위 모서리와 제목표시줄에도 — 위치 막대·상태줄이 숨는
        // 페이지 터치·몰입 모드에서도 지금 몇 쪽인지 보이게.
        imageCanvas.PageInfoText = $"[{pageIndex + 1}/{imagePages.Count}]";
        UpdateImageTitle();
        RefreshNavPosition();
        imageCanvas.Focus(); // 화살표 키가 바로 먹도록

        // 슬라이드쇼 도중 직접 넘겼다면(화살표·위치 막대 등) 다음 자동 넘김까지의 대기시간을 지금부터 다시 잰다.
        if (slideshowTimer != null) { slideshowTimer.Stop(); slideshowTimer.Start(); }

        SchedulePrefetch(pageIndex);
    }

    /// <summary>이미지를 좌/우 반쪽으로 잘라낸다. disposeFull이 true면(기본값) 원본을 다 쓴 걸로 보고 정리한다
    /// — 스프레드 모드에서 양쪽을 다 잘라낼 땐 마지막 한 번만 true로 줘서 원본을 한 번만 디코딩·정리한다.</summary>
    private static Image CropHalf(Image full, ImageHalf half, bool disposeFull = true)
    {
        int halfWidth = Math.Max(1, full.Width / 2);
        var cropped = new Bitmap(halfWidth, full.Height);
        using (var g = Graphics.FromImage(cropped))
        {
            var src = half == ImageHalf.Left
                ? new Rectangle(0, 0, halfWidth, full.Height)
                : new Rectangle(full.Width - halfWidth, 0, halfWidth, full.Height);
            g.DrawImage(full, new Rectangle(0, 0, halfWidth, full.Height), src, GraphicsUnit.Pixel);
        }
        if (disposeFull) full.Dispose();
        return cropped;
    }

    /// <summary>두 그림을 나란히 이어 붙인 비트맵을 만든다(두쪽 보기). 높이가 다르면 큰 쪽 높이에 맞춰 비율대로 키우고,
    /// 두 그림은 다 쓴 뒤 정리한다.</summary>
    private static Image ComposePair(Image left, Image right)
    {
        try
        {
            int h = Math.Max(left.Height, right.Height);
            int lw = Math.Max(1, (int)Math.Round((double)left.Width * h / left.Height));
            int rw = Math.Max(1, (int)Math.Round((double)right.Width * h / right.Height));
            var result = new Bitmap(lw + rw, h);
            using var g = Graphics.FromImage(result);
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(left, new Rectangle(0, 0, lw, h));
            g.DrawImage(right, new Rectangle(lw, 0, rw, h));
            return result;
        }
        finally
        {
            left.Dispose();
            right.Dispose();
        }
    }

    /// <summary>페이지 하나를 실제로 디코딩한다(필요하면 반쪽 자르기까지). 지금 당장 보여주기용 동기 디코딩과
    /// 백그라운드 미리 불러오기가 모두 이 메서드 하나를 거친다.
    /// zip 안 압축 해제(공유 스트림을 건드리는 부분)만 락으로 감싸 한 번에 하나씩 하고, 정작 시간이 오래
    /// 걸리는 디코딩은 락 밖에서 한다 — 그래야 여러 페이지를 동시에(다른 코어에서) 미리 디코딩할 수 있다.</summary>
    private Image LoadPageImage(int pageIndex)
    {
        var (itemIndex, half) = imagePages[pageIndex];
        byte[] bytes;
        lock (imageLoadLock)
        {
            bytes = imageSet!.LoadRawBytes(itemIndex);
        }
        Image full = ImageLoader.Decode(bytes, imageSet!.Name(itemIndex));
        if (half == ImageHalf.None) return full;

        if (half == ImageHalf.Pair)
        {
            // 두쪽 보기: 다음 그림도 불러와 나란히 이어 붙인다. 오른쪽→왼쪽 읽기면 앞 장이 오른쪽에 온다.
            byte[] bytes2;
            lock (imageLoadLock) { bytes2 = imageSet.LoadRawBytes(itemIndex + 1); }
            Image second = ImageLoader.Decode(bytes2, imageSet.Name(itemIndex + 1));
            bool rtlOrder = imageOrder is ReadingOrder.Rtl or ReadingOrder.RtlSpread;
            return rtlOrder ? ComposePair(second, full) : ComposePair(full, second);
        }

        // 2개로 나누기(스프레드) 모드: 좌/우 페이지가 원래 같은 원본 이미지 한 장에서 나온다.
        // 요청받은 쪽만 잘라내고 원본을 버리면, 곧이어(대부분 바로 다음/이전 페이지로) 반대쪽을
        // 불러올 때 같은 원본을 또 디코딩하게 된다. 그래서 여기서 반대쪽도 같이 잘라 캐시에
        // 넣어두고, 원본은 두 쪽을 다 자른 뒤에야 정리한다.
        int siblingIndex = FindSiblingSpreadPage(pageIndex, itemIndex);
        if (siblingIndex >= 0)
        {
            ImageHalf siblingHalf = half == ImageHalf.Left ? ImageHalf.Right : ImageHalf.Left;
            Image siblingImg = CropHalf(full, siblingHalf, disposeFull: false);
            lock (imageCacheLock)
            {
                if (imagePageCache.ContainsKey(siblingIndex)) siblingImg.Dispose(); // 이미 딴 경로로 채워졌으면 이걸 버린다
                else imagePageCache[siblingIndex] = siblingImg;
            }
        }
        return CropHalf(full, half, disposeFull: true);
    }

    /// <summary>스프레드 모드에서 지금 페이지(pageIndex)와 같은 원본 이미지(itemIndex)를 공유하는
    /// 반대쪽 반쪽 페이지 번호를 찾는다. RebuildImagePages가 스프레드 페이지 둘을 항상 나란히
    /// 넣어두므로 바로 앞/뒤만 보면 된다. 못 찾으면(예: 목록 맨 끝) -1.</summary>
    private int FindSiblingSpreadPage(int pageIndex, int itemIndex)
    {
        if (pageIndex + 1 < imagePages.Count && imagePages[pageIndex + 1].ItemIndex == itemIndex) return pageIndex + 1;
        if (pageIndex - 1 >= 0 && imagePages[pageIndex - 1].ItemIndex == itemIndex) return pageIndex - 1;
        return -1;
    }

    /// <summary>프리페치 캐시에서 꺼내온다(있으면 캐시에서 제거하고 소유권을 넘겨준다).</summary>
    private Image? TakeFromImageCache(int pageIndex)
    {
        lock (imageCacheLock)
        {
            if (imagePageCache.Remove(pageIndex, out var img)) return img;
        }
        return null;
    }

    /// <summary>지금 페이지를 기준으로 앞뒤 몇 장을 백그라운드 스레드에서 미리 디코딩해 캐시에 채워둔다.
    /// 실제로 넘기는 순간엔 이미 다 돼 있어서(캐시 적중) 압축 풀기·디코딩을 기다리지 않고 바로 뜬다.</summary>
    private void SchedulePrefetch(int centerIndex)
    {
        if (imageSet == null) return;
        var imageSetSnapshot = imageSet; // 백그라운드 작업이 끝났을 때 다른 파일로 이미 넘어갔는지 확인하는 용도

        var targets = new List<int>();
        for (int d = 1; d <= PrefetchAhead; d++) if (centerIndex + d < imagePages.Count) targets.Add(centerIndex + d);
        for (int d = 1; d <= PrefetchBehind; d++) if (centerIndex - d >= 0) targets.Add(centerIndex - d);

        foreach (int target in targets)
        {
            lock (imageCacheLock)
            {
                // 이미 캐시에 있거나, 다른 백그라운드 작업이 이미 이 페이지를 디코딩하는 중이면 또 예약하지 않는다.
                // (안 그러면 화살표를 몇 번만 눌러도 같은 페이지를 여러 번 겹쳐서 디코딩하느라 대기줄만 길어졌다.)
                if (imagePageCache.ContainsKey(target) || pendingPrefetch.Contains(target)) continue;
                pendingPrefetch.Add(target);
            }

            Task.Run(() =>
            {
                try
                {
                    // 락을 잡기 전에 먼저 지금도 필요한 페이지인지 확인 — 이미 너무 멀어졌으면 시작도 안 한다.
                    if (imageSet != imageSetSnapshot || Math.Abs(target - imageIndex) > CacheKeepRadius) return;

                    Image img;
                    try { img = LoadPageImage(target); }
                    catch { return; } // 미리 불러오기 실패는 조용히 넘어간다 — 실제로 그 페이지로 갈 때 다시 시도된다

                    // 디코딩하는 사이 다른 파일이 열렸거나 너무 멀리 넘어갔으면 그냥 버린다(메모리만 차지하니까).
                    if (imageSet != imageSetSnapshot || Math.Abs(target - imageIndex) > CacheKeepRadius)
                    {
                        img.Dispose();
                        return;
                    }

                    lock (imageCacheLock)
                    {
                        if (imagePageCache.ContainsKey(target)) { img.Dispose(); return; }
                        imagePageCache[target] = img;
                    }
                }
                finally
                {
                    lock (imageCacheLock) { pendingPrefetch.Remove(target); }
                }
            });
        }

        TrimImageCache(centerIndex);
    }

    /// <summary>지금 페이지에서 너무 멀어진 캐시는 정리한다(만화 한 편이 수백 장이어도 메모리가 계속 안 쌓이게).</summary>
    private void TrimImageCache(int centerIndex)
    {
        lock (imageCacheLock)
        {
            var stale = imagePageCache.Keys.Where(k => Math.Abs(k - centerIndex) > CacheKeepRadius).ToList();
            foreach (var k in stale)
            {
                imagePageCache[k].Dispose();
                imagePageCache.Remove(k);
            }
        }
    }

    private void ClearImageCache()
    {
        lock (imageCacheLock)
        {
            foreach (var img in imagePageCache.Values) img.Dispose();
            imagePageCache.Clear();
        }
    }

    private void ChangeImage(int delta)
    {
        if (imagePages.Count == 0) return;
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        int next = imageIndex + delta;
        if (next < 0)
        {
            if (delta < 0) HandleBookEdge(forward: false); // 첫 페이지에서 더 되돌리려 했을 때
            return;
        }
        if (next >= imagePages.Count)
        {
            if (delta > 0) HandleBookEdge(forward: true); // 마지막 페이지에서 더 오른쪽으로 넘기려 했을 때
            return;
        }
        ShowImagePage(next);
    }

    /// <summary>보기 > 보기 방식에서 고른 값을 설정에 저장하고 열려 있는 모든 창에 알린다.</summary>
    private void ChooseImageLayout(bool dbl)
    {
        settings.ImageDoublePage = dbl;
        settings.Save();
        AppShared.NotifySettingsChanged("imagelayout");
    }

    /// <summary>한쪽/두쪽 보기가 바뀌었을 때: 메뉴 체크를 맞추고, 보던 그림이 있는 화면으로 페이지 목록을 다시 만든다.</summary>
    private void ApplyImageLayout()
    {
        imageDouble = settings.ImageDoublePage;
        foreach (var item in imageLayoutMenuItems) item.Checked = item.Tag is bool b && b == imageDouble;
        if (imageSet == null) return;
        int rawIndex = imagePages.Count > 0 ? imagePages[Math.Clamp(imageIndex, 0, imagePages.Count - 1)].ItemIndex : 0;
        RebuildImagePages();
        JumpToImageItem(rawIndex);
    }

    private void AddImageOrderItem(ToolStripMenuItem parent, string label, ReadingOrder order)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetImageReadingOrder(order))
        {
            Tag = order,
            Checked = order == imageOrder,
        };
        imageOrderMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    /// <summary>보기 > 순서 / 나누기를 바꾸면 페이지 목록을 다시 짜고, 지금 보던 실제 이미지가 있는 페이지로 되돌아간다.</summary>
    private void SetImageReadingOrder(ReadingOrder order)
    {
        imageOrder = order;
        foreach (var item in imageOrderMenuItems) item.Checked = item.Tag is ReadingOrder o && o == order;
        if (imageSet == null) return;
        int rawIndex = imagePages.Count > 0 ? imagePages[Math.Clamp(imageIndex, 0, imagePages.Count - 1)].ItemIndex : 0;
        RebuildImagePages();
        JumpToImageItem(rawIndex);
    }

    private void AddImageViewModeItem(ToolStripMenuItem parent, string label, ImageViewMode mode)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetImageViewMode(mode))
        {
            Tag = mode,
            Checked = mode == ImageViewMode.FitWindowUpscale, // ImageCanvas의 기본값과 일치
        };
        imageViewModeMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    private void SetImageViewMode(ImageViewMode mode)
    {
        if (mode == ImageViewMode.Custom)
        {
            int? pct = AskCustomImagePercent(imageCanvas.CustomPercent);
            if (pct == null) return; // 취소하면 지금 보기 모드를 그대로 둔다
            imageCanvas.CustomPercent = pct.Value;
        }
        foreach (var item in imageViewModeMenuItems) item.Checked = item.Tag is ImageViewMode m && m == mode;
        imageCanvas.SetViewMode(mode);
    }

    // ───────────────────────── 슬라이드쇼(만화 자동 넘김) ─────────────────────────

    private void ToggleSlideshow()
    {
        if (slideshowTimer != null) { StopSlideshow(); return; }
        if (!imageMode || imagePages.Count == 0) return;

        slideshowIntervalSeconds = Math.Clamp(settings.SlideshowSeconds, 1, 120);
        slideshowTimer = new System.Windows.Forms.Timer { Interval = slideshowIntervalSeconds * 1000 };
        slideshowTimer.Tick += (_, _) => SlideshowTick();
        slideshowTimer.Start();
        UpdateAutoPlayMenu();
        statusLabel.Text = $"슬라이드쇼 시작 ({slideshowIntervalSeconds}초 간격)";
    }

    private void StopSlideshow()
    {
        if (slideshowTimer == null) return;
        slideshowTimer.Stop();
        slideshowTimer.Dispose();
        slideshowTimer = null;
        UpdateAutoPlayMenu();
    }

    private void SlideshowTick()
    {
        if (!imageMode || imagePages.Count == 0) { StopSlideshow(); return; }
        if (imageIndex >= imagePages.Count - 1) { HandleBookEdge(forward: true); return; }
        ChangeImage(1);
    }

    private void SetSlideshowInterval(int seconds)
    {
        slideshowIntervalSeconds = seconds;
        settings.SlideshowSeconds = seconds;
        settings.Save();
        foreach (var item in slideshowIntervalMenuItems) item.Checked = item.Text == $"{seconds}초마다";
        if (slideshowTimer != null) slideshowTimer.Interval = seconds * 1000;
    }

    // ───────────────────────── 마지막 페이지에서 ─────────────────────────

    private void AddEndOfBookItem(ToolStripMenuItem parent, string label, EndOfBookAction action)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetEndOfBookAction(action))
        {
            Tag = action,
            Checked = action == endOfBookAction,
        };
        endOfBookMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    private void SetEndOfBookAction(EndOfBookAction action)
    {
        endOfBookAction = action;
        foreach (var item in endOfBookMenuItems) item.Checked = item.Tag is EndOfBookAction a && a == action;
    }

    // 책의 끝·처음에 닿았을 때의 처리(HandleBookEdge)와 옆 파일·폴더 찾기는 MainForm.Edge.cs에 있다.

    /// <summary>txt/epub/이미지/pdf 화면 중 하나만 보이게 하고, 그 모드에 안 맞는 메뉴들은 잠그거나 푼다.</summary>
    private enum ContentKind { Txt, Epub, Image, Pdf }

    private void SetContentMode(ContentKind kind)
    {
        // 만화와 소설은 각자 설정의 기본 읽기 방식으로 시작한다. 같은 종류끼리 넘어갈 땐(만화 → 만화,
        // txt ↔ epub) 읽는 중에 바꾼 방식을 그대로 이어가고, 다른 종류에서 들어올 때만 기본값을 다시 적용한다.
        static bool IsNovel(ContentKind? k) => k is ContentKind.Txt or ContentKind.Epub;
        if (kind == ContentKind.Image && shownKind != ContentKind.Image)
        {
            tapToTurnEnabled = settings.ImagePageTouch;
        }
        else if (kind == ContentKind.Txt && directEdit)
        {
            // 메모장처럼 바로 쓰는 txt는 쪽 넘김이 없다(스크롤만).
            epubPageMode = false;
            pageMode = false;
            tapToTurnEnabled = false;
            ApplyEditorWrap();
        }
        else if (IsNovel(kind) && (!IsNovel(shownKind) || lastKindDirect))
        {
            // 여기서는 값만 정해둔다 — 불러온 쪽(ShowLoadedText · LoadEpubFile)이 이 값대로 본문을 그린다.
            // epub · zip 속 소설 · 한글 문서에서 뽑은 글은 웹 화면(다단 배치)이라 설정의 기본 읽기 방식(스크롤/페이지/페이지 터치)을 따른다.
            epubPageMode = settings.NovelReadingMode != NovelReadingMode.Scroll;
            pageMode = false;
            tapToTurnEnabled = settings.NovelReadingMode == NovelReadingMode.PageTouch;
            ApplyEditorWrap();
        }
        lastKindDirect = kind == ContentKind.Txt && directEdit;
        shownKind = kind;

        if (kind != ContentKind.Txt) { txtWeb = false; directEdit = false; }
        bool web = kind == ContentKind.Epub || (kind == ContentKind.Txt && txtWeb);
        epubMode = web;
        imageMode = kind == ContentKind.Image;
        editorHost.Visible = kind == ContentKind.Txt && !txtWeb;
        epubHost.Visible = web;
        imageHost.Visible = kind == ContentKind.Image;
        pdfHost.Visible = kind == ContentKind.Pdf;
        ApplySideMargin(); // 회차 글쓰기 칸 · 제목 줄을 켜고 끈다

        // 찾기/복사/모두 선택/실행취소/잘라내기/붙여넣기/삭제는 mainEditor 전용 기능이라 txt가 아니면 잠근다.
        bool isTxt = kind == ContentKind.Txt;
        // txt 웹 화면에서는 찾기만 된다(복사 · 모두 선택은 웹 화면 안에서 Ctrl+C · Ctrl+A로).
        foreach (var item in txtOnlyMenuItems) item.Enabled = isTxt && (!txtWeb || item == findMenuItem);
        foreach (var item in editOnlyMenuItems) item.Enabled = isTxt && !txtWeb && !mainEditor.ReadOnly;
        // 이미지(만화)·pdf는 수정·글자 크기·이미지 표시·저장 개념이 아예 없어서 다 잠근다(pdf는 뷰어
        // 자체 툴바에 인쇄·저장이 이미 있다). 읽기 방식은 이제 만화도 고를 수 있지만(페이지/페이지 터치),
        // 스크롤만은 만화에 없는 개념이라 그 항목만 잠그고, pdf는 페이지 넘김 자체를 뷰어가 알아서
        // 하므로 읽기 방식 메뉴를 통째로 잠근다.
        bool notReflowable = kind == ContentKind.Image || kind == ContentKind.Pdf;
        editModeMenuItem.Enabled = !notReflowable;
        scrollModeMenuItem.Enabled = kind != ContentKind.Image;
        pageModeMenuItem.Enabled = pageTouchModeMenuItem.Enabled = !(kind == ContentKind.Txt && directEdit); // 바로 쓰는 txt는 쪽 넘김이 없다
        readingModeMenu.Enabled = kind != ContentKind.Pdf;
        fontSizeMenu.Enabled = !notReflowable;
        showImagesMenuItem.Enabled = kind == ContentKind.Epub;
        saveMenuItem.Enabled = !notReflowable;
        saveAsMenuItem.Enabled = !notReflowable;
        orderMenu.Enabled = kind == ContentKind.Image; // "순서 / 나누기"는 만화(이미지) 모드 전용
        layoutMenu.Enabled = kind == ContentKind.Image; // "보기 방식"도 만화(이미지) 모드 전용
        viewModeMenu.Enabled = kind == ContentKind.Image; // "보기 모드"도 만화(이미지) 모드 전용
        slideshowMenu.Enabled = kind == ContentKind.Image; // "만화 넘김 간격"도 마찬가지
        endOfBookMenu.Enabled = kind != ContentKind.Pdf; // "마지막 · 첫 페이지에서"는 만화와 소설 모두(pdf는 뷰어가 알아서 넘긴다)
        if (kind != ContentKind.Image) StopSlideshow(); // 다른 모드로 나가면 자동 넘김도 멈춘다

        // 탐색 막대의 버튼 이름(챕터 / 이미지 선택)과 쓸 수 있는 것을 맞춘다. pdf는 챕터 목록 · 위치 막대를 잠근다.
        navKind = kind;
        ApplyLookBarShown();
        ConfigureNavBar(kind);

        // 지금 모드에 맞는 스크롤/페이지/페이지 터치 체크로 다시 맞춘다. 이미지(만화)는 항상 "페이지
        // 넘기기" 방식이라 스크롤은 나올 수 없고, pdf는 뷰어가 자체적으로 페이지를 넘기므로 우리 쪽
        // 화살표·표시줄은 아예 띄우지 않는다. 터치 켜짐 여부(tapToTurnEnabled)는 모드를 넘나들어도 그대로 이어간다.
        bool currentIsPage = kind switch { ContentKind.Image => true, ContentKind.Epub => epubPageMode, ContentKind.Pdf => false, _ => txtWeb ? epubPageMode : pageMode };
        UpdateReadingModeMenuChecks(!currentIsPage ? ReadingMode.Scroll : tapToTurnEnabled ? ReadingMode.PageTouch : ReadingMode.Page);
        UpdatePageNavVisibility(kind != ContentKind.Pdf && currentIsPage && !tapToTurnEnabled);
        imageCanvas.TapToTurnEnabled = tapToTurnEnabled;

        // 만화(이미지) 모드에서는 하단 표시줄에 텍스트 대신 꿀뷰 스타일 위치 막대를 보여준다. pdf는 뷰어
        // 자체 툴바가 위치를 보여주니 둘 다 끈다.
        bool showScrubber = kind == ContentKind.Image;
        pageScrubberBar.Visible = showScrubber;
        pageIndicatorLabel.Visible = !showScrubber && kind != ContentKind.Pdf;

        if (kind != ContentKind.Txt) findBar.Hide();
        if (kind == ContentKind.Image) imageCanvas.Focus();
        UpdateFeatureMenus(kind);
    }

    /// <summary>이전/다음 화살표·하단 위치 표시줄을 켜고 끄고, 위치를 다시 맞춘다. txt/epub/만화 세 모드가
    /// 화살표를 공용으로 쓴다(동그란 버튼 오버레이 — 본문/그림 폭은 안 줄이고 그 위에 그냥 얹는다).
    /// 화살표는 여기서 바로 띄우지 않고, 쓸 수 있는 상태일 때만 ArrowHoverTick이 마우스 위치를 보며 띄운다.</summary>
    private void UpdatePageNavVisibility(bool visible)
    {
        pageNavVisible = visible;
        // 탐색 막대가 켜져 있으면 위치 막대 · "약 N% 지점" 표시줄은 탐색 막대가 대신하므로 겹쳐 띄우지 않는다.
        pageNavPanel.Visible = visible && !settings.NavBarEnabled;
        prevNavBtn.Visible = false;
        nextNavBtn.Visible = false;
        if (visible)
        {
            RepositionOverlays();
            arrowHoverTimer.Start();
            ArrowHoverTick(); // 이미 가장자리에 마우스가 있으면 기다리지 않고 바로 띄운다
        }
        else arrowHoverTimer.Stop();
    }

    /// <summary>마우스가 좌우 넘김 버튼 근처(버튼 둘레 ArrowNearRadius 안)에 오면 그쪽 버튼만 나타나고,
    /// 벗어나면 잠깐 뒤에 다시 숨는다. epub(WebView2) 위에서는 마우스 이벤트가 이 폼까지 안 올라오니,
    /// 이벤트 대신 커서 위치를 주기적으로 살핀다(탐색 막대의 OnNavTimer와 같은 방식).</summary>
    private const int ArrowNearRadius = 110;
    private const int ArrowHideDelayMs = 500;

    private void ArrowHoverTick()
    {
        if (!pageNavVisible) return;
        var p = PointToClient(Cursor.Position);
        bool active = (ContainsFocus || IsHostActive) && contentHost.Bounds.Contains(p);
        UpdateArrowHover(prevNavBtn, active, p, ref prevNavLastNear);
        UpdateArrowHover(nextNavBtn, active, p, ref nextNavLastNear);
    }

    private static void UpdateArrowHover(ArrowOverlayButton btn, bool active, Point p, ref DateTime lastNear)
    {
        var zone = btn.Bounds;
        zone.Inflate(ArrowNearRadius, ArrowNearRadius);
        bool near = active && zone.Contains(p);
        if (near) lastNear = DateTime.Now;

        if (near && !btn.Visible)
        {
            btn.Visible = true;
            btn.BringToFront();
        }
        else if (!near && btn.Visible && (DateTime.Now - lastNear).TotalMilliseconds > ArrowHideDelayMs)
        {
            btn.Visible = false;
        }
    }

    // ───────────────────────── 보기: 읽기 방식(스크롤/페이지/페이지 터치) ─────────────────────────

    private enum ReadingMode { Scroll, Page, PageTouch }

    /// <summary>보기 > 읽기 방식 메뉴는 txt/epub/만화 공용이라, 지금 어느 쪽을 보고 있는지에 따라
    /// 알맞은 쪽으로 보낸다. 터치 켜짐 여부(tapToTurnEnabled)와 화살표 표시는 여기서 한 번에 정한다 —
    /// 페이지 터치를 고르면 화면 전체가 곧 "버튼"이니 작은 화살표는 굳이 같이 안 보여준다.</summary>
    private void SetReadingMode(ReadingMode mode)
    {
        bool isPage = mode != ReadingMode.Scroll;
        bool wasTapEnabled = tapToTurnEnabled;
        tapToTurnEnabled = mode == ReadingMode.PageTouch;
        imageCanvas.TapToTurnEnabled = tapToTurnEnabled;

        if (epubMode)
        {
            if (isPage == epubPageMode && tapToTurnEnabled != wasTapEnabled)
            {
                // 다단 배치 자체는 안 바뀌고(페이지 ↔ 페이지 터치) 터치 켜짐 여부만 바뀐 거면, 챕터를
                // 통째로 다시 불러와 위치가 처음으로 돌아가지 않도록 지금 켜진 스크립트만 살짝 갱신한다.
                PushEpubTapEnabled();
                UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
            }
            else
            {
                SetEpubPageMode(isPage); // 내부에서 챕터를 다시 불러오며, 지금 tapToTurnEnabled 기준으로 스크립트를 새로 심는다.
            }
        }
        else if (!imageMode)
        {
            SetTxtPageMode(isPage);
        }
        else
        {
            UpdatePageNavVisibility(!tapToTurnEnabled); // 만화는 항상 "페이지" — 터치 켜짐 여부만 화살표 표시에 영향을 준다.
        }

        UpdateReadingModeMenuChecks(mode);
    }

    private void UpdateReadingModeMenuChecks(ReadingMode mode)
    {
        scrollModeMenuItem.Checked = mode == ReadingMode.Scroll;
        pageModeMenuItem.Checked = mode == ReadingMode.Page;
        pageTouchModeMenuItem.Checked = mode == ReadingMode.PageTouch;
    }

    /// <summary>epub 읽기 방식 전환 — 지금 챕터를 다시 불러와서 다단 배치/바닥 감지 스크립트를 깨끗하게 새로 심는다.</summary>
    private void SetEpubPageMode(bool isPage)
    {
        epubPageMode = isPage;
        UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
        ReloadEpubKeepingPlace();
    }

    /// <summary>
    /// 스크롤: 지금까지처럼 mainEditor에 전체 글을 담아 이어서 훑어본다.
    /// 페이지: 지금 화면에 실제로 몇 글자가 들어가는지 직접 재서(옵시디언 플러그인처럼 화면에 꽉 차게)
    /// 그만큼만 보여주고, 아래 이동줄로 앞뒤 페이지를 넘긴다.
    /// 어느 쪽이든 지금 보고 있던 위치는 최대한 그대로 유지한 채로 전환한다.
    /// </summary>
    private void SetTxtPageMode(bool isPage)
    {
        if (isPage != pageMode)
        {
            if (isPage)
            {
                int offset = mainEditor.SelectionStart; // 스크롤 상태라 지금 이게 전체 글 기준 위치
                pageMode = true;
                ApplyEditorWrap(); // 페이지 방식은 줄바꿈이 켜져 있어야 한 화면에 들어가는 글자 수를 잴 수 있다
                currentPageStart = offset;
                currentPageEnd = FindPageEnd(currentPageStart);
                ShowCurrentPage();
                mainEditor.SelectionStart = 0;
            }
            else
            {
                int offset = currentPageStart;
                pageMode = false;
                mainEditor.ScrollLocked = false; // 아래 ScrollToCaret이 막히지 않게 먼저 푼다
                mainEditor.Text = fullText;
                mainEditor.SelectionStart = Math.Clamp(offset, 0, mainEditor.Text.Length);
            }
            mainEditor.SelectionLength = 0;
            mainEditor.ScrollToCaret();
            if (!isPage) ApplyEditorWrap(); // 스크롤로 돌아오면 사용자가 정한 줄바꿈 설정으로
        }

        UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
    }

    /// <summary>찾기 바·좌우 넘김 화살표처럼 Dock 없이 떠 있는 오버레이들의 위치를 창 크기에 맞춰 다시 잡는다.
    /// 화살표는 본문/그림 폭을 줄이지 않고 그 위에 그냥 겹쳐 그린다(자리를 마련해주지 않는다).</summary>
    private void RepositionOverlays()
    {
        // contentHost 기준으로 놓는다(Form에 직접 자식으로 달린 건 contentHost 쪽이라, 좌표계가 같다).
        // editorHost는 그 안에 한 겹 더 있는 Dock=Fill 자식이라 여기 좌표계로 쓰면 어긋난다.
        findBar.Left = ClientSize.Width - findBar.Width - 24;
        findBar.Top = contentHost.Top + 10;

        int centerY = contentHost.Top + contentHost.Height / 2 - prevNavBtn.Height / 2;
        prevNavBtn.Left = contentHost.Left + 16;
        prevNavBtn.Top = centerY;
        nextNavBtn.Left = contentHost.Right - nextNavBtn.Width - 16;
        nextNavBtn.Top = centerY;

        loadingOverlay.Bounds = contentHost.Bounds;

        // z-order도 겹쳐 그릴 때 확실히 보장이 안 될 때가 있어서, 위치를 다시 잡을 때마다
        // 맨 앞으로 명시적으로 올려준다.
        prevNavBtn.BringToFront();
        nextNavBtn.BringToFront();
        findBar.BringToFront();
        if (loadingOverlay.Visible) loadingOverlay.BringToFront();
        if (edgePrompt.Visible) { PlaceEdgePrompt(); edgePrompt.BringToFront(); }
    }

    private void ShowCurrentPage()
    {
        mainEditor.Text = fullText.Substring(currentPageStart, currentPageEnd - currentPageStart);
        int percent = fullText.Length == 0 ? 100 : (int)(100.0 * currentPageStart / fullText.Length);
        pageIndicatorLabel.Text = $"약 {percent}% 지점";
        RefreshNavPosition();
    }

    private void ChangePage(int delta)
    {
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        if (ttsActive && !ttsInternalNav) StopTts(); // 낭독 중에 직접 페이지를 넘기면 낭독은 멈춘다
        // 페이지 방식으로 읽는 중 사용자가 직접 끝(처음)을 넘기려 할 때만 옆 파일 처리로 넘긴다(낭독이 스스로 넘기는 건 끝에 닿기 전에 멈춘다).
        bool atEdgeHandled = editorHost.Visible && pageMode && !ttsInternalNav;
        if (delta > 0)
        {
            if (currentPageEnd >= fullText.Length) // 이미 마지막 페이지
            {
                if (atEdgeHandled) HandleBookEdge(forward: true);
                return;
            }
            currentPageStart = currentPageEnd;
            currentPageEnd = FindPageEnd(currentPageStart);
        }
        else
        {
            if (currentPageStart <= 0) // 이미 첫 페이지
            {
                if (atEdgeHandled) HandleBookEdge(forward: false);
                return;
            }
            int end = currentPageStart;
            currentPageStart = FindPageStart(end);
            currentPageEnd = end;
        }
        ShowCurrentPage();
        mainEditor.SelectionStart = 0;
        mainEditor.ScrollToCaret();
    }

    /// <summary>글자 크기·창 폭 기준으로 "이 정도면 한 페이지 되겠다" 하는 대략적인 시작 추정치.
    /// 이진 탐색의 첫 탐색 범위를 잡는 용도일 뿐이라, 정확하지 않아도 결과의 정확도엔 영향 없다.</summary>
    private int EstimateCharsPerPage()
    {
        using var g = mainEditor.CreateGraphics();
        const string sample = "가나다라마바사아자차카타파하ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var measured = g.MeasureString(sample, mainEditor.Font);
        float avgCharWidth = Math.Max(4f, measured.Width / sample.Length);
        int usableWidth = Math.Max(50, mainEditor.ClientSize.Width - 12);
        int charsPerLine = Math.Max(10, (int)(usableWidth / avgCharWidth));
        int lineHeight = Math.Max(1, mainEditor.Font.Height);
        int linesPerPage = Math.Max(3, mainEditor.ClientSize.Height / lineHeight);
        return Math.Max(200, charsPerLine * linesPerPage);
    }

    /// <summary>후보 텍스트를 실제로 mainEditor에 넣어보고, 스크롤 없이 화면에 다 들어가는지 진짜로 잰다.
    /// (옵시디언 플러그인이 CSS 레이아웃으로 실측했던 것과 같은 방식 — 여기선 WinForms의
    /// GetPositionFromCharIndex로 마지막 글자가 화면 안에 있는지를 직접 확인한다.)</summary>
    private bool FitsOnScreen(string candidate)
    {
        mainEditor.Text = candidate;
        if (candidate.Length == 0) return true;
        mainEditor.SelectionStart = 0; // 스크롤이 맨 위에 있어야 아래쪽 글자 위치가 정확히 측정된다
        mainEditor.SelectionLength = 0;
        mainEditor.ScrollToCaret();
        var pos = mainEditor.GetPositionFromCharIndex(candidate.Length - 1);
        return pos.Y + mainEditor.Font.Height <= mainEditor.ClientSize.Height;
    }

    /// <summary>start부터 시작해서, 화면에 꽉 차게(넘치지 않게) 들어가는 최대 끝 위치를 실측으로 찾는다.</summary>
    private int FindPageEnd(int start)
    {
        if (start >= fullText.Length) return fullText.Length;
        int remaining = fullText.Length - start;
        int cap = Math.Min(remaining, EstimateCharsPerPage() * 2);

        // 추정치가 화면을 다 못 채울 만큼 작았으면(글자가 작거나 창이 클 때), 다 채울 때까지 범위를 넓혀간다.
        while (cap < remaining && FitsOnScreen(fullText.Substring(start, cap)))
            cap = Math.Min(remaining, cap * 2);

        if (FitsOnScreen(fullText.Substring(start, cap)))
            return start + cap; // 남은 글 전체가 화면에 다 들어감 — 이 페이지가 마지막 페이지

        int loFit = 0, hiNotFit = cap;
        while (hiNotFit - loFit > 1)
        {
            int mid = (loFit + hiNotFit) / 2;
            if (FitsOnScreen(fullText.Substring(start, mid))) loFit = mid; else hiNotFit = mid;
        }
        return start + Math.Max(1, loFit); // 최소 한 글자는 진행해서 무한 루프를 막는다
    }

    /// <summary>end에서 끝나도록, 화면에 꽉 차게 들어가는 최소 시작 위치를 실측으로 찾는다(이전 페이지용).</summary>
    private int FindPageStart(int end)
    {
        if (end <= 0) return 0;
        int cap = Math.Min(end, EstimateCharsPerPage() * 2);

        while (cap < end && FitsOnScreen(fullText.Substring(end - cap, cap)))
            cap = Math.Min(end, cap * 2);

        if (FitsOnScreen(fullText.Substring(end - cap, cap)))
            return end - cap;

        int loFit = 0, hiNotFit = cap;
        while (hiNotFit - loFit > 1)
        {
            int mid = (loFit + hiNotFit) / 2;
            if (FitsOnScreen(fullText.Substring(end - mid, mid))) loFit = mid; else hiNotFit = mid;
        }
        return end - Math.Max(1, loFit);
    }

    // ───────────────────────── 보기: 글자 크기 ─────────────────────────

    private void SetFontSize(int size)
    {
        size = Math.Clamp(size, 8, 48);
        // 열린 파일이 있으면 그 파일에만 기억하고, 없으면 설정창 기본값으로.
        if (lookRec != null) { lookRec.LookSize = size; library.MarkDirty(); library.SaveIfDirty(); }
        else if (settings.FontSize != size) { settings.FontSize = size; settings.Save(); }
        ApplyLook();
    }
}
