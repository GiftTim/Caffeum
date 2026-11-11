using System.Drawing.Text;

namespace CoffeeViewer;

/// <summary>
/// 설정창. 창을 띄워둔 채로 값을 만지면 그 즉시 본문에 반영된다(모달이 아니라 옆에 두고 보면서 조절하는 방식).
/// 바뀔 때마다 AppSettings 값을 갱신하고 changed(종류)를 불러서 MainForm이 알맞게 다시 그리게 한다.
///
/// 반디뷰 환경 설정처럼 왼쪽에 분류(일반·보기·영상 처리·볼트·낭독) 목록을 두고, 고른 분류의 항목만 오른쪽에
/// 보여준다 — 항목을 한 줄로 죽 늘어놓으니 길고 어디에 뭐가 있는지 찾기 어려웠다.
/// 분류마다 표(TableLayoutPanel)를 하나씩 만들어 같은 자리에 겹쳐 두고, 고른 것만 보이게 한다.
/// </summary>
public sealed class SettingsForm : Form
{
    // 독서에 어울리는 한글 명조/바탕과 가독성 좋은 고딕만 추려서 보여준다(시스템 글꼴 수백 개를 다 늘어놓지 않으려고).
    private static readonly string[] ReadingFonts =
    {
        "맑은 고딕", "바탕", "바탕체", "함초롬바탕", "함초롬돋움", "나눔명조", "나눔고딕", "나눔바른고딕", "나눔스퀘어",
        "KoPubWorld바탕체 Medium", "KoPubWorld돋움체 Medium", "KoPub바탕체 Medium", "KoPub돋움체 Medium",
        "Noto Serif KR", "Noto Sans KR", "Noto Serif CJK KR", "Noto Sans CJK KR", "Source Han Serif K", "Pretendard", "SUIT",
        "리디바탕", "본명조", "본고딕", "고운바탕", "고운돋움", "이롭게 바탕체",
    };

    /// <summary>창을 닫았다 다시 열어도 보던 분류로 돌아오게(프로그램이 켜져 있는 동안만).</summary>
    private static int lastPage;

    private readonly AppSettings settings;
    private readonly Action<string> changed;
    private readonly TtsEngine preview = new();
    private readonly ListBox nav;
    private readonly Label pageTitle;
    private readonly Panel titleLine;
    private readonly Panel pageHost;
    private readonly List<TableLayoutPanel> pages = new();
    private readonly List<Label> hints = new(); // 테마를 입힌 뒤에도 흐린 글씨로 남겨야 하는 안내문
    private TableLayoutPanel table = null!; // AddHeading/AddRow가 채우는 "지금 만드는 분류"의 표
    private ReaderTheme theme = Themes.All[0];
    private bool loading = true; // 초기값을 채우는 동안 발생하는 이벤트로 저장·반영이 돌지 않게
    // 챕터 나누기는 큰 글 전체를 다시 훑으므로, 글자를 치거나 숫자를 올리는 동안은 잠깐 기다렸다가 한 번만 반영한다.
    private readonly System.Windows.Forms.Timer splitTimer = new() { Interval = 500 };

    public SettingsForm(AppSettings settings, Action<string> changed)
    {
        this.settings = settings;
        this.changed = changed;

        // 화면 배율(DPI)이 높아도 글자가 잘리지 않게, 픽셀 좌표 대신 자동 크기 표 레이아웃으로 짠다.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "설정";
        // 화면이 작으면(배율 150% 노트북 등) 크기를 줄일 수 있게 하고, 넘치는 건 오른쪽 영역 스크롤로 본다.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(12);
        Icon = AppShared.LoadAppIcon();

        // ── 왼쪽: 분류 목록 ──
        nav = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 190,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 44,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
        };
        nav.DrawItem += DrawNavItem;
        nav.SelectedIndexChanged += (_, _) => ShowPage(nav.SelectedIndex);

