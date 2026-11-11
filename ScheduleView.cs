using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CoffeeViewer;

/// <summary>집필 일정 한 건. Cal은 어느 캘린더(색)에 속하는지.</summary>
internal sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = "";   // yyyy-MM-dd
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string Cal { get; set; } = "";
    public int StartMin { get; set; } = -1;   // 시작 시각(0시부터 분). -1이면 종일
    public int DurMin { get; set; } = 60;     // 길이(분)
    public string Place { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>캘린더(일정 묶음): 이름 · 색 · 보이기. 달력에서 같은 색으로 보인다.</summary>
internal sealed class ScheduleCal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4aa3df";
    public bool Visible { get; set; } = true;
    public string Note { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public Color ColorValue { get { try { return ColorTranslator.FromHtml(Color); } catch { return System.Drawing.Color.SteelBlue; } } }
}

/// <summary>일정 저장소(앱 데이터 폴더의 schedule.json). 볼트와 상관없이 하나를 같이 쓴다.</summary>
internal static class ScheduleStore
{
    private sealed class Data
    {
        public List<ScheduleCal> Calendars { get; set; } = new();
        public List<ScheduleEntry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string FilePath => Path.Combine(AppSettings.DataDir, "schedule.json");
    private static Data? data;

    public static event Action? Changed;

    private static Data D
    {
        get
        {
            if (data != null) return data;
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    if (json.TrimStart().StartsWith("[")) // 예전 형식(일정 목록만)
                        data = new Data { Entries = JsonSerializer.Deserialize<List<ScheduleEntry>>(json, Opts) ?? new() };
                    else data = JsonSerializer.Deserialize<Data>(json, Opts);
                }
            }
            catch { }
            data ??= new();
            data.Calendars ??= new();
            data.Entries ??= new();
            if (data.Calendars.Count == 0)
            {
                data.Calendars.Add(new ScheduleCal { Name = "집필", Color = "#4aa3df" });
                data.Calendars.Add(new ScheduleCal { Name = "마감", Color = "#2b2570" });
                data.Calendars.Add(new ScheduleCal { Name = "휴식", Color = "#a0714f" });
            }
            foreach (var c in data.Calendars) c.Visible = true; // 보이기/숨기기 기능은 뺐다(예전에 숨긴 것도 다시 보인다)
            data.Entries.RemoveAll(e => e == null || !TryParse(e.Date, out _));
            foreach (var e in data.Entries) if (data.Calendars.All(c => c.Id != e.Cal)) e.Cal = data.Calendars[0].Id;
            return data;
        }
    }

    public static List<ScheduleCal> Calendars => D.Calendars;
    public static List<ScheduleEntry> All => D.Entries;
    public static ScheduleCal CalOf(string id) => D.Calendars.FirstOrDefault(c => c.Id == id) ?? D.Calendars[0];

    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    public static bool TryParse(string s, out DateTime d) => DateTime.TryParseExact(s, "yyyy-MM-dd", null, DateTimeStyles.None, out d);

    public static void Add(DateTime d, string text, string cal, int startMin = -1, int durMin = 60, string place = "", string note = "")
    {
        text = text.Trim();
        if (text.Length == 0) return;
        All.Add(new ScheduleEntry { Date = Key(d), Text = text, Cal = cal, StartMin = startMin, DurMin = durMin, Place = place, Note = note });
        Save();
    }

    public static void Remove(ScheduleEntry e) { All.Remove(e); Save(); }

    public static void AddCal(string name, Color color, string note = "")
    {
        Calendars.Add(new ScheduleCal { Name = name.Trim(), Color = ColorTranslator.ToHtml(color), Note = note });
        Save();
    }

    public static void RemoveCal(ScheduleCal c)
    {
        if (Calendars.Count <= 1) return;
        Calendars.Remove(c);
        All.RemoveAll(e => e.Cal == c.Id); // 그 캘린더의 일정도 함께
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(D, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>
/// 스케줄러(노션 캘린더 모양): 가운데는 큰 월간 달력(일정은 캘린더 색의 칩), 오른쪽은 미니 달력 + 캘린더(색) 목록.
/// 날짜 칸의 [+](또는 더블클릭)로 일정 추가, 칩을 누르면 완료 · 수정 · 삭제.
/// </summary>
internal sealed class ScheduleView : Panel
{
    private readonly WeekGrid grid;
    private readonly MiniMonth mini;
    private readonly CalList calList;
    private readonly Panel side;
    private readonly Panel topBar;
    private readonly FullScreenButton fullButton = new();
    private LibraryPalette? palette;
    private DateTime selected = DateTime.Today;

    public event Action? FullScreenRequested;
    public void SetFullScreenIcon(bool on) => fullButton.On = on;

    public ScheduleView()
    {
        DoubleBuffered = true;
        grid = new WeekGrid { Dock = DockStyle.Fill };
        mini = new MiniMonth { Dock = DockStyle.Top };
        calList = new CalList { Dock = DockStyle.Fill };
        side = new Panel { Dock = DockStyle.Right, Width = 260 };
        // 맨 위 가로줄(전체 화면 버튼): 아래 달력과 선으로 나눈다
        topBar = new Panel { Dock = DockStyle.Top, Height = 38 }; // 전체 화면 아이콘 높이만큼만(아이콘 32 + 위아래 3)
        topBar.Controls.Add(fullButton);
        topBar.Paint += (_, e) => { using var pen = new Pen(palette?.Line ?? Color.Gainsboro); e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1); };
        topBar.Resize += (_, _) => PlaceFull();
        side.Controls.Add(calList);
        side.Controls.Add(mini);
        side.Controls.Add(topBar);

        Controls.Add(grid);
        Controls.Add(side);
        fullButton.Click += (_, _) => FullScreenRequested?.Invoke();

        grid.DateSelected += d => Select(d);
        grid.WeekChanged += _ => mini.Month = grid.Selected;
        grid.CreateRequested += (d, a, b, at) => CreateEntry(d, a, b, at);
        grid.PaintRequested += (d, a, b) => { var br = grid.Brush; if (br == null) return; ScheduleStore.Add(d, br.Name, br.Id, a, a < 0 ? 0 : Math.Max(15, b - a)); Select(d); };
        calList.ActiveChanged += c => grid.Brush = c;
        calList.PickFirst();
        grid.EntryClicked += (en, at) => EditEntry(en, at); // 일정을 누르면 바로 보기 · 수정 창
        mini.DateSelected += d => { Select(d); grid.GoTo(d); };
        calList.Changed += () => { grid.Invalidate(); mini.Invalidate(); calList.Invalidate(); };
        ScheduleStore.Changed += OnStoreChanged;
        Disposed += (_, _) => ScheduleStore.Changed -= OnStoreChanged;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private void PlaceFull() => fullButton.SetBounds(topBar.ClientSize.Width - S(12) - S(32), (topBar.ClientSize.Height - S(32)) / 2, S(32), S(32));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); topBar.Height = S(38); side.Width = S(260); PlaceFull(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); topBar.Height = S(38); side.Width = S(260); PlaceFull(); }
    private void OnStoreChanged() { if (IsHandleCreated) { grid.Invalidate(); mini.Invalidate(); calList.Invalidate(); } }

    private void Select(DateTime d) { selected = d; grid.Selected = d; mini.Selected = d; }

    /// <summary>스케줄러를 열 때: 오늘 날짜로 맞춘다.</summary>
    public void ShowToday() { Select(DateTime.Today); grid.GoTo(DateTime.Today); mini.Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); grid.ScrollToNow(); }

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Main;
        side.BackColor = p.Side;
        topBar.BackColor = p.Dark ? p.Main : Color.White; // 전체 화면 아이콘 줄은 하얗게(어두운 테마에서는 본문색)
        fullButton.BackColor = topBar.BackColor;
        topBar.Invalidate();
        grid.SetPalette(p); mini.SetPalette(p); calList.SetPalette(p);
        fullButton.SetPalette(p);
    }

    private void CreateEntry(DateTime d, int startMin, int endMin, Point screen)
    {
        if (palette == null) return;
        using var dlg = new ScheduleEntryDialog(palette, "", ScheduleStore.Calendars.FirstOrDefault(c => c.Visible)?.Id ?? ScheduleStore.Calendars[0].Id, d, startMin, endMin);
        PlaceDialog(dlg, screen);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        ScheduleStore.Add(dlg.EntryDate, dlg.EntryText, dlg.CalId, dlg.StartMin, dlg.DurMin, dlg.Place, dlg.Note);
        Select(dlg.EntryDate); grid.GoTo(dlg.EntryDate);
    }

    private void EntryMenu(ScheduleEntry en, Point screen)
    {
        var m = new ContextMenuStrip();
        m.Items.Add(en.Done ? "완료 취소" : "완료로 표시", null, (_, _) => { en.Done = !en.Done; ScheduleStore.Save(); });
        m.Items.Add("수정…", null, (_, _) => BeginInvoke(() => EditEntry(en, screen)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("삭제", null, (_, _) => ScheduleStore.Remove(en));
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        m.Show(screen);
    }

    private void EditEntry(ScheduleEntry en, Point screen)
    {
        if (palette == null || !ScheduleStore.TryParse(en.Date, out var d)) return;
        using var dlg = new ScheduleEntryDialog(palette, en.Text, en.Cal, d, en.StartMin, en.StartMin < 0 ? -1 : en.StartMin + en.DurMin, en.Place, en.Note, en.Done, editing: true);
        PlaceDialog(dlg, screen);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (dlg.Deleted) { ScheduleStore.Remove(en); return; }
        en.Text = dlg.EntryText.Trim();
        en.Cal = dlg.CalId;
        en.Date = ScheduleStore.Key(dlg.EntryDate);
        en.StartMin = dlg.StartMin;
        en.DurMin = dlg.DurMin;
        en.Place = dlg.Place;
        en.Note = dlg.Note;
        en.Done = dlg.Done;
        ScheduleStore.Save();
    }
    /// <summary>스케줄 창은 늘 가운데(스케줄러가 들어 있는 창의 한가운데)에 뜬다.</summary>
    private static void PlaceDialog(Form f, Point screen) => f.StartPosition = FormStartPosition.CenterParent;
    // ───────── 가운데: 주간 시간표 ─────────

    /// <summary>한 주(일~토) 시간표: 위에 종일 줄, 아래에 시간 줄(0~24시, 휠로 스크롤). 빈 곳을 끌면 그 시간대로 새 일정.</summary>
    private sealed class WeekGrid : Control
    {
        private LibraryPalette? p;
        private DateTime weekStart = StartOfWeek(DateTime.Today);
        private DateTime selectedDay = DateTime.Today;
        private int scrollY;
        private Rectangle prevRect, nextRect, todayRect;
        private readonly Rectangle[] viewRects = new Rectangle[3]; // 월 · 주 · 일 보기 전환 아이콘
        private int titleRight;
        private int curView = 1;                                    // 0 월 · 1 주 · 2 일 (지금은 주만)
        private readonly ToolTip tip = new();
        private int hot = -1; // 0 이전 · 1 오늘 · 2 다음
        private readonly List<(Rectangle R, ScheduleEntry E)> blocks = new();
        private readonly System.Windows.Forms.Timer clock = new() { Interval = 30_000 };
        // 끌어서 새 일정
        private bool dragging;
        private int dragCol, dragA, dragB;
        private bool dragAll;
        private ScheduleEntry? lastBlock;
        private long lastBlockTick;

        public event Action<DateTime>? DateSelected;
        public event Action<DateTime>? WeekChanged;
        public event Action<DateTime, int, int, Point>? CreateRequested; // 날짜, 시작(분, 종일 -1), 끝(분), 화면 위치
        /// <summary>오른쪽에서 고른 스케줄(색)로 칠하기: 날짜, 시작(분, 시간 없이 -1), 끝(분).</summary>
        public event Action<DateTime, int, int>? PaintRequested;
        /// <summary>지금 고른 스케줄(붓). 있으면 시간표를 누르거나 끌 때 그 색으로 바로 칠해진다.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ScheduleCal? Brush { get; set; }
        public event Action<ScheduleEntry, Point>? EntryClicked;

        public static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(int)d.DayOfWeek);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Selected { get => selectedDay; set { selectedDay = value; var ws = StartOfWeek(value); if (ws != weekStart) { weekStart = ws; WeekChanged?.Invoke(ws); } Invalidate(); } }

        public WeekGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            clock.Tick += (_, _) => Invalidate();
            clock.Start();
            Disposed += (_, _) => { clock.Dispose(); tip.Dispose(); };
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        private int TitleH => S(108);
        private int DayHeadH => S(58);
        private int Gutter => S(58);
        private int HourH => S(52);
        private int ColW => Math.Max(S(40), (Width - Gutter - S(44)) / 7); // 오른쪽에 주 이동 화살표 자리
        private int AllDayH
        {
            get
            {
                int most = 0;
                var vis = VisibleIds();
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    most = Math.Max(most, ScheduleStore.All.Count(e => e.Date == k && vis.Contains(e.Cal)));
                }
                return S(10) + Math.Min(3, Math.Max(1, most)) * S(22);
            }
        }
        private int GridTop => TitleH + DayHeadH + AllDayH;
        private Rectangle GridRect => new(0, GridTop, Width, Math.Max(0, Height - GridTop));
        private int MaxScroll => Math.Max(0, 24 * HourH - GridRect.Height + S(8));
        private HashSet<string> VisibleIds() => ScheduleStore.Calendars.Where(c => c.Visible).Select(c => c.Id).ToHashSet();

        /// <summary>그 날짜의 주로 옮긴다(시간 줄은 그대로).</summary>
        public void GoTo(DateTime d) { selectedDay = d; var ws = StartOfWeek(d); if (ws != weekStart) { weekStart = ws; WeekChanged?.Invoke(ws); } Invalidate(); }

        /// <summary>열 때: 지금 시각 조금 위로 스크롤.</summary>
        public void ScrollToNow()
        {
            int hour = Math.Clamp(DateTime.Now.Hour - 2, 0, 23);
            scrollY = Math.Clamp(hour * HourH, 0, MaxScroll);
            Invalidate();
        }

        private void LayoutButtons()
        {
            int bs = S(34);

            todayRect = Rectangle.Empty; // [오늘] 버튼은 뺐다
            int cy = TitleH + S(38) - S(18); // 날짜 숫자 줄과 같은 높이
            prevRect = new Rectangle(S(12), cy, S(36), S(36));      // ‹ 지난주
            nextRect = new Rectangle(Width - S(46), cy, S(36), S(36)); // › 다음주
            titleRight = Width - S(60);
            for (int k = 0; k < 3; k++) viewRects[k] = new Rectangle(S(28) + k * S(44), S(30), S(44), bs);
        }

        private int ColAt(int x) { int c = (x - Gutter) / ColW; return x >= Gutter && c is >= 0 and < 7 ? c : -1; }
        private int MinuteAt(int y) => Math.Clamp((int)Math.Round((y - GridTop + scrollY) / (double)HourH * 60 / 15.0) * 15, 0, 24 * 60);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LayoutButtons();
            if (dragging) { dragB = dragAll ? -1 : MinuteAt(e.Y); Invalidate(); return; }
            int h = prevRect.Contains(e.Location) ? 0 : todayRect.Contains(e.Location) ? 1 : nextRect.Contains(e.Location) ? 2 : viewRects[0].Contains(e.Location) ? 3 : viewRects[1].Contains(e.Location) ? 4 : viewRects[2].Contains(e.Location) ? 5 : -1;
            bool onBlock = blocks.Any(b => b.R.Contains(e.Location));
            Cursor = h >= 0 || onBlock ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; tip.SetToolTip(this, h == 3 ? "월 (준비 중)" : h == 4 ? "주" : h == 5 ? "일 (준비 중)" : null); Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            LayoutButtons();
            if (prevRect.Contains(e.Location) || todayRect.Contains(e.Location) || nextRect.Contains(e.Location) || viewRects.Any(v => v.Contains(e.Location))) return;
            if (blocks.Any(b => b.R.Contains(e.Location))) return;
            int col = ColAt(e.X);
            if (col < 0) return;
            if (e.Y >= TitleH + DayHeadH && e.Y < GridTop) { dragging = true; dragAll = true; dragCol = col; dragA = dragB = -1; Capture = true; return; } // 종일 줄
            if (!GridRect.Contains(e.Location)) { // 요일 머리: 그 날을 고른다
                if (e.Y >= TitleH && e.Y < TitleH + DayHeadH) { selectedDay = weekStart.AddDays(col); DateSelected?.Invoke(selectedDay); Invalidate(); }
                return;
            }
            dragging = true; dragAll = false; dragCol = col; dragA = dragB = MinuteAt(e.Y); Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false; Capture = false;
                var day = weekStart.AddDays(dragCol);
                var at = PointToScreen(new Point(Gutter + dragCol * ColW + ColW, Math.Clamp(e.Y, GridTop, Height - S(40))));
                if (dragAll) { if (Brush != null) PaintRequested?.Invoke(day, -1, -1); else CreateRequested?.Invoke(day, -1, -1, at); }
                else
                {
                    int a = Math.Min(dragA, dragB), b = Math.Max(dragA, dragB);
                    if (b - a < 15) b = a + 60;          // 그냥 누르면 한 시간짜리
                    b = Math.Min(b, 24 * 60);
                    if (b <= a) a = b - 15;
                    if (Brush != null) PaintRequested?.Invoke(day, a, b); else CreateRequested?.Invoke(day, a, b, at);
                }
                Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            // 일정(블록 · 칩): 두 번 연속 누르면 보기 · 수정 창(프레임워크의 더블클릭 이벤트에 기대지 않고 직접 센다)
            foreach (var (r, en) in blocks)
                if (r.Contains(e.Location))
                {
                    EntryClicked?.Invoke(en, PointToScreen(new Point(r.Left, r.Bottom))); // 한 번 누르면 바로 보기 · 수정 창
                    return;
                }
            LayoutButtons();
            if (prevRect.Contains(e.Location)) { GoTo(weekStart.AddDays(-7 + (int)selectedDay.DayOfWeek)); DateSelected?.Invoke(selectedDay); }
            else if (nextRect.Contains(e.Location)) { GoTo(weekStart.AddDays(7 + (int)selectedDay.DayOfWeek)); DateSelected?.Invoke(selectedDay); }
            else if (todayRect.Contains(e.Location)) { GoTo(DateTime.Today); DateSelected?.Invoke(DateTime.Today); ScrollToNow(); }
        }

        /// <summary>일정(블록 · 칩)을 두 번 누르면 보기 · 수정 창이 뜬다.</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var (r, en) in blocks)
                if (r.Contains(e.Location)) { EntryClicked?.Invoke(en, PointToScreen(new Point(r.Left, r.Bottom))); return; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scrollY = Math.Clamp(scrollY - e.Delta / 120 * HourH, 0, MaxScroll);
            Invalidate();
        }

        private static string HourLabel(int h) => h == 0 ? "" : h == 12 ? "정오" : h < 12 ? $"오전 {h}시" : $"오후 {h - 12}시";
        public static string TimeText(int min) { int h = min / 60 % 24, m = min % 60; return $"{(h < 12 ? "오전" : "오후")} {(h % 12 == 0 ? 12 : h % 12)}:{m:00}"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            LayoutButtons();
            blocks.Clear();
            scrollY = Math.Clamp(scrollY, 0, MaxScroll);
            string ff = NovelStore.UiFontFamily;
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Color red = Color.FromArgb(0xE5, 0x48, 0x4D);
            Color lineCol = Themes.Blend(pal.Main, pal.Fore, 0.10f);
            var vis = VisibleIds();
            int colW = ColW, gutter = Gutter;

            // 시간 줄(스크롤): 0~24시
            var grid = GridRect;
            var state = g.Save();
            g.SetClip(grid);
            using (var lp = new Pen(lineCol))
            using (var gf = new Font(ff, 8f))
            {
                for (int h = 0; h <= 24; h++)
                {
                    int y = grid.Top + h * HourH - scrollY;
                    if (y < grid.Top - HourH || y > grid.Bottom + HourH) continue;
                    g.DrawLine(lp, gutter - S(6), y, gutter + 7 * colW, y);
                    if (h is > 0 and < 24 && y - S(9) >= grid.Top && y + S(9) <= grid.Bottom) // 영역 경계에 걸쳐 반쯤 잘려 보이는 시각 글자는 그리지 않는다
                        TextRenderer.DrawText(g, HourLabel(h), gf, new Rectangle(0, y - S(9), gutter - S(10), S(18)), Themes.Blend(pal.Main, pal.Fore, 0.5f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                for (int c = 0; c <= 7; c++) g.DrawLine(lp, gutter + c * colW, grid.Top, gutter + c * colW, grid.Bottom);
            }
            // 오늘 열을 아주 옅게
            for (int c = 0; c < 7; c++)
                if (weekStart.AddDays(c).Date == DateTime.Today)
                {
                    using var tb = new SolidBrush(Color.FromArgb(pal.Dark ? 18 : 14, pal.Accent));
                    g.FillRectangle(tb, gutter + c * colW + 1, grid.Top, colW - 1, grid.Height);
                }

            using (var tf2 = new Font(ff, 9f, FontStyle.Bold))
            using (var sf = new Font(ff, 8f))
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    var list = ScheduleStore.All.Where(x => x.Date == k && x.StartMin >= 0 && vis.Contains(x.Cal)).OrderBy(x => x.StartMin).ThenBy(x => x.DurMin).ToList();
                    // 겹치는 일정은 옆으로 나란히(차선 배정)
                    var lanes = new List<int>(); var laneEnd = new List<int>(); var cluster = new List<int>();
                    int clusterEnd = -1, clusterStart = 0;
                    var laneOf = new int[list.Count]; var lanesIn = new int[list.Count];
                    for (int i = 0; i < list.Count; i++)
                    {
                        int s0 = list[i].StartMin, e0 = s0 + Math.Max(15, list[i].DurMin);
                        if (s0 >= clusterEnd) { for (int j = clusterStart; j < i; j++) lanesIn[j] = laneEnd.Count; laneEnd.Clear(); clusterStart = i; clusterEnd = -1; }
                        int lane = laneEnd.FindIndex(le => le <= s0);
                        if (lane < 0) { laneEnd.Add(e0); lane = laneEnd.Count - 1; } else laneEnd[lane] = e0;
                        laneOf[i] = lane; clusterEnd = Math.Max(clusterEnd, e0);
                    }
                    for (int j = clusterStart; j < list.Count; j++) lanesIn[j] = Math.Max(1, laneEnd.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var en = list[i];
                        int w = (colW - S(6)) / Math.Max(1, lanesIn[i]);
                        int y0 = grid.Top + (int)(en.StartMin * HourH / 60.0) - scrollY;
                        int hgt = Math.Max(S(20), (int)(Math.Max(15, en.DurMin) * HourH / 60.0) - S(2));
                        var r = new Rectangle(gutter + c * colW + S(3) + laneOf[i] * w, y0 + 1, w - S(2), hgt);
                        if (r.Bottom < grid.Top || r.Top > grid.Bottom) { blocks.Add((r, en)); continue; }
                        DrawBlock(g, en, r, tf2, sf, pal);
                        blocks.Add((Rectangle.Intersect(r, grid), en));
                    }
                }

            // 끌어서 만드는 중인 일정(유령)
            if (dragging && !dragAll)
            {
                int a = Math.Min(dragA, dragB), b = Math.Max(dragA, dragB);
                if (b - a < 15) b = a + 60;
                var r = new Rectangle(gutter + dragCol * colW + S(3), grid.Top + (int)(a * HourH / 60.0) - scrollY, colW - S(6), (int)((b - a) * HourH / 60.0));
                var brushCol = Brush?.ColorValue ?? pal.Accent;
                using var gb = new SolidBrush(Color.FromArgb(110, brushCol));
                using var gp = LibraryPalette.Rounded(r, S(6));
                g.FillPath(gb, gp);
                using var gpen = new Pen(brushCol, S(1)); g.DrawPath(gpen, gp);
                using var f = new Font(ff, 8f);
                TextRenderer.DrawText(g, $"{TimeText(a)} – {TimeText(b)}", f, Rectangle.Inflate(r, -S(6), 0), pal.Fore, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }

            // 지금 시각 선: 이번 주에는 한 주 전체(일~토)를 가로질러 이어진다. 오늘 열은 진하게 + 점, 나머지 날은 옅게.
            for (int c = 0; c < 7; c++)
                if (weekStart.AddDays(c).Date == DateTime.Today)
                {
                    int y = grid.Top + (int)(DateTime.Now.TimeOfDay.TotalMinutes * HourH / 60.0) - scrollY;
                    using (var thin = new Pen(Color.FromArgb(110, red), Math.Max(1f, S(1)))) g.DrawLine(thin, gutter, y, gutter + 7 * colW, y);
                    using var rp = new Pen(red, S(2)); using var rb = new SolidBrush(red);
                    g.DrawLine(rp, gutter + c * colW, y, gutter + (c + 1) * colW, y);
                    g.FillEllipse(rb, gutter + c * colW - S(4), y - S(4), S(8), S(8));
                }            g.Restore(state);

            // 시간 줄에서 위로 삐져나온 글자·블록은 머리 영역(제목 · 요일 · 종일 줄)을 바탕으로 덮어서 가린다
            using (var hb = new SolidBrush(pal.Main)) g.FillRectangle(hb, 0, 0, Width, GridTop);


            // 제목: "11월 2026" (고른 날의 달) — 오른쪽, 이전/오늘/다음 버튼 바로 왼쪽
            using (var mf = new Font(ff, 26f, FontStyle.Bold))
            using (var yf = new Font(ff, 26f, FontStyle.Regular))
            {
                string mt = $" {selectedDay.Month}월", yt = $"{selectedDay.Year}";
                var msz = TextRenderer.MeasureText(g, mt, mf, Size.Empty, TextFormatFlags.NoPadding);
                var ysz = TextRenderer.MeasureText(g, yt, yf, Size.Empty, TextFormatFlags.NoPadding);
                int tx = titleRight - (msz.Width + ysz.Width), ty = S(47) - msz.Height / 2;
                TextRenderer.DrawText(g, yt, yf, new Point(tx, ty), Themes.Blend(pal.Main, pal.Fore, 0.55f), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, mt, mf, new Point(tx + ysz.Width, ty), pal.Fore, TextFormatFlags.NoPadding);
            }            DrawViewSwitch(g, pal);
            DrawChevron(g, prevRect, true, hot == 0 ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.85f));
            DrawChevron(g, nextRect, false, hot == 2 ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.85f));            using (var bf = new Font(ff, 10f))
            using (var icon = new Font(MainForm.IconFontName(), 10f))
            {
                void Btn(Rectangle r, int idx, string text, bool isIcon)
                {
                    using var path = LibraryPalette.Rounded(r, S(8));
                    if (hot == idx) { using var fill = new SolidBrush(pal.CardHover); g.FillPath(fill, path); }
                    using var pen = new Pen(pal.Line); g.DrawPath(pen, path);
                    TextRenderer.DrawText(g, text, isIcon ? icon : bf, r, pal.Fore, mid);
                }
            }

            // 요일 머리: 요일 + 날짜 숫자(오늘은 빨간 원)
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 9f))
            using (var nf = new Font(ff, 13f, FontStyle.Bold))
                for (int c = 0; c < 7; c++)
                {
                    var d = weekStart.AddDays(c);
                    int x = gutter + c * colW;
                    Color wc = c == 0 ? red : c == 6 ? Color.FromArgb(0x4A, 0x7F, 0xD0) : Themes.Blend(pal.Main, pal.Fore, 0.6f);
                    TextRenderer.DrawText(g, days[c], wf, new Rectangle(x, TitleH, colW, S(20)), wc, mid);
                    var circle = new Rectangle(x + (colW - S(32)) / 2, TitleH + S(22), S(32), S(32));
                    bool isToday = d.Date == DateTime.Today;
                    if (isToday) { using var b = new SolidBrush(red); g.FillEllipse(b, circle); }
                    else if (d.Date == selectedDay.Date) { using var pen = new Pen(pal.Accent, S(2)); g.DrawEllipse(pen, circle); }
                    TextRenderer.DrawText(g, d.Day.ToString(), nf, circle, isToday ? Color.White : pal.Fore, mid);
                }

            // 종일 줄
            int adTop = TitleH + DayHeadH, adH = AllDayH;
            using (var lp = new Pen(lineCol)) { g.DrawLine(lp, 0, adTop, Width, adTop); g.DrawLine(lp, 0, adTop + adH, Width, adTop + adH); }
            using (var af = new Font(ff, 8f))
                TextRenderer.DrawText(g, "스케줄", af, new Rectangle(0, adTop, gutter - S(8), adH), Themes.Blend(pal.Main, pal.Fore, 0.5f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var itemFont = new Font(ff, 8.5f))
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    var list = ScheduleStore.All.Where(x => x.Date == k && vis.Contains(x.Cal)).OrderBy(x => x.StartMin).ThenBy(x => x.Text).ToList(); // 그날 적어 둔 일정을 짧게 모아서 위에
                    int show = list.Count <= 3 ? list.Count : 2;
                    for (int i = 0; i < show; i++)
                    {
                        var r = new Rectangle(gutter + c * colW + S(3), adTop + S(5) + i * S(22), colW - S(6), S(19));
                        DrawChip(g, list[i], r, itemFont, pal, left, true);
                        blocks.Add((r, list[i]));
                    }
                    if (list.Count > 3)
                        TextRenderer.DrawText(g, $"+{list.Count - 2}개 더", itemFont, new Rectangle(gutter + c * colW + S(8), adTop + S(5) + 2 * S(22), colW - S(12), S(19)), Themes.Blend(pal.Main, pal.Fore, 0.55f),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }

        }

        /// <summary>왼쪽의 보기 전환: 월 · 주 · 일 아이콘(지금 보기는 눌린 모양). 월 · 일은 나중에 채운다.</summary>
        private void DrawViewSwitch(Graphics g, LibraryPalette pal)
        {
            var all = Rectangle.Union(Rectangle.Union(viewRects[0], viewRects[1]), viewRects[2]);
            using (var path = LibraryPalette.Rounded(all, S(8))) using (var pen = new Pen(pal.Line)) g.DrawPath(pen, path);
            for (int k = 0; k < 3; k++)
            {
                var r = viewRects[k];
                bool on = k == curView;
                if (on)
                {
                    using var path = LibraryPalette.Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(6));
                    using var fill = new SolidBrush(Themes.Blend(pal.Main, pal.Fore, pal.Dark ? 0.20f : 0.12f));
                    g.FillPath(fill, path);
                }
                else if (hot == 3 + k)
                {
                    using var path = LibraryPalette.Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(6));
                    using var fill = new SolidBrush(pal.CardHover);
                    g.FillPath(fill, path);
                }
                Color ic = on ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.55f);
                DrawCalIcon(g, new Rectangle(r.X + (r.Width - S(28)) / 2, r.Y + (r.Height - S(28)) / 2, S(28), S(28)), k, ic, pal);            }
        }
        /// <summary>달력 모양 아이콘: 위의 고리 두 개 + 칸. kind 0 월(칸이 가득) · 1 주(WEEK) · 2 일(DAY).</summary>
        private void DrawCalIcon(Graphics g, Rectangle r, int kind, Color col, LibraryPalette pal)
        {
            float lw = Math.Max(1.6f, r.Width / 13f);
            var body = new RectangleF(r.X + lw, r.Y + r.Height * 0.16f, r.Width - lw * 2, r.Height * 0.80f);
            using var pen = new Pen(col, lw) { LineJoin = LineJoin.Round };
            using var br = new SolidBrush(col);
            using (var bp = LibraryPalette.Rounded(Rectangle.Round(body), (int)(r.Width * 0.14f))) g.DrawPath(pen, bp);
            // 고리 두 개
            float rx1 = body.X + body.Width * 0.30f, rx2 = body.X + body.Width * 0.70f;
            using (var rp = new Pen(col, lw * 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(rp, rx1, r.Y + r.Height * 0.03f, rx1, r.Y + r.Height * 0.26f);
                g.DrawLine(rp, rx2, r.Y + r.Height * 0.03f, rx2, r.Y + r.Height * 0.26f);
            }
            float top = body.Y + body.Height * 0.26f;
            if (kind == 0) // 월: 머리 줄 + 칸 격자(채운 네모)
            {
                g.DrawLine(pen, body.X, top, body.Right, top);
                float cw = body.Width / 5f, chh = (body.Bottom - top) / 3.4f;
                for (int row = 0; row < 3; row++)
                    for (int c = 0; c < 4; c++)
                    {
                        if (row == 0 && c == 0) continue;
                        float bx = body.X + cw * 0.45f + c * cw, by = top + chh * 0.35f + row * chh * 1.0f;
                        g.FillRectangle(br, bx, by, cw * 0.72f, chh * 0.72f);
                    }
            }
            else
            {
                using var tf = new Font(NovelStore.UiFontFamily, kind == 1 ? Math.Max(5f, r.Width * 0.235f) : Math.Max(5f, r.Width * 0.31f), FontStyle.Bold, GraphicsUnit.Pixel);
                var tr = new Rectangle((int)body.X, (int)(top - lw), (int)body.Width, (int)(body.Bottom - top + lw));
                TextRenderer.DrawText(g, kind == 1 ? "WEEK" : "DAY", tf, tr, col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
        /// <summary>주 이동 화살표(얇고 큰 꺾쇠).</summary>
        private void DrawChevron(Graphics g, Rectangle r, bool left, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, h = S(11), w = S(6);
            using var pen = new Pen(col, S(2) + 0.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float dx = left ? w : -w;
            g.DrawLines(pen, new[] { new PointF(cx + dx, cy - h), new PointF(cx - dx, cy), new PointF(cx + dx, cy + h) });
        }
        private static void DrawChip(Graphics g, ScheduleEntry en, Rectangle r, Font f, LibraryPalette pal, TextFormatFlags left, bool allDay)
        {
            var col = ScheduleStore.CalOf(en.Cal).ColorValue;
            using var path = LibraryPalette.Rounded(r, 5);
            using var fill = new SolidBrush(Themes.Blend(pal.Main, col, en.Done ? 0.10f : (pal.Dark ? 0.35f : 0.22f)));
            g.FillPath(fill, path);
            using var bar = new SolidBrush(en.Done ? Themes.Blend(pal.Main, col, 0.4f) : col);
            g.FillRectangle(bar, r.X, r.Y + 3, 3, r.Height - 6);
            using var ff2 = new Font(f, en.Done ? FontStyle.Strikeout : FontStyle.Regular);
            TextRenderer.DrawText(g, en.Text, ff2, new Rectangle(r.X + 9, r.Y, r.Width - 12, r.Height), en.Done ? pal.Dim : pal.Fore, left);
        }

        private void DrawBlock(Graphics g, ScheduleEntry en, Rectangle r, Font title, Font sub, LibraryPalette pal)
        {
            var col = ScheduleStore.CalOf(en.Cal).ColorValue;
            using var path = LibraryPalette.Rounded(r, S(6));
            using var fill = new SolidBrush(Themes.Blend(pal.Main, col, en.Done ? 0.12f : (pal.Dark ? 0.40f : 0.26f)));
            g.FillPath(fill, path);
            var oldClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            using var bar = new SolidBrush(en.Done ? Themes.Blend(pal.Main, col, 0.4f) : col);
            g.FillRectangle(bar, r.X, r.Y, S(4), r.Height);
            g.Clip = oldClip;
            using var tf = new Font(title, en.Done ? FontStyle.Bold | FontStyle.Strikeout : FontStyle.Bold);
            var tr = new Rectangle(r.X + S(9), r.Y + S(2), r.Width - S(12), Math.Min(r.Height - S(2), title.Height + S(2)));
            TextRenderer.DrawText(g, en.Text, tf, tr, en.Done ? pal.Dim : pal.Fore, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (r.Height >= S(44))
                TextRenderer.DrawText(g, $"{TimeText(en.StartMin)} – {TimeText(en.StartMin + Math.Max(15, en.DurMin))}", sub,
                    new Rectangle(r.X + S(9), r.Y + S(4) + title.Height + S(2), r.Width - S(12), sub.Height), en.Done ? pal.Dim : Themes.Blend(pal.Main, pal.Fore, 0.7f),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    // ───────── 오른쪽 미니 달력 ─────────

    private sealed class MiniMonth : Control
    {
        private LibraryPalette? p;
        private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime selectedDay = DateTime.Today;
        private Rectangle upRect, downRect, titleRect;
        private readonly Rectangle[] cells = new Rectangle[42];
        private int rows = 6;
        private int hot = -1;

        public event Action<DateTime>? DateSelected;
        public event Action<DateTime>? MonthChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Month { get => month; set { var m = new DateTime(value.Year, value.Month, 1); if (m == month) return; month = m; Invalidate(); } }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Selected { get => selectedDay; set { selectedDay = value; Invalidate(); } }

        public MiniMonth()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Height = 312;
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Side; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(312); }
        private DateTime FirstCell => month.AddDays(-(int)month.DayOfWeek);

        private void LayoutCells()
        {
            rows = ((int)month.DayOfWeek + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7;
            int pad = S(14), top = S(96), cw = (Width - pad * 2) / 7, ch = S(36);
            for (int i = 0; i < 42; i++) cells[i] = i < rows * 7 ? new Rectangle(pad + (i % 7) * cw, top + (i / 7) * ch, cw, ch) : Rectangle.Empty;
            int bs = S(28);
            downRect = new Rectangle(Width - pad - bs, S(16), bs, bs);
            upRect = new Rectangle(downRect.X - bs, S(16), bs, bs);
            titleRect = new Rectangle(pad + S(4), S(16), Width - pad * 2 - bs * 2, bs);
        }

        private int HitAt(Point pt)
        {
            LayoutCells();
            if (upRect.Contains(pt)) return 0;
            if (downRect.Contains(pt)) return 1;
            for (int i = 0; i < rows * 7; i++) if (cells[i].Contains(pt)) return 100 + i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h == 0) { month = month.AddMonths(-1); MonthChanged?.Invoke(month); Invalidate(); }
            else if (h == 1) { month = month.AddMonths(1); MonthChanged?.Invoke(month); Invalidate(); }
            else if (h >= 100) { var d = FirstCell.AddDays(h - 100); selectedDay = d; DateSelected?.Invoke(d); Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); month = month.AddMonths(e.Delta > 0 ? -1 : 1); MonthChanged?.Invoke(month); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            LayoutCells();
            string ff = NovelStore.UiFontFamily;
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Color red = Color.FromArgb(0xE5, 0x48, 0x4D);

            using (var tf = new Font(ff, 11f, FontStyle.Bold))
                TextRenderer.DrawText(g, $"{month.Year}년 {month.Month}월", tf, titleRect, pal.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var af = new Font(MainForm.IconFontName(), 9f))
            {
                TextRenderer.DrawText(g, "", af, upRect, hot == 0 ? pal.Fore : pal.Dim, mid);
                TextRenderer.DrawText(g, "", af, downRect, hot == 1 ? pal.Fore : pal.Dim, mid);
            }
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 8.5f))
                for (int c = 0; c < 7; c++)
                    TextRenderer.DrawText(g, days[c], wf, new Rectangle(cells[c].X, S(64), cells[c].Width, S(20)), Themes.Blend(pal.Side, pal.Fore, 0.5f), mid);

            // 고른 날이 있는 주(한 줄)를 둥근 띠로
            var first = FirstCell;
            for (int r = 0; r < rows; r++)
            {
                bool hit = false;
                for (int c = 0; c < 7; c++) if (first.AddDays(r * 7 + c).Date == selectedDay.Date) hit = true;
                if (!hit) continue;
                var band = Rectangle.FromLTRB(cells[r * 7].X, cells[r * 7].Y + S(3), cells[r * 7 + 6].Right, cells[r * 7].Bottom - S(3));
                using var path = LibraryPalette.Rounded(band, S(10));
                using var bb = new SolidBrush(Themes.Blend(pal.Side, pal.Fore, pal.Dark ? 0.14f : 0.09f));
                g.FillPath(bb, path);
            }

            using var df = new Font(ff, 9.5f);
            for (int i = 0; i < rows * 7; i++)
            {
                var d = first.AddDays(i);
                bool inMonth = d.Month == month.Month, isToday = d.Date == DateTime.Today;
                var r = cells[i];
                var circle = new Rectangle(r.X + (r.Width - S(28)) / 2, r.Y + (r.Height - S(28)) / 2, S(28), S(28));
                Color nc = inMonth ? pal.Fore : Themes.Blend(pal.Side, pal.Fore, 0.3f);
                if (isToday) { using var b = new SolidBrush(red); g.FillRoundedRect(circle, S(8)); nc = Color.White; }
                else if (hot == 100 + i) { using var b = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(circle, S(8)); g.FillPath(b, hp); }
                TextRenderer.DrawText(g, d.Day.ToString(), df, circle, nc, mid);
            }
        }
    }

    // ───────── 오른쪽 캘린더(색) 목록 ─────────

    private sealed class CalList : Control
    {
        private LibraryPalette? p;
        private int hot = -1;
        private string? activeId;
        public event Action? Changed;
        /// <summary>고른 스케줄(붓)이 바뀌었다.</summary>
        public event Action<ScheduleCal?>? ActiveChanged;

        /// <summary>지금 고른 스케줄: 시간표를 누르거나 끌면 이 색으로 칠해진다. 숨겨 둔 것은 못 고른다.</summary>
        public ScheduleCal? Active => ScheduleStore.Calendars.FirstOrDefault(c => c.Id == activeId && c.Visible);

        private void SetActive(ScheduleCal? c)
        {
            if (c?.Id == activeId) return;
            activeId = c?.Id;
            ActiveChanged?.Invoke(Active);
            Invalidate();
        }

        public void PickFirst() { if (Active == null) SetActive(ScheduleStore.Calendars.FirstOrDefault(c => c.Visible)); }

        public CalList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Side; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        private int RowH => S(34);
        private Rectangle RowRect(int i) => new(S(8), S(46) + i * RowH, Width - S(16), RowH - S(2));
        private Rectangle SwatchRect(int i) { var r = RowRect(i); return new Rectangle(r.X + S(10), r.Y + (r.Height - S(22)) / 2, S(22), S(22)); }
        private int Count => ScheduleStore.Calendars.Count;
        private Rectangle PlusRect() => new(Width - S(8) - S(30), S(8), S(30), S(30)); // "내 스케줄" 줄 오른쪽 끝의 +

        private int HitAt(Point pt)
        {
            if (PlusRect().Contains(pt)) return 1000;
            for (int i = 0; i < Count; i++) if (RowRect(i).Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int h = HitAt(e.Location);
            if (h < 0) return;
            if (h == 1000) { AddCalendar(); return; } // 머리줄의 +
            var cal = ScheduleStore.Calendars[h];
            if (e.Button == MouseButtons.Right) { CalMenu(cal, PointToScreen(e.Location)); return; }
            if (MoreRect(h).Contains(e.Location)) { CalMenu(cal, PointToScreen(new Point(MoreRect(h).Left, MoreRect(h).Bottom))); return; } // ⋯
            SetActive(cal); // 줄을 누르면 그 스케줄(색)을 붓으로 고른다
        }

        private Rectangle MoreRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - S(34), r.Y + (r.Height - S(26)) / 2, S(28), S(26)); }

        /// <summary>점 세 개(⋯): 글꼴 모양에 기대지 않고 직접 그려서 칸의 정중앙에 놓는다.</summary>
        private void DrawMore(Graphics g, Rectangle r, Color col, bool hover)
        {
            if (hover) { using var hb = new SolidBrush(Color.FromArgb(40, col)); using var hp = LibraryPalette.Rounded(r, S(6)); g.FillPath(hb, hp); }
            float rad = S(2) + 0.3f, gap = S(6), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using var b = new SolidBrush(col);
            for (int k = -1; k <= 1; k++) g.FillEllipse(b, cx + k * gap - rad, cy - rad, rad * 2, rad * 2);
        }

        private void AddCalendar()
        {
            using var dlg = new ScheduleDefDialog(p!, "", Color.FromArgb(0x4A, 0xA3, 0xDF), "");
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            ScheduleStore.AddCal(dlg.DefName, dlg.DefColor, dlg.DefNote);
            SetActive(ScheduleStore.Calendars[^1]); // 만든 스케줄을 바로 붓으로 고른다
            Changed?.Invoke();
        }

        private void EditCalendar(ScheduleCal cal)
        {
            using var dlg = new ScheduleDefDialog(p!, cal.Name, cal.ColorValue, cal.Note);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            cal.Name = dlg.DefName;
            cal.Color = ColorTranslator.ToHtml(dlg.DefColor);
            cal.Note = dlg.DefNote;
            ScheduleStore.Save();
            Changed?.Invoke();
        }

        /// <summary>⋯ 메뉴: 수정하기 / 삭제.</summary>
        private void CalMenu(ScheduleCal cal, Point screen)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("수정하기", null, (_, _) => BeginInvoke(() => EditCalendar(cal)));
            var del = new ToolStripMenuItem("삭제") { Enabled = ScheduleStore.Calendars.Count > 1 };
            del.Click += (_, _) => BeginInvoke(() =>
            {
                if (MessageBox.Show(FindForm(), $"'{cal.Name}' 스케줄과 그 일정을 모두 지울까요?\n되돌릴 수 없어요.", "스케줄 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                if (cal.Id == activeId) SetActive(ScheduleStore.Calendars.FirstOrDefault(c => c.Id != cal.Id));
                ScheduleStore.RemoveCal(cal); Changed?.Invoke();
            });
            m.Items.Add(del);
            m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
            m.Show(screen);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Side);
            using (var sep = new Pen(pal.Line)) g.DrawLine(sep, 0, 0, Width, 0); // 위 달력과 나누는 선
            using (var hf = new Font(NovelStore.UiFontFamily, 9f, FontStyle.Bold))
                TextRenderer.DrawText(g, "내 스케줄", hf, new Rectangle(S(18), S(12), Width - S(36), S(24)), Themes.Blend(pal.Side, pal.Fore, 0.5f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            string ff = NovelStore.UiFontFamily;
            using var nf = new Font(ff, 10.5f);
            var cals = ScheduleStore.Calendars;
            for (int i = 0; i < cals.Count; i++)
            {
                var r = RowRect(i);
                if (cals[i].Id == activeId && cals[i].Visible) { using var ab = new SolidBrush(pal.AccentSoft); using var ap = LibraryPalette.Rounded(r, S(8)); g.FillPath(ab, ap); using var apn = new Pen(pal.Accent, S(1)); g.DrawPath(apn, ap); }
                else if (hot == i) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(r, S(8)); g.FillPath(hb, hp); }
                var sw = SwatchRect(i);
                var col = cals[i].ColorValue;
                using var path = LibraryPalette.Rounded(sw, S(8));
                if (cals[i].Visible) { using var b = new SolidBrush(col); g.FillPath(b, path); }
                else { using var pen = new Pen(col, S(2)); g.DrawPath(pen, path); }
                TextRenderer.DrawText(g, cals[i].Name, nf, new Rectangle(sw.Right + S(12), r.Y, r.Width - sw.Width - S(64), r.Height),
                    cals[i].Visible ? pal.Fore : pal.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                var more = MoreRect(i);
                DrawMore(g, more, Themes.Blend(pal.Side, pal.Fore, 0.6f), hot == i && PointToClient(Cursor.Position) is var cp && more.Contains(cp));
            }
            var pr = PlusRect();
            if (hot == 1000) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(pr, S(8)); g.FillPath(hb, hp); }
            using (var pp = new Pen(hot == 1000 ? pal.Fore : Themes.Blend(pal.Side, pal.Fore, 0.6f), S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float pcx = pr.X + pr.Width / 2f, pcy = pr.Y + pr.Height / 2f, arm = S(6);
                g.DrawLine(pp, pcx - arm, pcy, pcx + arm, pcy);
                g.DrawLine(pp, pcx, pcy - arm, pcx, pcy + arm);
            }        }
    }
}

/// <summary>
/// 스케줄 추가 · 보기 창(작은 팝업): [스케줄 ········ ×] / [색 네모][제목] / ─ / [시계] 시작 → 끝 (길이) / 날짜 / ─ / [핀] 위치 / ─ / 설명.
/// 새로 만들 때와 기존 일정을 눌렀을 때 같은 모양으로 뜬다(기존 일정이면 아래에 완료 체크와 삭제가 붙는다).
/// 빈 곳을 끌면 창을 옮길 수 있다. 모든 위치는 화면 배율(DeviceDpi)에 맞춰 직접 계산한다(창이 뜬 뒤 배율이 정해져도 어긋나지 않게 핸들이 만들어질 때 배치).
/// </summary>
internal sealed class ScheduleEntryDialog : Form
{
    private readonly TextBox input, place, note;
    private readonly DateTimePicker datePick, startPick, endPick;
    private readonly CheckBox allDay, doneBox;
    private readonly Label durLabel;
    private readonly Button close, ok, del;
    private readonly LibraryPalette p;
    private readonly bool editing;
    private string calId;
    private Rectangle swatchRect;

    public string EntryText => input.Text;
    public string CalId => calId;
    public DateTime EntryDate => datePick.Value.Date;
    /// <summary>시작 시각(0시부터 분). 시간 없이면 -1.</summary>
    public int StartMin => allDay.Checked ? -1 : startPick.Value.Hour * 60 + startPick.Value.Minute;
    public int DurMin => allDay.Checked ? 0 : Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - StartMin);
    public string Place => place.Text.Trim();
    public string Note => note.Text.Trim();
    public bool Done => doneBox.Checked;
    public bool Deleted { get; private set; }

    private const int W = 344;                 // 96dpi 기준 폭
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ScheduleEntryDialog(LibraryPalette pal, string text, string cal, DateTime date, int startMin, int endMin, string placeText = "", string noteText = "", bool done = false, bool editing = false)
    {
        p = pal;
        calId = cal;
        this.editing = editing;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        Color bg = p.Dark ? p.Main : Color.White;
        BackColor = bg; ForeColor = p.Fore;
        Color field = p.Dark ? Themes.Blend(p.Main, Color.White, 0.07f) : Color.FromArgb(0xF1, 0xF0, 0xEE);
        Paint += (_, e) => PaintAll(e.Graphics);
        MouseClick += (_, e) => { if (swatchRect.Contains(e.Location)) PickCalendar(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; } };

        close = new Button { Text = "", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = p.Fore, TabStop = false, Font = new Font(MainForm.IconFontName(), 11f) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        input = new TextBox { Text = text, BackColor = field, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, Font = new Font(NovelStore.UiFontFamily, 11f), PlaceholderText = "제목" };

        int s0 = startMin < 0 ? 9 * 60 : startMin, e0 = startMin < 0 || endMin <= s0 ? s0 + 60 : Math.Min(endMin, 24 * 60 - 1);
        DateTime Base(int min) => new(2000, 1, 1, Math.Clamp(min / 60, 0, 23), Math.Clamp(min % 60, 0, 59), 0);
        startPick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = Base(s0) };
        endPick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = Base(e0) };
        durLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = p.Dim };
        int len = e0 - s0;
        void ShowLen() { int d = Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - (startPick.Value.Hour * 60 + startPick.Value.Minute)); durLabel.Text = d % 60 == 0 ? $"{d / 60}시간" : d < 60 ? $"{d}분" : $"{d / 60}시간 {d % 60}분"; }
        startPick.ValueChanged += (_, _) => { int ns = startPick.Value.Hour * 60 + startPick.Value.Minute; endPick.Value = Base(Math.Min(ns + Math.Max(15, len), 24 * 60 - 1)); ShowLen(); };
        endPick.ValueChanged += (_, _) => { len = Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - (startPick.Value.Hour * 60 + startPick.Value.Minute)); ShowLen(); };
        ShowLen();

        datePick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "M월 d일 (ddd)", Value = date.Date };
        allDay = new CheckBox { Text = "시간 없이", ForeColor = p.Fore, Checked = startMin < 0, AutoSize = false };
        allDay.CheckedChanged += (_, _) => { startPick.Enabled = endPick.Enabled = !allDay.Checked; };
        startPick.Enabled = endPick.Enabled = !allDay.Checked;

        place = new TextBox { Text = placeText, BorderStyle = BorderStyle.None, BackColor = bg, ForeColor = p.Fore, PlaceholderText = "위치" };
        note = new TextBox { Multiline = true, Text = noteText, BorderStyle = BorderStyle.None, BackColor = field, ForeColor = p.Fore, PlaceholderText = "스케줄에 대한 메모", ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

        doneBox = new CheckBox { Text = "", ForeColor = p.Fore, Checked = done, Visible = editing, AutoSize = false };
        ok = new Button { Text = "저장", FlatStyle = FlatStyle.Flat, BackColor = p.Accent, ForeColor = p.AccentFore };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => { if (input.Text.Trim().Length == 0) { input.Focus(); return; } DialogResult = DialogResult.OK; };
        del = new Button { Text = "삭제", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Color.FromArgb(0xE0, 0x2B, 0x2B), Visible = editing };
        del.FlatAppearance.BorderColor = p.Line;
        del.Click += (_, _) => { Deleted = true; DialogResult = DialogResult.OK; };

        Controls.AddRange(new Control[] { close, input, startPick, endPick, durLabel, datePick, allDay, place, note, doneBox, ok, del });
        AcceptButton = ok;
        Load += (_, _) => Relayout(); Shown += (_, _) => { Relayout(); input.Focus(); input.SelectAll(); };
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    /// <summary>모든 칸의 자리와 크기를 지금 배율로 다시 정한다.</summary>
    private void Relayout()
    {
        Size = new Size(S(W), S(editing ? 416 : 380));
        close.SetBounds(S(W) - S(44), S(8), S(34), S(34));
        int ix = editing ? 90 : 58; // 완료 체크가 색 네모 왼쪽에 들어가는 만큼(기존 일정일 때)
        input.SetBounds(S(ix), S(56), S(W) - S(ix) - S(16), S(32));
        int row = S(112), h = S(30);
        startPick.SetBounds(S(58), row, S(90), h);
        endPick.SetBounds(S(176), row, S(90), h);
        durLabel.SetBounds(S(274), row, S(W) - S(274) - S(8), h);
        datePick.SetBounds(S(58), S(150), S(170), h);
        allDay.SetBounds(S(236), S(150), S(W) - S(236) - S(8), h);
        place.SetBounds(S(58), S(204), S(W) - S(74), S(24));
        note.SetBounds(S(16), S(270), S(W) - S(32), S(96));
        int by = S(382);
        doneBox.SetBounds(S(14), S(58), S(28), S(28)); // 완료 체크: 색 네모 왼쪽
        ok.SetBounds(S(W) - S(16) - S(90), by, S(90), S(32));
        del.SetBounds(S(16), by, S(90), S(32));
        Size = new Size(S(W), ok.Bottom + S(14)); // 삭제 · 저장 아래 여백은 14만
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>창의 빈 곳(제목 줄 · 여백)을 마우스로 끌면 창이 따라 움직인다.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || swatchRect.Contains(e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }

    private void PickCalendar()
    {
        var m = new ContextMenuStrip();
        foreach (var c in ScheduleStore.Calendars)
        {
            var item = new ToolStripMenuItem(c.Name) { Checked = c.Id == calId };
            var cc = c;
            item.Click += (_, _) => { calId = cc.Id; Invalidate(); };
            m.Items.Add(item);
        }
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        m.Show(this, new Point(swatchRect.Left, swatchRect.Bottom + S(4)));
    }

    private void PaintAll(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(p.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var tf = new Font(NovelStore.UiFontFamily, 15f, FontStyle.Bold))
            TextRenderer.DrawText(g, "스케줄", tf, new Rectangle(S(16), S(8), S(200), S(34)), p.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        swatchRect = new Rectangle(S(editing ? 46 : 16), S(56), S(34), S(32));
        using (var path = LibraryPalette.Rounded(swatchRect, S(8)))
        using (var b = new SolidBrush(ScheduleStore.CalOf(calId).ColorValue))
            g.FillPath(b, path);
        using var line = new Pen(p.Line);
        g.DrawLine(line, 0, S(100), Width, S(100));
        g.DrawLine(line, 0, S(192), Width, S(192));
        g.DrawLine(line, 0, S(244), Width, S(244));
        using var icon = new Font(MainForm.IconFontName(), 11f);
        const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(16), S(112), S(34), S(30)), p.Dim, mid);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(148), S(112), S(28), S(30)), p.Dim, mid);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(16), S(202), S(34), S(28)), p.Dim, mid);
        TextRenderer.DrawText(g, "설명", Font, new Rectangle(S(16), S(250), S(100), S(18)), p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 새 스케줄(색 묶음) 만들기 · 수정 창: [스케줄 ········ ×] / [색 네모][이름] / ─ / 설명. 시간 · 위치 · 완료는 없다.
/// 색 네모를 누르면 색을 고르고, 빈 곳을 끌면 창을 옮길 수 있다.
/// </summary>
internal sealed class ScheduleDefDialog : Form
{
    private readonly TextBox input, note;
    private readonly Button close, ok;
    private readonly LibraryPalette p;
    private Color color;
    private Rectangle swatchRect;

    public string DefName => input.Text.Trim();
    public Color DefColor => color;
    public string DefNote => note.Text.Trim();

    private const int W = 336;
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ScheduleDefDialog(LibraryPalette pal, string name, Color col, string noteText)
    {
        p = pal;
        color = col;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        Color bg = p.Dark ? p.Main : Color.White;
        BackColor = bg; ForeColor = p.Fore;
        Color field = p.Dark ? Themes.Blend(p.Main, Color.White, 0.07f) : Color.FromArgb(0xF1, 0xF0, 0xEE);
        Paint += (_, e) => PaintAll(e.Graphics);
        MouseClick += (_, e) => { if (swatchRect.Contains(e.Location)) PickColor(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; } };

        close = new Button { Text = "", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = p.Fore, TabStop = false, Font = new Font(MainForm.IconFontName(), 11f) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        input = new TextBox { Text = name, BackColor = field, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, Font = new Font(NovelStore.UiFontFamily, 11f), PlaceholderText = "스케줄 이름" };
        note = new TextBox { Multiline = true, Text = noteText, BorderStyle = BorderStyle.None, BackColor = bg, ForeColor = p.Fore, PlaceholderText = "스케줄에 대한 메모", ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        ok = new Button { Text = "저장", FlatStyle = FlatStyle.Flat, BackColor = p.Accent, ForeColor = p.AccentFore };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => { if (input.Text.Trim().Length == 0) { input.Focus(); return; } DialogResult = DialogResult.OK; };
        Controls.AddRange(new Control[] { close, input, note, ok });
        AcceptButton = ok;
        Load += (_, _) => Relayout(); Shown += (_, _) => { Relayout(); input.Focus(); input.SelectAll(); };
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    private void Relayout()
    {
        Size = new Size(S(W), S(280));
        close.SetBounds(S(W) - S(44), S(8), S(34), S(34));
        input.SetBounds(S(58), S(56), S(W) - S(74), S(32));
        note.SetBounds(S(16), S(132), S(W) - S(32), S(90));
        ok.SetBounds(S(W) - S(16) - S(90), S(280) - S(14) - S(32), S(90), S(32));
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || swatchRect.Contains(e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    private void PickColor()
    {
        using var cd = new ColorDialog { FullOpen = true, Color = color };
        if (cd.ShowDialog(this) != DialogResult.OK) return;
        color = cd.Color;
        Invalidate();
    }

    private void PaintAll(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(p.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var tf = new Font(NovelStore.UiFontFamily, 15f, FontStyle.Bold))
            TextRenderer.DrawText(g, "스케줄", tf, new Rectangle(S(16), S(8), S(200), S(34)), p.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        swatchRect = new Rectangle(S(16), S(56), S(34), S(32));
        using (var path = LibraryPalette.Rounded(swatchRect, S(8)))
        using (var b = new SolidBrush(color))
            g.FillPath(b, path);
        using var line = new Pen(p.Line);
        g.DrawLine(line, 0, S(100), Width, S(100));
        TextRenderer.DrawText(g, "설명", Font, new Rectangle(S(16), S(108), S(100), S(18)), p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal static class GraphicsRoundedExt
{
    /// <summary>둥근 네모 채우기(지금 브러시 없이 오늘 표시용으로 쓰는 간단한 도우미).</summary>
    public static void FillRoundedRect(this Graphics g, Rectangle r, int radius)
    {
        using var path = LibraryPalette.Rounded(r, radius);
        using var b = new SolidBrush(Color.FromArgb(0xE5, 0x48, 0x4D));
        g.FillPath(b, path);
    }
}
