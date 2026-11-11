using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CoffeeViewer;

/// <summary>집필 일정 한 건.</summary>
internal sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = "";   // yyyy-MM-dd
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>일정 저장소(앱 데이터 폴더의 schedule.json). 볼트와 상관없이 하나를 같이 쓴다.</summary>
internal static class ScheduleStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string FilePath => Path.Combine(AppSettings.DataDir, "schedule.json");
    private static List<ScheduleEntry>? items;

    public static event Action? Changed;

    public static List<ScheduleEntry> All
    {
        get
        {
            if (items != null) return items;
            try { items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ScheduleEntry>>(File.ReadAllText(FilePath), Opts) : null; } catch { }
            items ??= new();
            items.RemoveAll(e => e == null || !TryParse(e.Date, out _));
            return items;
        }
    }

    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    public static bool TryParse(string s, out DateTime d) => DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out d);

    public static List<ScheduleEntry> On(DateTime d) { string k = Key(d); return All.Where(e => e.Date == k).ToList(); }

    public static void Add(DateTime d, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        All.Add(new ScheduleEntry { Date = Key(d), Text = text });
        Save();
    }

    public static void Remove(ScheduleEntry e) { All.Remove(e); Save(); }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>
/// 스케줄러: 왼쪽은 월간 달력(날짜마다 일정 제목이 보인다), 오른쪽은 고른 날짜의 일정 목록(체크 · 추가 · 삭제).
/// </summary>
internal sealed class ScheduleView : Panel
{
    private readonly CalendarGrid grid;
    private readonly Panel side;
    private readonly Label dateLabel;
    private readonly FlowLayoutPanel listPanel;
    private readonly TextBox input;
    private readonly Button addButton;
    private readonly FullScreenButton fullButton = new();
    private LibraryPalette? palette;
    private DateTime selected = DateTime.Today;

    public event Action? FullScreenRequested;
    public void SetFullScreenIcon(bool on) => fullButton.On = on;

