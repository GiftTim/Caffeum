namespace CoffeeViewer;

/// <summary>
/// 네비게이션 바(툴바의 목차 아이콘 · 보기 > 네비게이션 바). 반디뷰 아래쪽 막대처럼 읽기 화면 맨 아래에 붙는다:
///   [📁 파일] [☰ 챕터 / 이미지] [◀] [━━━ 위치 막대 ━━━] [▶] [3 / 20] [📌 고정]
/// - 파일: 같은 폴더의 파일 목록 → 고르면 이 창에서 연다.
/// - 챕터 / 이미지 선택: 소설은 챕터, 만화는 이미지 목록 → 고르면 그리로 간다(예전 "챕터 목록" 창을 대신한다).
/// - 위치 막대: 누르거나 끌어서 이동(만화 = 페이지, epub = 챕터, txt = 글 위치).
/// - ◀ ▶: 이전 · 다음 페이지(← → 키와 같다). 이전 · 다음 파일은 위쪽 툴바의 버튼과 [ ] 키.
/// - 고정: 켜면 늘 보이고, 끄면 마우스를 화면 아래쪽에 가져갈 때만 떠오른다(그 판단은 MainForm.Nav.cs).
/// 이 막대는 "무엇을 보여주고 무엇을 할지"를 모른다 — 목록·위치는 MainForm이 채우고, 고른 결과는 이벤트로 돌려준다.
/// </summary>
public sealed class NavigationBar : ToolStrip
{
    public event Action? FileListRequested;
    public event Action? ItemListRequested;
    public event Action? PrevPageRequested;
    public event Action? NextPageRequested;
    /// <summary>위치 막대로 이 값(0 ~ Total-1)으로 가고 싶다.</summary>
    public event Action<int>? SeekRequested;
    public event Action<bool>? PinChanged;

    private readonly ToolStripButton fileButton;
    private readonly ToolStripButton listButton;
    private readonly ToolStripButton prevPageButton;
    private readonly ToolStripButton nextPageButton;
    private readonly SeekBar seek;
    private readonly ToolStripControlHost seekHost;
    private readonly ToolStripLabel positionLabel;
    private readonly ToolStripButton pinButton;
    private readonly ToolTip previewTip = new();
    private Func<int, string>? previewText;
    private ToolStripDropDown? popup;
    private bool popupForFiles;
    // 목록이 떠 있을 때 같은 버튼을 다시 누르면, 버튼을 누르는 순간(마우스 다운) 목록이 먼저 저절로 닫히고 그다음 클릭이 와서
    // 다시 열려 버린다. 방금(짧은 사이에) 같은 목록이 닫혔으면 그 클릭은 "닫기"로 보고 다시 열지 않는다.
    private bool? lastClosedForFiles;
    private long lastClosedTick;
    private readonly Font iconFont;
    private const string GlyphFolder = "", GlyphList = "", GlyphPin = "", GlyphPinned = "";
    private ReaderTheme theme = Themes.All[0];
    private bool layingOut;

    /// <summary>목록 창이 떠 있거나 위치 막대를 끄는 중 — 자동 숨김이 이때 막대를 치우면 안 된다.</summary>
    public bool IsBusy => seek.Dragging || (popup?.Visible ?? false);

