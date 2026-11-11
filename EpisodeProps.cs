namespace CoffeeViewer;

/// <summary>
/// 회차 상세 정보에 붙이는 속성들(요약 · 작가의 말 · 줄바꿈 · 태그 · 수위 · 시점 · 등장). 소설 폴더의 .caffeum.json에 회차 파일 이름별로 저장된다.
/// [속성 추가하기]로 추가한 속성만 상세 정보에 보인다(Added).
/// </summary>
internal sealed class EpisodeProps
{
    public string? Summary { get; set; }
    public string? AuthorNote { get; set; }
    public string? LineBreak { get; set; }
    public string? Rating { get; set; }
    public string? Pov { get; set; }
    public List<string>? Tags { get; set; }
    /// <summary>이 회차에 등장하는 캐릭터 · 설정(위키 문서 제목).</summary>
    public List<string>? Appear { get; set; }
    /// <summary>상세 정보에 보이게 추가해 둔 속성 키(summary · note · linebreak · tags · rating · pov · appear).</summary>
    public List<string>? Added { get; set; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(Summary) && string.IsNullOrEmpty(AuthorNote) && string.IsNullOrEmpty(LineBreak) && string.IsNullOrEmpty(Rating) && string.IsNullOrEmpty(Pov)
        && (Tags == null || Tags.Count == 0) && (Appear == null || Appear.Count == 0) && (Added == null || Added.Count == 0);

    public EpisodeProps Clone() => new()
    {
        Summary = Summary, AuthorNote = AuthorNote, LineBreak = LineBreak, Rating = Rating, Pov = Pov,
        Tags = Tags?.ToList(), Appear = Appear?.ToList(), Added = Added?.ToList(),
    };

    /// <summary>속성 정의(키 · 이름 · 아이콘). [속성 추가하기] 목록의 순서이기도 하다.</summary>
    public static readonly (string Key, string Label, string Glyph)[] Defs =
    {
        ("summary", "요약", ""),
        ("note", "작가의 말", ""),
        ("tags", "태그", ""),
        ("rating", "수위", ""),
        ("pov", "시점", ""),
        ("appear", "등장", ""),
    };

    /// <summary>줄바꿈 처리 방식.</summary>
    public static readonly string[] LineBreakOptions = { "수동 관리", "문장마다 줄바꿈", "문단 사이 한 줄 띄우기" };
    /// <summary>수위(이용 연령).</summary>
    public static readonly string[] RatingOptions = { "전체 이용가", "12세 이상", "15세 이상", "19세 이상", "제한 관람가", "등급분류 거부" };
    /// <summary>시점.</summary>
    public static readonly string[] PovOptions = { "1인칭 주인공 시점", "1인칭 관찰자 시점", "2인칭 관찰자 시점", "3인칭 관찰자 시점", "전지적 작가 시점", "선택적 시점", "복합 시점", "의식의 흐름", "분석 불가" };
}

/// <summary>여러 줄 글을 고치는 작은 창(요약 · 작가의 말).</summary>
internal sealed class TextEditDialog : Form
{
    private readonly TextBox box;
    public string Value => box.Text.Trim();

    public TextEditDialog(string caption, string value)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        box = new TextBox { Text = value, Width = 480, Height = 160, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, 12) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(84, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(84, 30) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(box);
        root.Controls.Add(buttons);
        Controls.Add(root);
        CancelButton = cancel;
        Shown += (_, _) => { box.Focus(); box.SelectionStart = box.TextLength; };
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}