    public ScheduleView()
    {
        DoubleBuffered = true;
        string ff = NovelStore.UiFontFamily;
        grid = new CalendarGrid { Dock = DockStyle.Fill };
        grid.DateChosen += d => { selected = d; RefreshSide(); };

        side = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(20, 24, 24, 20) };
        dateLabel = new Label { Dock = DockStyle.Top, Height = 40, AutoSize = false, Font = new Font(ff, 14f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 42 };
        addButton = new Button { Text = "추가", Dock = DockStyle.Right, Width = 64, FlatStyle = FlatStyle.Flat, Font = new Font(ff, 10f), TabStop = false };
        addButton.FlatAppearance.BorderSize = 0;
        addButton.Click += (_, _) => AddEntry();
        input = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = new Font(ff, 10.5f), PlaceholderText = "일정을 적고 Enter" };
        input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddEntry(); e.Handled = e.SuppressKeyPress = true; } };
        inputRow.Controls.Add(input);
        inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        inputRow.Controls.Add(addButton);
        listPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        side.Controls.Add(listPanel);
        side.Controls.Add(inputRow);
        side.Controls.Add(dateLabel);

        Controls.Add(grid);
        Controls.Add(side);
        Controls.Add(fullButton);
        fullButton.Click += (_, _) => FullScreenRequested?.Invoke();
        fullButton.BringToFront();
        Resize += (_, _) => PlaceFull();
        side.Resize += (_, _) => RebuildList();
        ScheduleStore.Changed += OnStoreChanged;
        Disposed += (_, _) => ScheduleStore.Changed -= OnStoreChanged;
        RefreshSide();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private void PlaceFull() => fullButton.SetBounds(ClientSize.Width - S(24) - S(40), S(14), S(40), S(40));

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); PlaceFull(); side.Width = S(340); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); side.Width = S(340); PlaceFull(); }

    /// <summary>스케줄러를 열 때: 오늘 날짜로 맞춘다.</summary>
    public void ShowToday() { selected = DateTime.Today; grid.GoTo(selected); RefreshSide(); }

    private void OnStoreChanged() { if (IsHandleCreated) { grid.Invalidate(); } }

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Main;
        side.BackColor = p.Side;
        dateLabel.ForeColor = p.Fore; dateLabel.BackColor = p.Side;
        listPanel.BackColor = p.Side;
        input.BackColor = p.Dark ? Themes.Blend(p.Main, Color.White, 0.05f) : Color.White; input.ForeColor = p.Fore;
        addButton.BackColor = p.Accent; addButton.ForeColor = p.AccentFore;
        grid.SetPalette(p);
        fullButton.SetPalette(p);
        RebuildList();
    }

    private void AddEntry()
    {
        ScheduleStore.Add(selected, input.Text);
        input.Clear();
        RefreshSide();
        input.Focus();
    }

    private void RefreshSide()
    {
        grid.Selected = selected;
        dateLabel.Text = selected.ToString("M월 d일 (ddd)", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
        RebuildList();
    }

    private void RebuildList()
    {
        if (palette == null) return;
        var p = palette;
        listPanel.SuspendLayout();
        foreach (Control c in listPanel.Controls) c.Dispose();
        listPanel.Controls.Clear();
        var entries = ScheduleStore.On(selected);
        int w = Math.Max(120, listPanel.ClientSize.Width - 8);
        if (entries.Count == 0)
            listPanel.Controls.Add(new Label { Text = "이 날의 일정이 없어요", ForeColor = p.Dim, AutoSize = true, Margin = new Padding(2, 10, 0, 0), BackColor = p.Side });
        foreach (var en in entries)
        {
            var row = new Panel { Width = w, Height = S(34), Margin = new Padding(0, 0, 0, 2), BackColor = p.Side };
            var del = new Button { Text = "✕", Dock = DockStyle.Right, Width = S(30), FlatStyle = FlatStyle.Flat, ForeColor = p.Dim, BackColor = p.Side, TabStop = false };
            del.FlatAppearance.BorderSize = 0;
            var cb = new CheckBox { Text = en.Text, Checked = en.Done, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = en.Done ? p.Dim : p.Fore, BackColor = p.Side };
            if (en.Done) cb.Font = new Font(Font, FontStyle.Strikeout);
            var entry = en;
            cb.CheckedChanged += (_, _) => { entry.Done = cb.Checked; ScheduleStore.Save(); RebuildList(); };
            del.Click += (_, _) => { ScheduleStore.Remove(entry); RefreshSide(); };
            row.Controls.Add(cb);
            row.Controls.Add(del);
            listPanel.Controls.Add(row);
        }
        listPanel.ResumeLayout();
    }

    /// <summary>월간 달력 칸: 날짜 숫자와 일정 제목(두 줄까지, 넘치면 +N).</summary>
    private sealed class CalendarGrid : Control
    {
        private LibraryPalette? p;
        private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime selectedDay = DateTime.Today;
        private Rectangle prevRect, nextRect, todayRect;
        private int hover = -1; // 0 이전 · 1 오늘 · 2 다음 · 100+ 날짜 칸
        private readonly Rectangle[] cells = new Rectangle[42];
        public event Action<DateTime>? DateChosen;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Selected { get => selectedDay; set { selectedDay = value; Invalidate(); } }

        public CalendarGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }
        public void GoTo(DateTime d) { month = new DateTime(d.Year, d.Month, 1); Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        private int Top0 => S(86);          // 요일 줄 위
        private int WeekH => S(30);
        private DateTime FirstCell => month.AddDays(-(int)month.DayOfWeek);

        private void Layout6()
        {
            int pad = S(32);
            int gx = pad, gy = Top0 + WeekH, gw = Math.Max(S(300), Width - pad * 2), gh = Math.Max(S(200), Height - gy - S(24));
            int cw = gw / 7, ch = gh / 6;
            for (int i = 0; i < 42; i++) cells[i] = new Rectangle(gx + (i % 7) * cw, gy + (i / 7) * ch, cw, ch);
            int bs = S(36);
            nextRect = new Rectangle(Width - S(110) - bs, S(18), bs, bs); // 전체 화면 버튼 왼쪽
            todayRect = new Rectangle(nextRect.X - S(8) - S(64), S(18), S(64), bs);
            prevRect = new Rectangle(todayRect.X - S(8) - bs, S(18), bs, bs);
        }

        private int HitAt(Point pt)
        {
            Layout6();
            if (prevRect.Contains(pt)) return 0;
            if (todayRect.Contains(pt)) return 1;
            if (nextRect.Contains(pt)) return 2;
            for (int i = 0; i < 42; i++) if (cells[i].Contains(pt)) return 100 + i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h == 0) { month = month.AddMonths(-1); Invalidate(); }
            else if (h == 2) { month = month.AddMonths(1); Invalidate(); }
            else if (h == 1) { month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); selectedDay = DateTime.Today; DateChosen?.Invoke(selectedDay); Invalidate(); }
            else if (h >= 100)
            {
                var d = FirstCell.AddDays(h - 100);
                selectedDay = d;
                if (d.Month != month.Month) month = new DateTime(d.Year, d.Month, 1);
                DateChosen?.Invoke(d);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Layout6();
            string ff = NovelStore.UiFontFamily;
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            using (var tf = new Font(ff, 18f, FontStyle.Bold))
                TextRenderer.DrawText(g, $"스케줄러  ·  {month.Year}년 {month.Month}월", tf, new Rectangle(S(32), S(14), Width - S(300), S(44)), pal.Fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            using (var bf = new Font(ff, 10f))
            using (var icon = new Font(MainForm.IconFontName(), 10f))
            {
                void Btn(Rectangle r, int idx, string text, bool isIcon)
                {
                    using var path = LibraryPalette.Rounded(r, S(8));
                    using var fill = new SolidBrush(hover == idx ? pal.CardHover : pal.Card);
                    g.FillPath(fill, path);
                    TextRenderer.DrawText(g, text, isIcon ? icon : bf, r, pal.Fore, mid);
                }
                Btn(prevRect, 0, "", true);
                Btn(todayRect, 1, "오늘", false);
                Btn(nextRect, 2, "", true);
            }

            // 요일 줄
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 9.5f, FontStyle.Bold))
                for (int c = 0; c < 7; c++)
                {
                    var r = new Rectangle(cells[c].X, Top0, cells[c].Width, WeekH);
                    Color col = c == 0 ? Color.FromArgb(0xD0, 0x50, 0x50) : c == 6 ? Color.FromArgb(0x50, 0x80, 0xD0) : pal.Dim;
                    TextRenderer.DrawText(g, days[c], wf, r, col, mid);
                }

            var first = FirstCell;
            var byDay = ScheduleStore.All.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToList());
            using var dayFont = new Font(ff, 10.5f, FontStyle.Bold);
            using var itemFont = new Font(ff, 8.5f);
            using var linePen = new Pen(pal.Line);
            for (int i = 0; i < 42; i++)
            {
                var d = first.AddDays(i);
                var r = cells[i];
                bool inMonth = d.Month == month.Month;
                bool isToday = d.Date == DateTime.Today;
                bool isSel = d.Date == selectedDay.Date;
                var inner = Rectangle.Inflate(r, -S(2), -S(2));
                using (var path = LibraryPalette.Rounded(inner, S(8)))
                {
                    if (isSel) { using var b = new SolidBrush(pal.AccentSoft); g.FillPath(b, path); }
                    else if (hover == 100 + i) { using var b = new SolidBrush(pal.CardHover); g.FillPath(b, path); }
                    else if (inMonth) { using var b = new SolidBrush(pal.Card); g.FillPath(b, path); }
                    if (isSel) { using var pen = new Pen(pal.Accent, S(2)); g.DrawPath(pen, path); }
                }
                // 날짜 숫자(오늘은 강조색 동그라미)
                var num = new Rectangle(inner.X + S(8), inner.Y + S(6), S(26), S(26));
                Color nc = !inMonth ? Themes.Blend(pal.Main, pal.Fore, 0.3f) : d.DayOfWeek == DayOfWeek.Sunday ? Color.FromArgb(0xD0, 0x50, 0x50) : d.DayOfWeek == DayOfWeek.Saturday ? Color.FromArgb(0x50, 0x80, 0xD0) : pal.Fore;
                if (isToday)
                {
                    using var b = new SolidBrush(pal.Accent);
                    g.FillEllipse(b, num);
                    nc = pal.AccentFore;
                }
                TextRenderer.DrawText(g, d.Day.ToString(), dayFont, num, nc, mid);

                if (!byDay.TryGetValue(ScheduleStore.Key(d), out var list)) continue;
                int ty = num.Bottom + S(2);
                int lineH = itemFont.Height + S(2);
                int room = (inner.Bottom - ty - S(4)) / lineH;
                int show = Math.Min(list.Count, Math.Max(0, room - (list.Count > room ? 1 : 0)));
                for (int k = 0; k < show; k++)
                {
                    var item = list[k];
                    var tr = new Rectangle(inner.X + S(8), ty + k * lineH, inner.Width - S(14), lineH);
                    using var dot = new SolidBrush(item.Done ? pal.Dim : pal.Accent);
                    g.FillEllipse(dot, tr.X, tr.Y + (lineH - S(5)) / 2, S(5), S(5));
                    using var f = new Font(itemFont, item.Done ? FontStyle.Strikeout : FontStyle.Regular);
                    TextRenderer.DrawText(g, item.Text, f, new Rectangle(tr.X + S(10), tr.Y, tr.Width - S(10), tr.Height), item.Done ? pal.Dim : pal.Fore,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
                if (list.Count > show && room > 0)
                    TextRenderer.DrawText(g, $"+{list.Count - show}개 더", itemFont, new Rectangle(inner.X + S(8), ty + show * lineH, inner.Width - S(14), lineH), pal.Dim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}