    public NavigationBar()
    {
        GripStyle = ToolStripGripStyle.Hidden;
        CanOverflow = false;
        Padding = new Padding(6, 3, 6, 3);
        AutoSize = false;
        Height = 38;
        Stretch = true;

        // 📁 파일 · ☰ 챕터 · 📌 고정은 글자 대신 아이콘 — 툴바와 같은 Segoe MDL2 아이콘 글꼴(컬러 이모지 글꼴은 크기가 제각각이라 잘렸다).
        iconFont = new Font("Segoe MDL2 Assets", 11f);
        Disposed += (_, _) => iconFont.Dispose();
        fileButton = new ToolStripButton(GlyphFolder) { Font = iconFont, ToolTipText = "같은 폴더의 파일", AccessibleName = "파일" };
        fileButton.Click += (_, _) => FileListRequested?.Invoke();
        listButton = new ToolStripButton(GlyphList) { Font = iconFont, ToolTipText = "챕터 목록", AccessibleName = "챕터" };
        listButton.Click += (_, _) => ItemListRequested?.Invoke();
        // ◀ ▶ 글자는 글꼴마다 크기가 달라 버튼 아래가 잘렸다. 그래서 글자 대신 직접 그린 그림을 쓴다(UpdatePageIcons).
        // 그림만 있는 버튼이라 화면 읽기 프로그램이 부를 이름(AccessibleName)을 따로 준다.
        prevPageButton = new ToolStripButton { ToolTipText = "이전 페이지  ( ← )", AccessibleName = "이전 페이지", DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None };
        prevPageButton.Click += (_, _) => PrevPageRequested?.Invoke();
        nextPageButton = new ToolStripButton { ToolTipText = "다음 페이지  ( → )", AccessibleName = "다음 페이지", DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None };
        nextPageButton.Click += (_, _) => NextPageRequested?.Invoke();

        seek = new SeekBar();
        seek.Seek += v => SeekRequested?.Invoke(v);
        // 막대 위에 마우스를 올리면 그 자리의 페이지 · 챕터 이름을 마우스 바로 위 말풍선으로(오른쪽 숫자 칸은 좁아서 긴 챕터 이름이 잘린다).
        seek.Preview += (v, x) =>
        {
            if (v is int i && previewText != null) previewTip.Show(previewText(i), seek, x + 12, -30);
            else previewTip.Hide(seek);
        };
        seekHost = new ToolStripControlHost(seek) { AutoSize = false, Margin = new Padding(6, 0, 6, 0) };

        positionLabel = new ToolStripLabel("") { AutoSize = false, Width = 110, TextAlign = ContentAlignment.MiddleCenter };
        pinButton = new ToolStripButton(GlyphPin) { Font = iconFont, CheckOnClick = true, AccessibleName = "고정",
            ToolTipText = "고정 — 켜면 네비게이션 바가 늘 보이고, 끄면 마우스를 아래쪽에 가져갈 때만 나타납니다" };
        pinButton.CheckedChanged += (_, _) =>
        {
            pinButton.Text = pinButton.Checked ? GlyphPinned : GlyphPin; // 꽂힌 핀 / 빠진 핀
            PinChanged?.Invoke(pinButton.Checked);
        };

        Items.AddRange(new ToolStripItem[]
        {
            fileButton, listButton, new ToolStripSeparator(),
            prevPageButton, seekHost, nextPageButton, positionLabel, new ToolStripSeparator(), pinButton,
        });
        UpdatePageIcons();
    }

    /// <summary>이전 · 다음 페이지 버튼의 그림(◀ / ▶)을 현재 글자색과 화면 배율로 다시 그린다.</summary>
    private void UpdatePageIcons()
    {
        float s = DeviceDpi / 96f;
        var color = ForeColor.IsEmpty || ForeColor == Color.Empty ? SystemColors.ControlText : ForeColor;
        prevPageButton.Image?.Dispose();
        nextPageButton.Image?.Dispose();
        prevPageButton.Image = DrawPageIcon(prev: true, color, s);
        nextPageButton.Image = DrawPageIcon(prev: false, color, s);
    }

    private static Bitmap DrawPageIcon(bool prev, Color color, float scale)
    {
        int size = (int)Math.Round(18 * scale);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.ScaleTransform(scale, scale);
        if (!prev) { g.TranslateTransform(18, 0); g.ScaleTransform(-1, 1); } // 다음 페이지는 이전 페이지를 좌우로 뒤집은 모양
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[] { new PointF(5f, 9f), new PointF(13f, 3.5f), new PointF(13f, 14.5f) });
        return bmp;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned
    {
        get => pinButton.Checked;
        set => pinButton.Checked = value;
    }