        // ── 오른쪽: 분류 제목 + 그 분류의 항목들 ──
        pageTitle = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 36, TextAlign = ContentAlignment.MiddleLeft };
        pageTitle.Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Bold);
        titleLine = new Panel { Dock = DockStyle.Top, Height = 1 }; // 제목 밑 가는 선(색은 ApplyTheme에서)
        pageHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 4, 0) };
        right.Controls.Add(pageHost);   // Fill은 먼저 넣고
        right.Controls.Add(titleLine);  // Top들은 나중에 넣을수록 위로 간다
        right.Controls.Add(pageTitle);

        // 카페움 오피스 중심 순서: 일반 → 볼트(왼쪽 패널) → 서식창 · 머리 막대 → 보기(글자 · 여백) → 읽기 · 낭독.
        // 읽기 전용 설정(챕터 · PDF/움직이는 그림 · 낭독)은 한 분류("읽기 · 낭독")에 소제목으로 모은다.
        BuildGeneralPage();
        BuildLibraryPage();
        BuildToolbarPage();
        BuildViewPage();
        BuildChapterPage();
        BuildImagePage();
        BuildTtsPage();

        // 왼쪽 분류 순서: 일반 - 볼트 - 사이드바 - 참고자료 서식창(· txt · epub/만화 서식창) - 보기 - 읽기 · 낭독
        string[] navOrder = { "일반", "볼트", "사이드바", "참고자료 서식창", "txt 서식창", "보기", "읽기 · 낭독" };
        int Rank(TableLayoutPanel p) { int i = Array.IndexOf(navOrder, (string)p.Tag!); return i < 0 ? 99 : i; }
        var sorted = pages.OrderBy(Rank).ToList();
        pages.Clear();
        pages.AddRange(sorted);
        nav.Items.Clear();
        foreach (var p in pages) nav.Items.Add((string)p.Tag!);

        // ── 아래: 닫기 ──
        var closeButton = new Button { Text = "닫기", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 2, 14, 2), Margin = new Padding(0) };
        closeButton.Click += (_, _) => Close();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        bottom.Controls.Add(closeButton);
        CancelButton = closeButton;

        Controls.Add(right);   // Fill
        Controls.Add(nav);     // Left
        Controls.Add(bottom);  // Bottom — 나중에 넣은 것이 가장자리를 먼저 차지한다

        // 처음 크기: 가장 큰 분류가 다 보이는 크기. 분류를 바꿔도 창 크기가 들썩이지 않게 한 번 정해두고,
        // 화면(작업 영역)보다 커지지는 않게 한다. 넘치면 오른쪽 영역에 스크롤바가 생긴다.
        int pageW = pages.Max(p => p.GetPreferredSize(Size.Empty).Width);
        int pageH = pages.Max(p => p.GetPreferredSize(Size.Empty).Height);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        int wantW = Padding.Horizontal + nav.Width + right.Padding.Horizontal + pageW + SystemInformation.VerticalScrollBarWidth + 8;
        int wantH = Padding.Vertical + pageTitle.Height + 1 + 14 + pageH + bottom.PreferredSize.Height + 8;
        // 가장 긴 분류에 맞추면 너무 커져서(긴 안내 글 · 목록 때문에) 상한을 둔다 — 넘치는 부분은 오른쪽 영역 스크롤로 본다.
        int capW = LogicalToDeviceUnits(780), capH = LogicalToDeviceUnits(560);
        ClientSize = new Size(Math.Min(wantW, Math.Min(capW, wa.Width - 40)), Math.Min(Math.Max(wantH, 360), Math.Min(capH, wa.Height - 100)));
        MinimumSize = new Size(Math.Min(Width, 520), 300);

        nav.SelectedIndex = Math.Clamp(lastPage, 0, pages.Count - 1);

        FormClosed += (_, _) => { preview.Dispose(); splitTimer.Dispose(); };
        loading = false;
    }

    // ───────────────────────── 분류별 항목 ─────────────────────────

    private void BuildGeneralPage()
    {
        BeginPage("일반");

        AddHeading("화면 색", first: true);
        var themeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        foreach (var t in Themes.All) themeBox.Items.Add(t.Name);
        themeBox.SelectedItem = Themes.Find(settings.Theme).Name;
        themeBox.SelectedIndexChanged += (_, _) => Apply(() => settings.Theme = (string)themeBox.SelectedItem!, "theme");
        AddRow("테마", themeBox);
        AddHint("읽기 화면과 메뉴 · 설정 창의 색입니다. 볼트 화면 색은 '볼트'에서 따로 고를 수 있어요.");

        AddHeading("처음 · 마지막 파일에서");
        var edgeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        edgeBox.Items.AddRange(new object[]
        {
            "아무것도 하지 않기",
            "무엇을 할지 물어보기",
            "다시 처음으로 들어가기",
            "다음 · 이전 폴더의 파일로 넘어가기",
        }); // EdgeFileAction 선언 순서와 같다
        edgeBox.SelectedIndex = Math.Clamp((int)settings.EdgeFileAction, 0, edgeBox.Items.Count - 1);
        edgeBox.SelectedIndexChanged += (_, _) => Apply(() => settings.EdgeFileAction = (EdgeFileAction)edgeBox.SelectedIndex, "edge");
        AddRow("동작", edgeBox);
        AddHint("폴더의 마지막 파일 끝에서 더 넘기거나, 첫 파일 앞으로 되돌릴 때의 동작입니다.\n" +
                "'물어보기'는 화면에 안내를 띄우고, ] 를 누르면 다음 폴더로 [ 를 누르면 이전 폴더로 넘어갑니다.\n" +
                "'처음으로'는 마지막 파일이면 첫 파일로, 첫 파일이면 마지막 파일로 갑니다.\n" +
                "만화(zip)와 소설의 페이지 방식(txt · epub)에서 동작합니다.");
    }

    private void BuildViewPage()
    {
        BeginPage("보기");

        AddHeading("문단 서식 기본값", first: true);
        var paraBtn = new Button { Text = "문단 서식 기본값 설정…", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        paraBtn.Click += (_, _) =>
        {
            using var dlg = new ParagraphDialog(settings.DefaultPara?.Clone() ?? new ParaFormat { Align = "left", LinePercent = settings.LineHeight });
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var p = dlg.Result;
            Apply(() => { settings.DefaultPara = p; if (p.LineMode == 0) settings.LineHeight = Math.Clamp(p.LinePercent, 100, 300); }, "font");
        };
        AddRow("", paraBtn);
        AddHint("정렬 · 여백 · 첫 줄 · 줄 간격 · 문단 간격 · 줄 나눔 기준을 파일마다 따로 정하지 않았을 때 이 값을 씁니다.");
        AddHeading("글자");
        var fontBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        var installed = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var fonts = ReadingFonts.Where(installed.Contains).ToList();
        if (!fonts.Contains(settings.FontFamily, StringComparer.OrdinalIgnoreCase)) fonts.Insert(0, settings.FontFamily);
        foreach (var f in fonts) fontBox.Items.Add(f);
        fontBox.SelectedItem = fonts.First(f => string.Equals(f, settings.FontFamily, StringComparison.OrdinalIgnoreCase));
        fontBox.SelectedIndexChanged += (_, _) => Apply(() => settings.FontFamily = (string)fontBox.SelectedItem!, "font");
        AddRow("글꼴", fontBox);

        var sizeBox = new NumericUpDown { Minimum = 8, Maximum = 48, Value = Math.Clamp(settings.FontSize, 8, 48), Width = 80 };
        sizeBox.ValueChanged += (_, _) => Apply(() => settings.FontSize = (int)sizeBox.Value, "fontsize");
        AddRow("글자 크기(pt)", sizeBox);

        var lineBox = new NumericUpDown { Minimum = 100, Maximum = 300, Increment = 10, Value = Math.Clamp(settings.LineHeight, 100, 300), Width = 80 };
        lineBox.ValueChanged += (_, _) => Apply(() => settings.LineHeight = (int)lineBox.Value, "margin");
        AddRow("줄 간격(%)", lineBox);

        var paraBox = new NumericUpDown { Minimum = 0, Maximum = 60, Increment = 2, Value = Math.Clamp(settings.ParagraphSpacing, 0, 60), Width = 80 };
        paraBox.ValueChanged += (_, _) => Apply(() => settings.ParagraphSpacing = (int)paraBox.Value, "margin");
        AddRow("문단 간격(px, txt)", paraBox);

        AddHeading("여백");
        var sideBox = new NumericUpDown { Minimum = 0, Maximum = 300, Increment = 4, Value = Math.Clamp(settings.SideMargin, 0, 300), Width = 80 };
        sideBox.ValueChanged += (_, _) => Apply(() => { settings.SideMargin = (int)sideBox.Value; settings.EpubMarginLeft = settings.EpubMarginRight = null; }, "margin");
        AddRow("좌우 여백(px)", sideBox);

        var vertBox = new NumericUpDown { Minimum = 0, Maximum = 200, Increment = 4, Value = Math.Clamp(settings.VerticalMargin, 0, 200), Width = 80 };
        vertBox.ValueChanged += (_, _) => Apply(() => { settings.VerticalMargin = (int)vertBox.Value; settings.EpubMarginTop = settings.EpubMarginBottom = null; }, "margin");
        AddRow("상하 여백(px)", vertBox);
        AddHint("소설(txt · epub) 본문에 적용됩니다.");

        AddHeading("만화 여백");
        var imgSideBox = new NumericUpDown { Minimum = 0, Maximum = 400, Increment = 4, Value = Math.Clamp(settings.ImageSideMargin, 0, 400), Width = 80 };
        imgSideBox.ValueChanged += (_, _) => Apply(() => { settings.ImageSideMargin = (int)imgSideBox.Value; settings.ImageMarginLeft = settings.ImageMarginRight = null; }, "imagemargin");
        AddRow("좌우 여백(px)", imgSideBox);

        var imgVertBox = new NumericUpDown { Minimum = 0, Maximum = 400, Increment = 4, Value = Math.Clamp(settings.ImageVerticalMargin, 0, 400), Width = 80 };
        imgVertBox.ValueChanged += (_, _) => Apply(() => { settings.ImageVerticalMargin = (int)imgVertBox.Value; settings.ImageMarginTop = settings.ImageMarginBottom = null; }, "imagemargin");
        AddRow("상하 여백(px)", imgVertBox);
        AddHint("만화(zip · 이미지)에 적용됩니다. 그림은 이 여백을 뺀 안쪽 영역에 맞춰 보이고, 0이면 예전처럼 창 전체를 씁니다.");

        AddHeading("읽기 방식");
        var novelReadingBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        novelReadingBox.Items.AddRange(new object[] { "스크롤", "페이지", "페이지 터치" }); // NovelReadingMode 선언 순서와 같다
        novelReadingBox.SelectedIndex = Math.Clamp((int)settings.NovelReadingMode, 0, novelReadingBox.Items.Count - 1);
        novelReadingBox.SelectedIndexChanged += (_, _) => Apply(() => settings.NovelReadingMode = (NovelReadingMode)novelReadingBox.SelectedIndex, "novelreading");
        AddRow("소설(epub)", novelReadingBox);

        var imageReadingBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        imageReadingBox.Items.AddRange(new object[] { "페이지", "페이지 터치" }); // 만화엔 스크롤이 없다
        imageReadingBox.SelectedIndex = settings.ImagePageTouch ? 1 : 0;
        imageReadingBox.SelectedIndexChanged += (_, _) => Apply(() => settings.ImagePageTouch = imageReadingBox.SelectedIndex == 1, "imagereading");
        AddRow("만화(zip · 이미지)", imageReadingBox);

        var imageLayoutBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        imageLayoutBox.Items.AddRange(new object[] { "한쪽 보기", "두쪽 보기" });
        imageLayoutBox.SelectedIndex = settings.ImageDoublePage ? 1 : 0;
        imageLayoutBox.SelectedIndexChanged += (_, _) => Apply(() => settings.ImageDoublePage = imageLayoutBox.SelectedIndex == 1, "imagelayout");
        AddRow("만화 보기 방식", imageLayoutBox);
        AddHint("파일을 열 때 처음 쓰는 읽기 방식입니다.\n'페이지'는 화살표로, '페이지 터치'는 화면 왼쪽 · 오른쪽을 눌러 넘깁니다. txt는 메모장처럼 바로 쓰는 화면이라 스크롤만 됩니다.\n읽는 중에는 보기 > 읽기 방식에서 바꿀 수 있어요.");
    }

    private void BuildChapterPage()
    {
        BeginPage("읽기 · 낭독");
        splitTimer.Tick += (_, _) => { splitTimer.Stop(); Apply(() => { }, "txtsplit"); };
        void Changed(Action set) { if (loading) return; set(); splitTimer.Stop(); splitTimer.Start(); }

        AddHeading("나누는 방식", first: true);
        var modes = new[]
        {
            (TxtSplitMode.Auto, "자동 (제목으로 나누고, 큰 구간은 글자 수로 다시 자르기)"),
            (TxtSplitMode.Heading, "제목 형식으로만 나누기"),
            (TxtSplitMode.Size, "글자 수로만 나누기"),
            (TxtSplitMode.None, "나누지 않기 (파일 전체가 챕터 하나)"),
        };
        var modeFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        var presetBoxes = new List<CheckBox>();
        var regexBox = new TextBox { Width = 300 };
        var chunkBox = new NumericUpDown
        {
            Minimum = TxtSplitOptions.MinChunkSize, Maximum = TxtSplitOptions.MaxChunkSize, Increment = 1000,
            Value = Math.Clamp(settings.TxtChunkSize, TxtSplitOptions.MinChunkSize, TxtSplitOptions.MaxChunkSize), Width = 90,
        };
        void SyncEnabled()
        {
            bool byHeading = settings.TxtSplitMode is TxtSplitMode.Auto or TxtSplitMode.Heading;
            bool bySize = settings.TxtSplitMode is TxtSplitMode.Auto or TxtSplitMode.Size;
            foreach (var c in presetBoxes) c.Enabled = byHeading;
            regexBox.Enabled = byHeading;
            chunkBox.Enabled = bySize;
        }
        foreach (var (mode, label) in modes)
        {
            var radio = new RadioButton { Text = label, AutoSize = true, Checked = settings.TxtSplitMode == mode, Margin = new Padding(0, 2, 0, 2) };
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked) return;
                Changed(() => settings.TxtSplitMode = mode);
                SyncEnabled();
            };
            modeFlow.Controls.Add(radio);
        }
        AddRow("", modeFlow);
        AddHint("긴 txt를 '챕터'로 나누면 네비게이션 바의 챕터 목록과 위치 표시에 쓰입니다.\n" +
                "열려 있는 txt는 바로 다시 나뉘고, 읽던 위치는 그대로입니다.\n" +
                "(수정 중이거나 zip에서 꺼낸 글은 다시 열 때 적용됩니다.)");

        AddHeading("제목으로 인정할 형식");
        foreach (var p in TxtSplitOptions.Presets)
        {
            var check = new CheckBox { Text = $"{p.Label}   ({p.Example})", AutoSize = true, Checked = settings.TxtHeadingPresets.Contains(p.Key) };
            string key = p.Key;
            check.CheckedChanged += (_, _) => Changed(() =>
            {
                settings.TxtHeadingPresets.RemoveAll(k => k == key);
                if (check.Checked) settings.TxtHeadingPresets.Add(key);
            });
            presetBoxes.Add(check);
            AddRow("", check);
        }

        regexBox.Text = settings.TxtHeadingCustomRegex ?? "";
        var regexError = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, 0, 0, 0) };
        regexBox.TextChanged += (_, _) =>
        {
            bool ok = TxtSplitOptions.IsValidRegex(regexBox.Text, out string err);
            regexError.Text = ok ? "" : "정규식 오류: " + err;
            Changed(() => settings.TxtHeadingCustomRegex = regexBox.Text);
        };
        AddRow("직접 입력(정규식)", regexBox);
        AddRow("", regexError);
        AddHint("줄 하나에 적용됩니다. 예) ^\\s*\\d+\\s*회   (\"12회\"로 시작하는 줄)\n" +
                "위에서 고른 형식과 함께 쓰이며, 잘못된 정규식은 무시됩니다.");

        AddHeading("글자 수");
        chunkBox.ValueChanged += (_, _) => Changed(() => settings.TxtChunkSize = (int)chunkBox.Value);
        AddRow("챕터 크기(자)", chunkBox);
        AddHint("제목이 없거나 한 구간이 너무 클 때, 이 크기 안팎으로 가까운 빈 줄에서 자릅니다.\n" +
                $"자동 모드는 이 크기의 약 2.5배를 넘는 구간만 자르고, 전체가 약 1.7배 이하면 아예 나누지 않습니다. (기본 {TxtSplitOptions.DefaultChunkSize:N0})");

        AddHeading("미리보기");
        var previewBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 400, Height = 150 };
        var previewButton = new Button { Text = "지금 설정으로 나눠 보기", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
        previewButton.Click += (_, _) => PreviewSplit(previewBox);
        AddRow("", previewButton);
        AddRow("", previewBox);
        AddHint("지금 열려 있는 txt에 적용해 봅니다. 없으면 파일을 골라 확인할 수 있어요.");

        SyncEnabled();
    }

    /// <summary>열려 있는 txt(없으면 고른 파일)를 현재 설정으로 나눠 보고 결과를 요약해 보여준다. 설정 자체는 건드리지 않는다.</summary>
    private void PreviewSplit(TextBox output)
    {
        if (!AppShared.TryGetOpenTxt(out string text, out string name))
        {
            using var dlg = new OpenFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일|*.*", Title = "나눠 볼 텍스트 파일" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                text = MainForm.DecodeText(File.ReadAllBytes(dlg.FileName));
                name = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
            catch (Exception ex)
            {
                output.Text = "파일을 읽지 못했습니다: " + ex.Message;
                return;
            }
            finally { Cursor = Cursors.Default; }
        }

        var chapters = TxtChapterSplitter.Split(text, name, TxtSplitOptions.From(settings));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{name} — 총 {text.Length:N0}자, {chapters.Count:N0}개 챕터");
        int shown = Math.Min(chapters.Count, 30);
        for (int i = 0; i < shown; i++)
            sb.AppendLine($"{i + 1}. {chapters[i].Name}  ({chapters[i].End - chapters[i].Start:N0}자)");
        if (chapters.Count > shown) sb.AppendLine($"… 외 {chapters.Count - shown:N0}개");
        output.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private void BuildImagePage()
    {
        BeginPage("읽기 · 낭독");

        AddHeading("PDF", first: true);
        var pdfDark = new CheckBox { Text = "PDF 야간 모드 (색 반전)", AutoSize = true, Checked = settings.PdfDarkMode };
        pdfDark.CheckedChanged += (_, _) => Apply(() => settings.PdfDarkMode = pdfDark.Checked, "pdf");
        AddRow("", pdfDark);
        AddHint("흰 바탕 PDF를 검은 바탕으로 뒤집어서, 어두운 곳에서 눈부시지 않게 봅니다.");

        AddHeading("움직이는 그림");
        var loopCheck = new CheckBox { Text = "GIF · WebP 무한 반복 재생", AutoSize = true, Checked = settings.LoopAnimations };
        loopCheck.CheckedChanged += (_, _) => Apply(() => settings.LoopAnimations = loopCheck.Checked, "anim");
        AddRow("", loopCheck);
        AddHint("끄면 한 번만 재생하고 마지막 장면에서 멈춥니다.");
    }

    private void BuildLibraryPage()
    {
        BeginPage("볼트");

        AddHeading("화면", first: true);
        const string SameAsReader = "읽기 화면과 같게";
        var libThemeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        libThemeBox.Items.Add(SameAsReader);
        foreach (var t in Themes.All) libThemeBox.Items.Add(t.Name);
        libThemeBox.SelectedItem = settings.LibraryTheme != null && Themes.All.Any(t => t.Name == settings.LibraryTheme) ? settings.LibraryTheme : SameAsReader;
        libThemeBox.SelectedIndexChanged += (_, _) => Apply(() =>
            settings.LibraryTheme = (string)libThemeBox.SelectedItem! == SameAsReader ? null : (string)libThemeBox.SelectedItem!, "libtheme");
        AddRow("화면 색", libThemeBox);

        var libSizeBox = new NumericUpDown { Minimum = 8, Maximum = 24, Value = Math.Clamp(settings.LibraryFontSize, 8, 24), Width = 80 };
        libSizeBox.ValueChanged += (_, _) => Apply(() => settings.LibraryFontSize = (int)libSizeBox.Value, "libfont");
        AddRow("폴더 트리 글자 크기(pt)", libSizeBox);

    }

/// <summary>설정 > 아이콘 막대: 워크스페이스 맨 왼쪽 아이콘들의 순서와 켜고 끄기.
    /// "── 여기부터 아래쪽 ──" 줄 뒤의 항목은 막대 아래쪽에 붙는다(그 줄은 숨길 수 없다).</summary>
    private void BuildRailPage()
    {
        BeginPage("사이드바");
        AddHeading("맨 왼쪽 아이콘 · 순서 · 켜고 끄기", first: true);
        var list = new CheckedListBox { Width = 320, Height = 300, IntegralHeight = false, CheckOnClick = false };
        bool filling = false, allowToggle = false;
        void Fill()
        {
            filling = true;
            list.Items.Clear();
            foreach (var k in WorkspaceRail.OrderFor(settings))
                list.Items.Add(new ToolbarEntry(k, WorkspaceRail.LabelOf(k)), k == "spacer" || WorkspaceRail.IsCustom(k) || !settings.RailHidden.Contains(k));
            filling = false;
        }
        Fill();
        // 체크 칸(왼쪽 네모)을 누르거나 스페이스를 눌렀을 때만 켜고 끈다(글자를 눌러 고를 때는 그대로).
        list.MouseDown += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (i < 0 || e.Button != MouseButtons.Left || e.X > LogicalToDeviceUnits(22)) return;
            list.SelectedIndex = i;
            allowToggle = true;
            try { list.SetItemChecked(i, !list.GetItemChecked(i)); }
            finally { allowToggle = false; }
        };
        list.KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Space) allowToggle = true; };
        list.KeyUp += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Space) allowToggle = false; };
        list.ItemCheck += (_, e) =>
        {
            if (filling || loading) return;
            string key = ((ToolbarEntry)list.Items[e.Index]).Key;
            if (!allowToggle || key == "spacer" || WorkspaceRail.IsCustom(key)) { e.NewValue = e.CurrentValue; return; }
            bool hide = e.NewValue == CheckState.Unchecked;
            Apply(() => { settings.RailHidden.RemoveAll(x => x == key); if (hide) settings.RailHidden.Add(key); }, "toolbar");
        };
        void Commit(List<string> keys) { Apply(() => settings.RailOrder = keys, "toolbar"); Fill(); }
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var keys = list.Items.Cast<ToolbarEntry>().Select(x => x.Key).ToList();
            (keys[i], keys[j]) = (keys[j], keys[i]);
            Commit(keys);
            list.SelectedIndex = j;
        }
        var up = new Button { Text = "▲ 위로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var down = new Button { Text = "▼ 아래로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var addSep = new Button { Text = "구분선 추가", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var addBlank = new Button { Text = "빈 아이콘 추가", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var del = new Button { Text = "삭제", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        void AddCustom(string prefix)
        {
            var keys = list.Items.Cast<ToolbarEntry>().Select(x => x.Key).ToList();
            int n = 1;
            while (keys.Contains(prefix + n)) n++;
            int at = list.SelectedIndex >= 0 ? list.SelectedIndex + 1 : Math.Max(0, keys.IndexOf("spacer")); // 고른 항목 바로 뒤(고른 게 없으면 위쪽 묶음 맨 끝)
            keys.Insert(at, prefix + n);
            Commit(keys);
            list.SelectedIndex = at;
        }
        addSep.Click += (_, _) => AddCustom("sepX");
        addBlank.Click += (_, _) => AddCustom("blankX");
        del.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || !WorkspaceRail.IsCustom(((ToolbarEntry)list.Items[i]).Key)) return; // 직접 추가한 구분선 · 빈 아이콘만 지운다
            var keys = list.Items.Cast<ToolbarEntry>().Select(x => x.Key).ToList();
            keys.RemoveAt(i);
            Commit(keys);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
        };
        var reset = new Button { Text = "기본으로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        reset.Click += (_, _) => { Apply(() => { settings.RailOrder = new(); settings.RailHidden = new() { "files" }; }, "toolbar"); Fill(); };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange(new Control[] { up, down, addSep, addBlank, del, reset });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.Add(list);
        row.Controls.Add(buttons);
        AddRow("", row);
        AddHint("항목을 고르고 ▲ ▼ 로 순서를 바꾸고, 체크를 풀면 그 아이콘이 막대에서 사라집니다(다시 체크하면 돌아옵니다).\n" +
                "'── 여기부터 아래쪽 ──' 줄보다 뒤에 있는 아이콘은 막대 아래쪽에 붙습니다. 맨 위 로고는 늘 있고, 누르면 서재로 돌아갑니다.");
    }

    private sealed record ToolbarEntry(string Key, string Label) { public override string ToString() => Label; }

    private void BuildToolbarPage()
    {
        void SetHidden(int kind, string k, bool hidden) => Apply(() =>
        {
            var list = kind == 3 ? settings.ToolbarRefHidden : settings.ToolbarHidden;
            list.RemoveAll(x => x == k);
            if (hidden) list.Add(k);
        }, "toolbar");

        // 세 분류: txt 서식창(탭 밑 편집 줄) / 머리 막대(맨 위 오른쪽 버튼) / epub · 만화 서식창(읽기 전용 줄).
        // 각각 순서를 바꾸고 켜고 끄며, [구분선 추가]로 구분선을 늘리고 [구분선 삭제]로 직접 추가한 구분선을 지운다.
        const int Format = 0, Bar = 1, Read = 2, Ref = 3;
        string NewSepKey()
        {
            var used = new HashSet<string>(settings.ToolbarLeft.Concat(settings.ToolbarOrder).Concat(settings.ToolbarRead).Concat(settings.ToolbarRef));
            int n = 1;
            while (used.Contains("sepX" + n)) n++;
            return "sepX" + n;
        }
        CheckedListBox BuildOrderList(int kind, int height, string hint)
        {
            var list = new CheckedListBox { Width = 320, Height = height, CheckOnClick = true, IntegralHeight = false };
            bool filling = false;
            List<string> Order() => kind == Format ? MainForm.ToolbarLeftOrder(settings) : kind == Bar ? MainForm.ToolbarOrder(settings) : kind == Ref ? MainForm.ToolbarRefOrder(settings) : MainForm.ToolbarReadOrder(settings);
            List<string> Hidden() => kind == Ref ? settings.ToolbarRefHidden : settings.ToolbarHidden; // 참고파일 서식창은 숨김 목록을 따로 쓴다(같은 키가 다른 서식창에도 있어서)
            string LabelOf(string k) => kind == Ref ? MainForm.RefBarLabel(k) : MainForm.ToolbarLabel(k);
            void Fill()
            {
                filling = true;
                list.Items.Clear();
                foreach (var k in Order())
                    list.Items.Add(new ToolbarEntry(k, LabelOf(k)), !Hidden().Contains(k));
                filling = false;
            }
            Fill();
            // 글자 줄을 눌러 고르기만 해서는 체크가 바뀌지 않게: 체크 칸(왼쪽 네모)을 직접 누르거나 스페이스를 눌렀을 때만 바뀐다.
            bool allowToggle = false;
            list.CheckOnClick = false;
            list.MouseDown += (_, e) =>
            {
                int i = list.IndexFromPoint(e.Location);
                if (i < 0 || e.Button != MouseButtons.Left || e.X > LogicalToDeviceUnits(22)) return;
                list.SelectedIndex = i;
                allowToggle = true;
                try { list.SetItemChecked(i, !list.GetItemChecked(i)); }
                finally { allowToggle = false; }
            };
            list.KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Space) allowToggle = true; };
            list.KeyUp += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Space) allowToggle = false; };
            list.ItemCheck += (_, e) =>
            {
                if (filling || loading) return;
                if (!allowToggle) { e.NewValue = e.CurrentValue; return; } // 체크 칸이 아니라 글자를 눌러 생긴 변화는 무시
                SetHidden(kind, ((ToolbarEntry)list.Items[e.Index]).Key, e.NewValue == CheckState.Unchecked);
            };
            List<string> Keys() => list.Items.Cast<ToolbarEntry>().Select(x => x.Key).ToList();
            void Commit(List<string> keys, string? removedKey = null)
            {
                Apply(() =>
                {
                    if (kind == Format) settings.ToolbarLeft = keys; else if (kind == Bar) settings.ToolbarOrder = keys; else if (kind == Ref) settings.ToolbarRef = keys; else settings.ToolbarRead = keys;
                    if (removedKey != null) Hidden().RemoveAll(x => x == removedKey);
                }, "toolbar");
                Fill();
            }
            void Move(int delta)
            {
                int i = list.SelectedIndex, j = i + delta;
                if (i < 0 || j < 0 || j >= list.Items.Count) return;
                var keys = Keys();
                (keys[i], keys[j]) = (keys[j], keys[i]);
                Commit(keys);
                list.SelectedIndex = j;
            }
            var up = new Button { Text = "▲ 앞으로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            var down = new Button { Text = "▼ 뒤로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            var addSep = new Button { Text = "구분선 추가", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            var delSep = new Button { Text = "구분선 삭제", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Enabled = false };
            var reset = new Button { Text = "기본 순서로", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            up.Click += (_, _) => Move(-1);
            down.Click += (_, _) => Move(1);
            addSep.Click += (_, _) => // 고른 항목 바로 뒤(고른 게 없으면 맨 끝)에 새 구분선
            {
                var keys = Keys();
                int at = list.SelectedIndex >= 0 ? list.SelectedIndex + 1 : keys.Count;
                keys.Insert(at, NewSepKey());
                Commit(keys);
                list.SelectedIndex = at;
            };
            delSep.Click += (_, _) =>
            {
                int i = list.SelectedIndex;
                if (i < 0 || list.Items[i] is not ToolbarEntry { } e || !MainForm.IsCustomSep(e.Key)) return;
                var keys = Keys();
                keys.RemoveAt(i);
                Commit(keys, e.Key);
                if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
            };
            reset.Click += (_, _) => Commit(new List<string>());
            // 직접 추가한 구분선만 지울 수 있다(기본 구분선은 체크를 풀어 숨긴다).
            list.SelectedIndexChanged += (_, _) => delSep.Enabled = list.SelectedItem is ToolbarEntry { } s && MainForm.IsCustomSep(s.Key);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
            buttons.Controls.AddRange(new Control[] { up, down, addSep, delSep, reset });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.Add(list);
            row.Controls.Add(buttons);
            AddRow("", row);
            AddHint(hint);
            return list;
        }

        // ── txt 서식창 ──
        BeginPage("txt 서식창");
        var showBar = new CheckBox { Text = "서식창 보이기 (txt를 볼 때 탭 밑에 네비게이션 · 편집 용지 · 글꼴 · 크기 … 줄)", AutoSize = true, Checked = settings.ShowFormatBar };
        showBar.CheckedChanged += (_, _) => Apply(() => settings.ShowFormatBar = showBar.Checked, "toolbar");
        AddHeading("서식창 표시", first: true);
        AddRow("", showBar);
        AddHeading("항목 순서 · 켜고 끄기");
        BuildOrderList(Format, 300,
            "항목을 고르고 ▲ ▼ 로 순서를 바꾸고, 체크를 풀면 그 항목이 사라집니다(다시 체크하면 돌아옵니다).\n" +
            "[구분선 추가]는 고른 항목 바로 뒤에 구분선을 넣고, 직접 추가한 구분선은 고른 뒤 [구분선 삭제]로 지웁니다.");

        // ── 아이콘 막대(워크스페이스 맨 왼쪽) ──
        BuildRailPage();

        // ── 참고파일 서식창 ──
        BeginPage("참고자료 서식창");
        AddHeading("참고 파일을 열 때 나오는 줄 · 순서 · 켜고 끄기", first: true);
        BuildOrderList(Ref, 340,
            "작품의 [참고 자료] 파일(txt · epub · 만화 · pdf)을 열 때 탭 밑에 나오는 줄입니다.\n" +
            "항목을 고르고 ▲ ▼ 로 순서를 바꾸고, 체크를 풀면 그 항목이 사라집니다. 구분선도 추가 · 삭제할 수 있어요.\n" +
            "(한쪽 · 두쪽 보기와 보기 모드는 만화에서만, 수정하기와 용지 편집은 글 문서에서만 나옵니다.)");
    }

    private void BuildTtsPage()
    {
        BeginPage("읽기 · 낭독");

        AddHeading("목소리", first: true);
        var voiceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        var voices = TtsEngine.InstalledVoices();
        foreach (var v in voices) voiceBox.Items.Add(v);
        if (voices.Count == 0) { voiceBox.Items.Add("(설치된 음성이 없습니다)"); voiceBox.Enabled = false; }
        voiceBox.SelectedIndex = Math.Max(0, voices.FindIndex(v => v == settings.TtsVoice));
        voiceBox.SelectedIndexChanged += (_, _) =>
        {
            if (voices.Count > 0) Apply(() => settings.TtsVoice = (string)voiceBox.SelectedItem!, "tts");
        };
        AddRow("목소리", voiceBox);
        // 목록이 왜 이것뿐인지 알려준다. 영어 전용 음성은 한글을 못 읽어서(문장 부호만 소리 냄) 일부러 뺐다.
        AddHint("한국어를 읽을 수 있는 목소리만 보여줍니다.\nWindows 설정 > 시간 및 언어 > 음성에서\n한국어 음성을 추가하면 여기에 나타나요.");

        var rateBar = new TrackBar { Minimum = -10, Maximum = 10, TickFrequency = 5, Value = Math.Clamp(settings.TtsRate, -10, 10), Width = 200, AutoSize = false, Height = 40 };
        var rateLabel = new Label { Text = RateText(rateBar.Value), AutoSize = true, Margin = new Padding(6, 10, 0, 0) };
        rateBar.ValueChanged += (_, _) =>
        {
            rateLabel.Text = RateText(rateBar.Value);
            Apply(() => settings.TtsRate = rateBar.Value, "tts");
        };
        var rateRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        rateRow.Controls.Add(rateBar);
        rateRow.Controls.Add(rateLabel);
        AddRow("속도", rateRow);

        var previewButton = new Button { Text = "미리 듣기", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
        previewButton.Click += (_, _) =>
        {
            preview.VoiceName = settings.TtsVoice;
            preview.Rate = settings.TtsRate;
            preview.Speak("안녕하세요. 이 목소리와 속도로 소설을 읽어 드립니다.");
        };
        AddRow("", previewButton);
    }

    // ───────────────────────── 틀 ─────────────────────────

    /// <summary>새 분류를 시작한다. 이후 AddHeading/AddRow/AddHint는 이 분류의 표에 들어간다.</summary>
    private void BeginPage(string name)
    {
        // 바로 앞 분류와 이름이 같으면 새로 만들지 않고 이어 쓴다(여러 Build 함수가 한 분류를 채울 때).
        if (pages.Count > 0 && (string)pages[^1].Tag! == name) { table = pages[^1]; return; }
        table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Location = new Point(0, 14), // 제목 밑 선과 첫 소제목 사이 간격(스크롤 패널은 Padding을 안 먹는다)
            Visible = false,
            Tag = name,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pages.Add(table);
        pageHost.Controls.Add(table);
        nav.Items.Add(name);
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= pages.Count) return;
        lastPage = index;
        pageHost.SuspendLayout();
        pageHost.AutoScrollPosition = Point.Empty;
        for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == index;
        pageHost.ResumeLayout();
        pageTitle.Text = (string)pages[index].Tag!;
    }

    private void AddHeading(string text, bool first = false)
    {
        int r = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, first && r == 0 ? 0 : 16, 0, 4) };
        table.Controls.Add(label, 0, r);
        table.SetColumnSpan(label, 2);
    }

    private void AddRow(string label, Control input)
    {
        int r = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 6, 16, 6) }, 0, r);
        input.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(input, 1, r);
    }

    /// <summary>입력칸 아래 흐린 글씨 안내문(오른쪽 칸에 붙는다).</summary>
    private void AddHint(string text)
    {
        var hint = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };
        hints.Add(hint);
        AddRow("", hint);
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color back = nav.BackColor;
        using (var bg = new SolidBrush(selected ? Themes.Blend(back, theme.ChromeFore, theme.Dark ? 0.22f : 0.14f) : back))
            e.Graphics.FillRectangle(bg, e.Bounds);
        if (selected) // 고른 분류 왼쪽에 굵은 막대
            using (var bar = new SolidBrush(theme.Link))
                e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, Math.Max(3, e.Bounds.Height / 10), e.Bounds.Height);
        using var bold = new Font(nav.Font, FontStyle.Bold);
        var textRect = new Rectangle(e.Bounds.X + e.Bounds.Height / 2, e.Bounds.Y, e.Bounds.Width - e.Bounds.Height / 2, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, (string)nav.Items[e.Index], selected ? bold : nav.Font, textRect, nav.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // 직접 그리는 목록 칸 높이는 화면 배율을 따라가지 않아서 직접 맞춘다.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        nav.ItemHeight = LogicalToDeviceUnits(44);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        nav.ItemHeight = LogicalToDeviceUnits(44);
    }

    private static string RateText(int v) => v == 0 ? "보통" : v > 0 ? $"+{v} (빠름)" : $"{v} (느림)";

    private void Apply(Action set, string kind)
    {
        if (loading) return;
        set();
        settings.Save();
        changed(kind);
    }

    public void ApplyTheme(ReaderTheme t)
    {
        theme = t;
        Themes.ApplyChrome(this, t);
        // ApplyChrome이 모든 글씨를 같은 색으로 칠하므로, 안내문은 다시 흐리게, 제목 밑 선은 옅은 선 색으로 되돌린다.
        var dim = Themes.Blend(t.ChromeFore, t.Chrome, 0.4f);
        foreach (var h in hints) h.ForeColor = dim;
        titleLine.BackColor = Themes.Blend(t.Chrome, t.ChromeFore, 0.25f);
        nav.Invalidate();
    }
}
