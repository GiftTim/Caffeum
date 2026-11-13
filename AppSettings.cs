using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoffeeViewer;

/// <summary>같은 폴더의 마지막 파일을 다 넘겼거나(또는 첫 파일 앞으로 되돌렸을 때) 무엇을 할지. 설정창의 콤보 상자 순서와 같다.</summary>
public enum EdgeFileAction
{
    /// <summary>아무것도 하지 않는다.</summary>
    Nothing,
    /// <summary>무엇을 할지 물어본다.</summary>
    Ask,
    /// <summary>같은 폴더의 반대쪽 끝 파일로 돌아간다(마지막 → 첫 파일, 첫 → 마지막 파일).</summary>
    WrapAround,
    /// <summary>다음(이전) 폴더의 첫(마지막) 파일로 넘어간다.</summary>
    AdjacentFolder,
}

/// <summary>소설(txt · epub)을 열 때의 읽기 방식. 설정창의 콤보 상자 순서와 같다.</summary>
public enum NovelReadingMode
{
    /// <summary>전체 글을 이어서 스크롤한다.</summary>
    Scroll,
    /// <summary>화면 한 장씩, 화살표로 넘긴다.</summary>
    Page,
    /// <summary>화면 한 장씩, 화면 왼쪽 · 오른쪽을 눌러 넘긴다.</summary>
    PageTouch,
}