    /// <summary>지금 보는 것에 맞춰 버튼 이름·쓸 수 있는지를 정한다.</summary>
    public void Configure(string listLabel, bool listEnabled, bool filesEnabled, bool seekEnabled, bool liveSeek, bool pagesEnabled = true)
    {
        // 버튼은 아이콘(☰)이라 이름은 말풍선 · 화면 읽기 이름으로만.
        listButton.ToolTipText = listLabel == "이미지 선택" ? "이미지 목록" : "챕터 목록";
        listButton.AccessibleName = listLabel;
        listButton.Enabled = listEnabled;
        fileButton.Enabled = filesEnabled;
        prevPageButton.Enabled = nextPageButton.Enabled = pagesEnabled;
        seek.Enabled = seekEnabled;
        seek.LiveSeek = liveSeek;
        if (!seekEnabled) SetPosition(0, 1, "", null);
        StretchSeek();
    }

    /// <summary>위치 막대와 오른쪽 숫자를 갱신한다. preview는 막대 위에 마우스를 올렸을 때 그 자리의 이름(없으면 숫자만).</summary>
    public void SetPosition(int value, int total, string text, Func<int, string>? preview)
    {
        seek.SetRange(value, total);
        previewText = preview;
        positionLabel.Text = text;
    }

    /// <summary>막대 바로 위에 목록 창을 띄운다(파일 · 챕터 · 이미지 공용). 고르면 onPick(번호).</summary>
    public void ShowList(bool forFiles, IReadOnlyList<string> names, int current, Action<int> onPick)
    {
        // 같은 버튼을 다시 누르면 닫기(열려 있던 목록이 방금 그 클릭으로 닫혔거나, 아직 열려 있으면).
        if (popup is { Visible: true } && popupForFiles == forFiles) { popup.Close(); return; }
        if (lastClosedForFiles == forFiles && Environment.TickCount64 - lastClosedTick < 600) { lastClosedForFiles = null; return; }
        popup?.Close();
        popupForFiles = forFiles;
        var list = new ListBox
        {
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            Width = 380,
            Height = 440,
            BackColor = theme.Name == "라이트" ? Color.White : theme.Dark ? Themes.Blend(theme.Chrome, Color.Black, 0.25f) : Themes.Blend(theme.Chrome, Color.White, 0.5f),
            ForeColor = theme.ChromeFore,
            Font = Font,
        };
        list.BeginUpdate();
        foreach (var n in names) list.Items.Add(n);
        list.EndUpdate();
        if (current >= 0 && current < names.Count) list.SelectedIndex = current; // 지금 보는 것을 가운데쯤 보이게
        if (list.SelectedIndex >= 0) list.TopIndex = Math.Max(0, list.SelectedIndex - 8);

        var host = new ToolStripControlHost(list) { AutoSize = false, Size = list.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        var dd = new ToolStripDropDown { Padding = new Padding(1), AutoClose = true };
        dd.Items.Add(host);
        popup = dd;

        void Pick()
        {
            int i = list.SelectedIndex;
            dd.Close();
            if (i >= 0) onPick(i);
        }
        list.MouseClick += (_, e) => { if (list.IndexFromPoint(e.Location) >= 0) Pick(); };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; Pick(); } };
        dd.Closed += (_, _) =>
        {
            lastClosedForFiles = forFiles;
            lastClosedTick = Environment.TickCount64;
            BeginInvoke(() => dd.Dispose());
        };