/// <summary>
/// 화면·낭독 설정. %LOCALAPPDATA%\CoffeeViewer\settings.json 에 저장한다.
/// 설정 파일이 깨졌거나 없어도 앱이 멈추면 안 되므로, 읽기/쓰기 실패는 조용히 기본값으로 넘어간다.
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "카페 라이트";
    public string FontFamily { get; set; } = "맑은 고딕";
    public int FontSize { get; set; } = 11;
    /// <summary>txt: 본문 좌우 안쪽 여백 / epub: 페이지 좌우 여백(px). 예전 epub 고정값(32)과 같게 시작한다.</summary>
    public int SideMargin { get; set; } = 32;
    /// <summary>본문 상하 여백(px). txt·epub 공용.</summary>
    public int VerticalMargin { get; set; } = 12;
    /// <summary>만화(이미지) 좌우 여백(px). 그림은 이 여백을 뺀 안쪽 영역에 맞춰 보인다.</summary>
    public int ImageSideMargin { get; set; } = 0;
    /// <summary>만화(이미지) 상하 여백(px).</summary>
    public int ImageVerticalMargin { get; set; } = 0;
    /// <summary>epub 읽기 서식창의 [상][하][좌][우] 여백(px). 비어 있으면 위의 상하(VerticalMargin) · 좌우(SideMargin) 값을 쓴다.</summary>
    public int? EpubMarginTop { get; set; }
    public int? EpubMarginBottom { get; set; }
    public int? EpubMarginLeft { get; set; }
    public int? EpubMarginRight { get; set; }
    /// <summary>만화 읽기 서식창의 [상][하][좌][우] 여백(px). 비어 있으면 ImageVerticalMargin · ImageSideMargin을 쓴다.</summary>
    public int? ImageMarginTop { get; set; }
    public int? ImageMarginBottom { get; set; }
    public int? ImageMarginLeft { get; set; }
    public int? ImageMarginRight { get; set; }
    /// <summary>소설 본문 줄 간격(글자 크기 대비 %). txt·epub 공용.</summary>
    public int LineHeight { get; set; } = 170;
    /// <summary>txt 문단(줄) 사이 추가 간격(px). 빈 줄로 문단을 나누지 않은 글을 읽기 편하게.</summary>
    public int ParagraphSpacing { get; set; } = 0;
    /// <summary>읽는 txt를 문장이 끝날 때마다 줄을 바꿔서 보여줄지(글 파일은 그대로).</summary>
    public bool TxtAutoBreak { get; set; }
    /// <summary>편집 용지의 새 문서 기본값(null = 용지 없이 창 폭에 맞춤).</summary>
    public PaperSetup? Paper { get; set; }
    /// <summary>탭 밑 서식창(편집 용지 · 글꼴 · 크기 · 줄 간격 · 정렬 …)을 글(txt · epub)에서 보일지.</summary>
    public bool ShowFormatBar { get; set; } = true;
    /// <summary>SAPI 낭독 속도(-10 ~ 10).</summary>
    public int TtsRate { get; set; } = 0;
    /// <summary>만화 자동 넘김(슬라이드쇼) 간격(초).</summary>
    public int SlideshowSeconds { get; set; } = 3;
    public string? TtsVoice { get; set; }
    public bool PdfDarkMode { get; set; } = false;
    /// <summary>움직이는 그림(gif·webp)을 끝없이 반복할지. 끄면 한 번만 재생하고 마지막 장면에서 멈춘다.</summary>
    public bool LoopAnimations { get; set; } = true;
    /// <summary>만화(이미지)를 열 때의 읽기 방식. false = 페이지(화살표로 넘기기), true = 페이지 터치(화면 반쪽 탭으로 넘기기).</summary>
    public bool ImagePageTouch { get; set; } = false;
    /// <summary>만화 보기 방식. false = 한쪽 보기(한 번에 한 장), true = 두쪽 보기(연속한 두 장을 나란히, 읽는 방향에 따라 좌우 순서가 정해진다).</summary>
    public bool ImageDoublePage { get; set; } = false;
    /// <summary>epub 페이지 방식에서 한 화면에 두 쪽(2단)으로 보기. 기본은 한쪽.</summary>
    public bool EpubDoublePage { get; set; } = false;
    /// <summary>소설(txt · epub)을 열 때의 읽기 방식. 예전 기본 동작(스크롤)과 같게 시작한다.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NovelReadingMode NovelReadingMode { get; set; } = NovelReadingMode.Scroll;    /// <summary>폴더의 마지막 파일 끝(첫 파일 앞)에서 더 넘기려 할 때의 동작. 만화 zip · 소설 페이지 방식에 적용된다.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EdgeFileAction EdgeFileAction { get; set; } = EdgeFileAction.Ask;

    /// <summary>txt 본문의 자동 줄바꿈(서식 메뉴). 페이지 방식에서는 이 값과 상관없이 항상 켜진다.</summary>
    public bool TxtWordWrap { get; set; } = true;

    // ── txt 챕터 나누기(TxtChapterSplitter) ──
    /// <summary>txt를 챕터로 나누는 방식. 기본(자동)은 예전 동작과 같다.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TxtSplitMode TxtSplitMode { get; set; } = TxtSplitMode.Auto;
    /// <summary>제목으로 인식할 형식(TxtSplitOptions.Presets의 키). 기본은 "N화"만.</summary>
    public List<string> TxtHeadingPresets { get; set; } = new() { "episode" };
    /// <summary>제목 줄을 직접 정하는 정규식(줄 하나에 적용). 비우면 쓰지 않는다.</summary>
    public string? TxtHeadingCustomRegex { get; set; }
    /// <summary>한 챕터의 목표 글자 수(자동 · 글자 수 방식에서 큰 구간을 자를 때).</summary>
    public int TxtChunkSize { get; set; } = TxtSplitOptions.DefaultChunkSize;

    // ── 탐색 막대(보기 > 탐색) ──
    /// <summary>읽기 화면 아래 탐색 막대(파일 · 챕터 · 위치 막대)를 쓸지.</summary>
    public bool NavBarEnabled { get; set; } = true;
    /// <summary>탐색 막대를 늘 보이게(고정) 할지. 아니면 마우스를 화면 아래쪽에 가져갈 때만 떠오른다.
    /// 보기 > 탐색을 켤 때마다 고정된 채로 시작한다(MainForm.Nav.cs).</summary>
    public bool NavBarPinned { get; set; } = true;

    /// <summary>읽기 화면 위쪽 머리 막대에서 뺀 버튼의 키(MainForm.ToolbarCatalog). 비어 있으면 전부 보인다.</summary>
    public List<string> ToolbarHidden { get; set; } = new();
    /// <summary>머리 막대 오른쪽 묶음의 순서(왼쪽 → 오른쪽 키). 비어 있으면 기본 순서(MainForm.ToolbarRightDefault).</summary>
    public List<string> ToolbarOrder { get; set; } = new();
    /// <summary>오른쪽 묶음에서 왼쪽(이전 파일 옆, 책 이름 앞)으로 옮겨 둔 키들. 왼쪽 → 오른쪽 순서. 구분선도 옮길 수 있다.</summary>
    public List<string> ToolbarLeft { get; set; } = new();
    /// <summary>epub · 만화용 읽기 전용 서식창 항목의 순서(MainForm.ToolbarReadDefault). 비어 있으면 기본 순서.</summary>
    public List<string> ToolbarRead { get; set; } = new();
    /// <summary>참고 파일 서식창 항목의 순서(MainForm.RefBarDefault). 비어 있으면 기본 순서.</summary>
    public List<string> ToolbarRef { get; set; } = new();
    /// <summary>참고 파일 서식창에서 뺀 항목의 키. 비어 있으면 전부 보인다(문서 종류에 안 맞는 버튼은 늘 감춘다).</summary>
    public List<string> ToolbarRefHidden { get; set; } = new();

    // ── 서재 화면(읽기 화면 설정과 따로) ──
    /// <summary>마지막으로 연 볼트 폴더(3.0). [볼트 열기]의 폴더 선택 창이 여기서 시작한다.</summary>
    public string? LastVault { get; set; }

    /// <summary>볼트 왼쪽 패널의 정렬(name, name-desc, modified, modified-asc). 폴더는 늘 파일 앞에 온다.</summary>
    public string VaultSort { get; set; } = "name";

    // ── 워크스페이스 왼쪽 아이콘 막대 ──
    /// <summary>아이콘 막대 항목 순서(WorkspaceRail.Catalog의 키, "spacer" 뒤는 아래쪽 묶음). 비어 있으면 기본 순서.</summary>
    public List<string> RailOrder { get; set; } = new();
    /// <summary>아이콘 막대에서 숨긴 항목들. 볼트 파일은 처음엔 숨겨 둔다.</summary>
    public List<string> RailHidden { get; set; } = new() { "files" };

    // ── 회차 글쓰기(Muvel식 "글자 수 세기" 위젯) ──
    /// <summary>한 회차의 목표 글자 수.</summary>
    public int WriterGoalChars { get; set; } = 3000;

    /// <summary>글을 고치다가 아무것도 안 치고 이 초가 지나면 자동 저장(기본 10초).</summary>
    public int AutoSaveIdleSeconds { get; set; } = 10;
    /// <summary>계속 치고 있어도 이 초마다는 자동 저장(기본 60초 = 1분).</summary>
    public int AutoSaveMaxSeconds { get; set; } = 60;

    // ── 빠른 교정 내용(도구 > 빠른 교정 내용…) ──
    /// <summary>빠른 교정 사용자 사전: 틀린 말 → 맞는 말(띄어쓰기 · 엔터를 칠 때 바로 앞 낱말을 바꾼다).</summary>
    public List<WordPair> QuickCorrect { get; set; } = new();
    /// <summary>입력 자동 명령 사용자 사전: 입력할 말 → 바뀔 말(치는 순간 바뀐다).</summary>
    public List<WordPair> AutoCommands { get; set; } = new();
    /// <summary>환경설정 > 보기 > 문단 서식 기본값(파일에 따로 정한 게 없을 때 쓴다).</summary>
    public ParaFormat? DefaultPara { get; set; }
    // ── 글자 수 세기 위젯 설정 ──
    /// <summary>분량 계산 단위: 0 글자 수 · 1 단어 수 · 2 문장 수 · 3 용량(바이트).</summary>
    public int GoalUnit { get; set; }
    public bool GoalExcludeSpaces { get; set; } = true;
    public bool GoalExcludePunct { get; set; }
    public bool GoalExcludeSpecial { get; set; }
    /// <summary>소설 메인 · 파일 트리 목록의 글자 수에 공백을 넣을지(기본: 공백 제외).</summary>
    public bool ListCountWithSpaces { get; set; }
    public bool AutoEllipsis { get; set; }
    /// <summary>말줄임표를 …(한 글자) 대신 가운뎃점 여섯 개(······)로.</summary>
    public bool AutoEllipsisDots { get; set; }
    public bool AutoDash { get; set; }
    public bool AutoQuotes { get; set; }
    public bool AutoOrdinal { get; set; }
    public bool AutoBrackets { get; set; }
    public bool AutoBullet { get; set; }
    public bool AutoNumber { get; set; }
    public bool AutoDate { get; set; }
    /// <summary>글자 수 위젯을 글쓰기 화면 오른쪽 아래에 띄울지(상태 줄의 글자 수 칸을 누르면 다시 켜진다).</summary>
    public bool WriterGoalWidget { get; set; } = true;
    /// <summary>목표를 공백 포함 글자 수로 셀지(끄면 공백 제외).</summary>
    public bool WriterGoalWithSpaces { get; set; } = true;

    /// <summary>볼트(폴더 경로)별 북마크한 파일들.</summary>
    public Dictionary<string, List<string>> Bookmarks { get; set; } = new();

    /// <summary>서재만의 색 테마 이름. null이면 읽기 화면 테마를 그대로 따른다.</summary>
    public string? LibraryTheme { get; set; }
    /// <summary>서재 목록·폴더 트리의 글자 크기(pt). 열 폭도 이 크기에 맞춰 같은 비율로 늘어난다.</summary>
    public int LibraryFontSize { get; set; } = 9;
    /// <summary>같은 소설의 여러 파일(회차별·형식별)을 머리글 밑에 묶어서 보여줄지.</summary>
    public bool SeriesGrouping { get; set; } = true;
    /// <summary>묶음을 처음부터 접힌 채로 보여줄지(펼쳐서 보던 것만 펼침).</summary>
    public bool SeriesStartCollapsed { get; set; } = false;
    /// <summary>묶음 안 책 제목을 머리글보다 안쪽으로 들여쓰는 폭(px, 화면 배율 100% 기준).</summary>
    public int SeriesIndent { get; set; } = 16;

    /// <summary>CAFEUMOFFICE_DATA_DIR 환경 변수가 있으면 그 폴더를 쓴다(설치본의 실제 설정을 건드리지 않고 시험해볼 때).</summary>
    internal static string DataDir => Environment.GetEnvironmentVariable("CAFEUMOFFICE_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CafeumOffice");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 경로·글꼴 이름의 한글이 \uXXXX로 안 바뀌게
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch { /* 깨진 설정 파일은 무시하고 기본값으로 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true); // 쓰다 죽어도 기존 파일이 반쪽으로 남지 않게 임시 파일 → 교체
        }
        catch { /* 저장 실패가 읽기를 막으면 안 된다 */ }
    }
}