        var anchor = forFiles ? fileButton : listButton;
        int gap = (int)Math.Round(4 * DeviceDpi / 96.0); // 막대에 바짝 붙지 않게 아주 조금 띄운다
        dd.Show(this, new Point(anchor.Bounds.Left, -gap), ToolStripDropDownDirection.AboveRight);
        MenuGuard.CloseOnOutsideClick(dd); // 목록 밖을 누르면(웹 화면 위여도) 닫는다
        list.Focus();
    }

    public void ApplyTheme(ReaderTheme t)
    {
        theme = t;
        BackColor = t.Chrome;
        ForeColor = t.ChromeFore;
        seek.BackColor = t.Chrome;
        seek.TrackColor = Themes.Blend(t.Chrome, t.ChromeFore, t.Dark ? 0.3f : 0.2f);
        seek.FillColor = t.Link;
        seek.ThumbColor = t.Dark ? t.ChromeFore : t.Link;
        seek.Invalidate();
        UpdatePageIcons();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        StretchSeek();
    }

    /// <summary>위치 막대가 남는 폭을 다 차지하게(ToolStrip은 한 항목을 늘려주는 기능이 없어서 직접 잰다).</summary>
    private void StretchSeek()
    {
        if (layingOut || seekHost is null) return; // 생성자에서 Height를 정할 때 벌써 불린다(아직 항목을 안 만들었을 때)
        layingOut = true;
        try
        {
            int others = 0;
            foreach (ToolStripItem item in Items)
                if (item != seekHost && item.Available) others += item.Width + item.Margin.Horizontal;
            int w = DisplayRectangle.Width - others - seekHost.Margin.Horizontal - 4;
            seekHost.Width = Math.Max(60, w);
            seekHost.Height = Math.Max(16, DisplayRectangle.Height - 4);
        }
        finally { layingOut = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { popup?.Dispose(); previewTip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>탐색 막대 안의 위치 막대. 누르거나 끌면 그 자리 값으로 간다 — LiveSeek이면 끄는 동안 계속,
    /// 아니면(epub 챕터 · txt처럼 옮길 때마다 다시 그리는 게 무거운 것) 손을 뗄 때 한 번만.</summary>
    private sealed class SeekBar : Control
    {
        public event Action<int>? Seek;
        /// <summary>마우스가 가리키는 자리와 그 x 좌표(자리가 null이면 막대를 벗어남) — 말풍선으로 미리 보여주려고.</summary>
        public event Action<int?, int>? Preview;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color TrackColor { get; set; } = Color.Silver;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color FillColor { get; set; } = Color.DodgerBlue;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color ThumbColor { get; set; } = Color.DodgerBlue;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool LiveSeek { get; set; } = true;
        public bool Dragging { get; private set; }

        private int lastPreview = -1;
        private int total = 1;
        private int value;
        private int dragValue = -1;

        public SeekBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public void SetRange(int current, int count)
        {
            total = Math.Max(1, count);
            value = Math.Clamp(current, 0, total - 1);
            if (!Dragging) dragValue = -1;
            Invalidate();
        }

        private int Pad => Height / 2;

        private int ValueAt(int x)
        {
            int usable = Math.Max(1, Width - Pad * 2);
            float r = Math.Clamp((x - Pad) / (float)usable, 0f, 1f);
            return total <= 1 ? 0 : (int)Math.Round(r * (total - 1));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Dragging = true;
            Capture = true;
            MoveTo(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Enabled) return;
            int v = ValueAt(e.X);
            if (v != lastPreview) { lastPreview = v; Preview?.Invoke(v, e.X); } // 같은 자리에서 말풍선이 깜빡이지 않게
            if (Dragging) MoveTo(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Dragging) return;
            Dragging = false;
            Capture = false;
            if (!LiveSeek && dragValue >= 0 && dragValue != value) Seek?.Invoke(dragValue);
            dragValue = -1;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            lastPreview = -1;
            if (!Dragging) Preview?.Invoke(null, 0);
        }

        private void MoveTo(int x)
        {
            int v = ValueAt(x);
            if (LiveSeek) { if (v != value) Seek?.Invoke(v); }
            else { dragValue = v; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int shown = dragValue >= 0 ? dragValue : value;
            float ratio = total <= 1 ? 0f : shown / (float)(total - 1);
            int trackH = Math.Max(4, Height / 6);
            int y = Height / 2 - trackH / 2;
            int left = Pad, right = Width - Pad;
            int thumbX = left + (int)((right - left) * ratio);
            using (var b = new SolidBrush(Enabled ? TrackColor : Themes.Blend(TrackColor, BackColor, 0.5f)))
                g.FillRectangle(b, left, y, right - left, trackH);
            if (!Enabled) return;
            using (var b = new SolidBrush(FillColor))
                g.FillRectangle(b, left, y, thumbX - left, trackH);
            int r = Math.Max(6, Height / 3);
            using (var b = new SolidBrush(ThumbColor))
                g.FillEllipse(b, thumbX - r, Height / 2 - r, r * 2, r * 2);
        }
    }
}
