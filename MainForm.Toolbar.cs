namespace CoffeeViewer;

/// <summary>
/// 메모장식 메뉴바(파일 / 편집 / 보기 / 서식 …) 대신 쓰는 얇은 아이콘 툴바.
/// 따로 ToolStrip을 만들지 않고 기존 MenuStrip(MainMenuStrip)의 맨 위 항목만 바꾼다 — 몰입 모드에서 숨기고
/// 맨 위에 마우스를 대면 다시 띄우는 처리(ImmersionHoverTick)와 테마 색칠이 그대로 통하게 하려는 것.
/// 원래의 메뉴들은 왼쪽 ☰ 안으로 그대로 옮겨서, 수정하기 · 저장 같은 드문 기능은 거기서 쓴다.
/// </summary>
public partial class MainForm
{
    // Segoe MDL2 Assets(윈도우 10) · Segoe Fluent Icons(윈도우 11)는 같은 코드 자리에 같은 아이콘이 있다.
    internal const string GlyphMenu = "", GlyphLibrary = "", GlyphOpen = "",
        GlyphPrev = "", GlyphNext = "", GlyphToc = "", GlyphLook = "",
        GlyphPlay = "", GlyphPause = "", GlyphFullScreen = "", GlyphPin = "";

    private Font? toolbarIconFont;
    private ToolStripMenuItem? toolbarMore; // ☰ — 서재 창(머리 막대)의 메뉴 버튼이 이 드롭다운을 연다
    private ToolStripMenuItem? toolbarPlay;
    private ToolStripMenuItem? toolbarToc;
    private ToolStripMenuItem? toolbarViewMode, toolbarViewLayout; // epub · 만화 읽기 전용 서식창의 [보기 모드] [보기 방식]
    private ToolStripMenuItem? toolbarLook; // 글자 크기 표시(ZoomBubble)가 이 버튼 밑에 뜬다
    private ReaderTitle? readerTitle;
    private ToolStripControlHost? readerTitleHost;
    private bool fittingReaderTitle;

    internal static string IconFontName()
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    /// <summary>BuildFeatureMenus까지 다 붙인 뒤에 부른다. 맨 위 메뉴들을 ☰ 안으로 옮기고 아이콘 버튼을 단다.</summary>
    private void BuildToolbar(MenuStrip menu, ToolStripMenuItem alwaysOnTopItem)
    {
        toolbarIconFont = new Font(IconFontName(), 11f);
        FormClosed += (_, _) => toolbarIconFont.Dispose();
        menu.ShowItemToolTips = true;
        menu.Padding = new Padding(4, 2, 4, 2);

        // ☰ — 원래 메뉴(파일 · 편집 · 보기 · 서식 · 자동 재생 · 도움말)를 통째로 담는다.
        var more = toolbarMore = IconItem(GlyphMenu, "메뉴", null);
        more.DropDown.Font = menu.Font;
        foreach (var top in menu.Items.OfType<ToolStripMenuItem>().Where(i => i != alwaysOnTopItem).ToList())
        {
            menu.Items.Remove(top);
            more.DropDownItems.Add(top);
        }
        AddToolsMenu();

        // 서재 버튼 — 다른 버튼과 같은 아이콘 글꼴의 각진 서재(책꽂이) 아이콘. 서재 안에서 읽을 때도, 따로 뜬 창에서도 같은 버튼이다.
        var library = IconItem(GlyphLibrary, "볼트 열기 (Ctrl+L)", ShowLibrary);
        var open = IconItem(GlyphOpen, "파일 열기 (Ctrl+O)", OpenFileDialog);
        // ✎ 수정 — ☰ > 편집 > 수정하기와 같은 버튼(txt · epub은 수정 모드, 한글 문서는 한컴 한글로). 메뉴 항목의 글자 · 쓸 수 있는지를 그대로 따라간다.
        var edit = IconItem("", "수정하기", () => editModeMenuItem.PerformClick());
        void SyncEdit()
        {
            edit.ToolTipText = editModeMenuItem.Text;
            edit.Enabled = editModeMenuItem.Enabled;
            edit.Checked = editModeMenuItem.Text == "읽기 전용으로"; // 고치는 중이면 눌린 모양
        }
        editModeMenuItem.TextChanged += (_, _) => SyncEdit();
        editModeMenuItem.EnabledChanged += (_, _) => SyncEdit();
        SyncEdit();
        // 책 이름(굵게) + 그 밑 작은 정보 줄(크기 · 형식 · 진행률 · 읽던 위치 자동 저장). 몰입 모드처럼 제목표시줄이 없을 때도 지금 책이 보이게.
        readerTitle = new ReaderTitle();
        readerTitleHost = new ToolStripControlHost(readerTitle) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        TextChanged += (_, _) => UpdateReaderTitle();
        positionTimer.Tick += (_, _) => UpdateReaderTitle(); // 진행률이 읽는 동안 바뀐다

        // 오른쪽 묶음 — 오른쪽 정렬 항목은 먼저 넣은 것이 맨 오른쪽에 온다.
        alwaysOnTopItem.Text = GlyphPin;
        alwaysOnTopItem.Font = new Font(toolbarIconFont.FontFamily, toolbarIconFont.Size + 2.5f);
        alwaysOnTopItem.ToolTipText = "항상 위";
        alwaysOnTopItem.AutoToolTip = false;
        UpdatePinLook(alwaysOnTopItem);
        var fullScreen = IconItem(GlyphFullScreen, "몰입 모드 (F11)", ToggleImmersion);
        toolbarPlay = IconItem(GlyphPlay, "낭독 · 자동 재생 설정 (바로 시작/정지: F5)", ShowPlayPopup);
        var look = toolbarLook = IconItem(GlyphLook, "보기 설정 (글자 · 줄 간격 · 읽기 방식 · 테마)", null);
        look.DropDown.Font = menu.Font;
        BuildLookMenu(look);
        // ⚙ 설정 — 툴바 맨 오른쪽(오른쪽 정렬 항목은 컬렉션에서 앞에 있을수록 오른쪽이라 "항상 위" 앞에 끼운다).
        var settingsItem = IconItem("", "설정 (Ctrl+,)", ShowSettings);
        settingsItem.Alignment = ToolStripItemAlignment.Right;
        menu.Items.Insert(menu.Items.IndexOf(alwaysOnTopItem), settingsItem);
        // 목차 아이콘 = 네비게이션 바(파일 · 챕터 목록 · 위치 막대) 열기/닫기. 챕터 목록은 네비게이션 바의 ☰ 버튼으로 연다.
        toolbarToc = IconItem(GlyphToc, "네비게이션 바 열기/닫기", () => navMenuItem.PerformClick());

        // 책 이름(가운데) ······ [☰] [✎] [열기] [서재] | [네비게이션] [보기 설정] [낭독] [몰입] [항상 위] [설정]
        // 로고 옆 왼쪽 묶음(설정에서 정함) | 구분선 | 책 이름 — 가운데에 놓인다.
        var centerSep = new ToolStripSeparator();
        var left = new ToolStripItem[] { centerSep, readerTitleHost };
        for (int i = 0; i < left.Length; i++) { left[i].Alignment = ToolStripItemAlignment.Left; menu.Items.Insert(i, left[i]); }
        toolbarItems["sepC"] = centerSep;
        centerSep.Margin = new Padding(LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(4), 0);
        // 오른쪽 묶음의 순서(설정 > 머리 막대)는 ApplyToolbarVisibility가 맞춘다.
        // 설정 > 머리 막대에서 켜고 끄고 순서를 바꾸는 버튼들(키는 ToolbarCatalog와 같다).
        toolbarItems["sep"] = new ToolStripSeparator();
        toolbarItems["sep2"] = new ToolStripSeparator();
        toolbarItems["library"] = library; toolbarItems["menu"] = more; toolbarItems["edit"] = edit; toolbarItems["open"] = open;
        toolbarItems["title"] = readerTitleHost;
        toolbarItems["nav"] = toolbarToc; toolbarItems["look"] = look; toolbarItems["play"] = toolbarPlay;
        toolbarItems["immersion"] = fullScreen; toolbarItems["pin"] = alwaysOnTopItem; toolbarItems["settings"] = settingsItem;
        toolbarItems["vmode"] = toolbarViewMode!; toolbarItems["vlayout"] = toolbarViewLayout!;
        InitMarginBar(); // [여백 상 하 좌 우] 칸 (epub · 만화 읽기 서식창)
        // 구분선(|) 양옆 여백. 왼쪽 | (sepC)는 책 이름 바로 옆에 붙는다.
        foreach (var k in new[] { "sep", "sep2" }) toolbarItems[k].Margin = new Padding(LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(10), 0);
        ApplyToolbarVisibility();
        menu.Resize += (_, _) => FitReaderTitle();
        menu.Move += (_, _) => FitReaderTitle(); // 머리 막대로 옮겨지거나 로고 폭이 바뀔 때
        menu.Layout += (_, _) => FitReaderTitle(); // 버튼 폭이 정해진 뒤(처음 배치 · 글꼴 변경)에도 가운데를 다시 잡는다
    }

    /// <summary>설정 > 머리 막대의 목록(키, 이름). 순서는 설정창에 보이는 순서.</summary>
    internal static readonly (string Key, string Label)[] ToolbarCatalog =
    {
        ("sepC", "구분선 ( | ) — 책 이름 앞"), ("title", "책 이름"),
        ("paper", "편집 용지"), ("sepF1", "구분선 ( | )"), ("hist", "되돌리기 · 다시 실행"), ("sepF2", "구분선 ( | )"), ("lang", "언어"),
        ("font", "글꼴"), ("size", "글자 크기"), ("line", "줄 간격"), ("sepF3", "구분선 ( | )"), ("align", "정렬 (양쪽 · 왼쪽 · 가운데 · 오른쪽)"), ("para", "문단 서식"), ("chars", "글자 서식 (진하게 · 기울임 · 밑줄 · 취소선 · 글자 색)"),
        ("sep", "구분선 1 ( | )"), ("menu", "메뉴 (☰)"), ("sep2", "구분선 2 ( | )"), ("edit", "수정(편집)"), ("open", "파일 열기"), ("library", "볼트 열기"),
        ("nav", "네비게이션 바"), ("look", "보기 설정"), ("play", "낭독 · 자동 재생"),
        ("immersion", "몰입 모드"), ("pin", "항상 위"), ("help", "도움말 (❓)"), ("settings", "설정 (⚙)"),
        ("sidebar", "왼쪽 패널 열기/닫기"), ("sepF4", "구분선 ( | )"), ("fnav", "네비게이션 바"), ("sepF5", "구분선 ( | )"), ("tts", "낭독 · 자동 재생"), ("vmode", "보기 모드"), ("vlayout", "보기 방식"), ("margins", "여백 (상 · 하 · 좌 · 우)"),
    };

    /// <summary>머리 막대 오른쪽 버튼의 기본 순서(왼쪽 → 오른쪽). 도움말 · 설정은 서재 창이 들고 있어 항상 맨 오른쪽 끝에 놓이고, 둘 사이의 순서만 바뀐다.</summary>
    internal static readonly string[] ToolbarRightDefault =
        { "menu", "sep", "immersion", "pin", "sep2", "help", "settings", "sidebar" };

    /// <summary>서식창(탭 밑 서식 줄) 항목의 기본 순서. 설정 > 서식창 · 머리 막대에서 옮기거나 끌 수 있다.</summary>
    internal static readonly string[] ToolbarLeftDefault =
        { "fnav", "sepF4", "paper", "sepF1", "hist", "sepF2", "lang", "font", "size", "line", "sepF3", "align", "para", "chars", "sepF5", "tts" };

    /// <summary>머리 막대 오른쪽 버튼의 순서: 설정에 저장된 순서에 없는 키는 기본 자리 그대로 뒤에 붙인다.</summary>
    internal static List<string> ToolbarOrder(AppSettings s)
    {
        var order = s.ToolbarOrder.Where(k => ToolbarRightDefault.Contains(k) || IsCustomSep(k)).Distinct().ToList();
        order.AddRange(ToolbarRightDefault.Where(k => !order.Contains(k)));
        return order;
    }

    /// <summary>서식창 항목의 순서(왼쪽 → 오른쪽).</summary>
    internal static List<string> ToolbarLeftOrder(AppSettings s)
    {
        var order = s.ToolbarLeft.Where(k => ToolbarLeftDefault.Contains(k) || IsCustomSep(k)).Distinct().ToList();
        order.AddRange(ToolbarLeftDefault.Where(k => !order.Contains(k)));
        return order;
    }

    /// <summary>키에 해당하는 머리 막대 버튼.</summary>
    private List<ToolStripItem> ItemsOf(string key) =>
        toolbarItems.TryGetValue(key, out var one) ? new List<ToolStripItem> { one } : new();

    internal static string ToolbarLabel(string key) =>
        ToolbarCatalog.Where(c => c.Key == key).Select(c => c.Label).FirstOrDefault() ?? (key.StartsWith("sep") ? "구분선 ( | )" : key);

    /// <summary>설정에서 사용자가 직접 추가한 구분선의 키(sepX1, sepX2 …). 서식창 · 머리 막대 · 읽기 서식창 어디에든 넣고 뺄 수 있다.</summary>
    internal static bool IsCustomSep(string key) => key.StartsWith("sepX");

    /// <summary>epub · 만화용 읽기 전용 서식창 항목의 기본 순서.</summary>
    internal static readonly string[] ToolbarReadDefault = { "nav", "sepR1", "edit", "sepR2", "vmode", "vlayout", "margins", "sepR3", "look", "sepR4", "play" };

    internal static List<string> ToolbarReadOrder(AppSettings s)
    {
        var order = s.ToolbarRead.Where(k => ToolbarReadDefault.Contains(k) || IsCustomSep(k)).Distinct().ToList();
        order.AddRange(ToolbarReadDefault.Where(k => !order.Contains(k)));
        return order;
    }

    private readonly Dictionary<string, ToolStripItem> toolbarItems = new();

    /// <summary>설정에서 정한 순서로 오른쪽 묶음을 다시 늘어놓고, 뺀 버튼은 감춘다. 책 이름 칸 위치도 다시 잡는다.</summary>
    private void ApplyToolbarVisibility()
    {
        var menu = MainMenuStrip;
        if (menu != null)
        {
            var items = ToolbarOrder(settings).SelectMany(ItemsOf).ToList();
            menu.SuspendLayout();
            foreach (var it in items) menu.Items.Remove(it);
            // 오른쪽 정렬은 먼저 넣은 것이 더 오른쪽이라, 오른쪽 끝부터 넣는다.
            for (int i = items.Count - 1; i >= 0; i--)
            {
                items[i].Alignment = ToolStripItemAlignment.Right;
                menu.Items.Add(items[i]);
            }
            menu.ResumeLayout(true);
        }
        foreach (var (key, item) in toolbarItems) item.Visible = !settings.ToolbarHidden.Contains(key);
        ApplyLookBarLayout();
        ApplyReadBarLayout();
        // 문서 이름(과 그 앞 구분선)은 쓰지 않는다. 도움말 · 설정 버튼은 서재 창(머리 막대)이 따로 들고 있다.
        // 메뉴 · 몰입 · 항상 위 · 도움말 · 설정은 서재 창이 머리 막대 맨 오른쪽에 늘 띄운다(VaultForm).
        foreach (var k in new[] { "title", "sepC", "settings", "menu", "immersion", "pin", "sep", "sep2", "library", "open" }) toolbarItems[k].Visible = false; // 카페움 오피스에서 안 쓰는 버튼(구분선 · 볼트 열기 · 네비게이션 · 낭독 · 수정 · 파일 열기 · 보기 설정). 머리 막대 구분선은 VaultForm이 따로 들고 있다.
        if (refBarActive) SyncRefBarKind(); // 참고 자료 서식창에 가져다 쓴 버튼(항상 위 · 몰입 · 설정)은 다시 보이게
        FitReaderTitle();
        MainMenuStrip?.PerformLayout();
    }

    // 서재 안에서 읽을 때는 이 툴바를 서재 머리 막대(로고 · 이름 오른쪽)에 얹는다. 그동안 보이기/숨기기(몰입 모드)는 서재가 맡는다.
    internal Action<bool>? ToolbarVisibilityHost;
    private bool toolbarShown = true;

    internal MenuStrip? DetachToolbar()
    {
        var m = MainMenuStrip;
        if (!embedded || m == null) return null;
        Controls.Remove(m);
        return m;
    }

    internal void ReattachToolbar(MenuStrip m)
    {
        ToolbarVisibilityHost = null;
        toolbarShown = true;
        m.Dock = DockStyle.Top;
        m.AutoSize = true;
        Controls.Add(m);
    }

    private void SetToolbarVisible(bool visible)
    {
        toolbarShown = visible;
        if (ToolbarVisibilityHost != null) ToolbarVisibilityHost(visible);
        else if (MainMenuStrip != null) MainMenuStrip.Visible = visible;
    }

    /// <summary>책 이름과 정보 줄을 맞춘다. 정보 줄: 파일 크기 · 형식 · 진행률 · 읽던 위치 자동 저장(서재 기록에 남는 값).</summary>
    private void UpdateReaderTitle()
    {
        if (readerTitle == null) return;
        string title = Text.EndsWith(" — " + AppShared.AppDisplayName) ? Text[..^(" — " + AppShared.AppDisplayName).Length] : "";
        string meta = "";
        if (!string.IsNullOrEmpty(currentPath))
        {
            var rec = library.Find(currentPath);
            long size = rec?.Size ?? 0;
            if (size == 0) try { size = new FileInfo(currentPath).Length; } catch { }
            string kind = Path.GetExtension(currentPath).TrimStart('.').ToUpperInvariant();
            string sizeText = size >= 1L << 20 ? $"{size / (double)(1 << 20):0.#} MB" : $"{Math.Max(1, size / 1024)} KB";
            meta = $"{sizeText} · {kind} · {(int)Math.Round((rec?.Progress ?? 0) * 100)}% · 읽던 위치 자동 저장";
        }
        readerTitle.SetText(title, meta);
        FitReaderTitle();
    }

    /// <summary>
    /// 책 이름을 툴바 가운데에 놓는다. MenuStrip에는 "가운데" 배치가 없어서, 왼쪽 버튼들 끝에서 창 가운데까지를 제목 칸의 왼쪽 여백으로
    /// 채운다. 폭은 글자 길이만큼, 단 왼쪽 · 오른쪽 버튼 사이에 남는 자리를 넘지 않게(넘치면 …로 줄인다). 자리가 모자라면 가운데가
    /// 아니라 왼쪽 버튼들 바로 옆으로 붙는다.
    /// </summary>
    private void FitReaderTitle()
    {
        if (readerTitle == null || readerTitleHost == null || MainMenuStrip == null || fittingReaderTitle) return;
        fittingReaderTitle = true;
        try
        {
            var menu = MainMenuStrip;
            // 가운데 세트 = 책 이름. 그 앞의 왼쪽 정렬 항목(왼쪽 묶음 · 구분선)은 왼쪽에 그대로 붙어 있고,
            // 세트는 첫 항목의 왼쪽 여백으로 창 가운데에 놓는다(MenuStrip에는 "가운데" 배치가 없다).
            var all = menu.Items.Cast<ToolStripItem>().Where(i => i.Available).ToList();
            var lefts = all.Where(i => i.Alignment == ToolStripItemAlignment.Left).ToList();
            var rightItems = all.Where(i => i.Alignment == ToolStripItemAlignment.Right).ToList();
            var set = new List<ToolStripItem>();
            if (lefts.Contains(readerTitleHost)) set.Add(readerTitleHost);
            int leftEnd = menu.Padding.Left + lefts.Where(i => i != readerTitleHost).Sum(i => i.Width + i.Margin.Horizontal);
            int othersW = 0;
            int rightStart = menu.ClientSize.Width - menu.Padding.Right - rightItems.Sum(i => i.Width + i.Margin.Horizontal);
            int gap = LogicalToDeviceUnits(12);
            // 중심은 툴바가 아니라 툴바가 놓인 곳 전체(머리 막대 · 창)의 가운데 — 머리 막대 안에선 로고 때문에 툴바가 오른쪽으로 밀려 있다.
            int center = menu.Parent != null ? menu.Parent.ClientSize.Width / 2 - menu.Left : menu.ClientSize.Width / 2;
            int minWidth = LogicalToDeviceUnits(80);
            bool titleShown = set.Contains(readerTitleHost);
            // 세트 폭은 고정(책 이름 길이 · 왼쪽 묶음 버튼 수에 따라 변하지 않는다). 창이 너무 좁을 때만 남는 자리만큼 줄인다.
            int room = Math.Max(minWidth, rightStart - leftEnd - gap * 2 - othersW);
            int width = Math.Min(LogicalToDeviceUnits(300), room);
            int setW = othersW + (titleShown ? width : 0);
            int x = Math.Clamp(center - setW / 2, leftEnd + gap, Math.Max(leftEnd + gap, rightStart - gap - setW));
            var size = new Size(width, readerTitle.PreferredHeight);
            if (readerTitleHost.Size != size) readerTitleHost.Size = size;
            for (int i = 0; i < set.Count; i++)
            {
                var margin = new Padding(i == 0 ? Math.Max(0, x - leftEnd) : 0, 0, 0, 0);
                if (set[i].Margin != margin) set[i].Margin = margin;
            }
        }
        finally { fittingReaderTitle = false; }
    }

    /// <summary>툴바 가운데의 두 줄 제목: 책 이름(굵게) + 작은 정보 줄. 길면 …로 줄인다.</summary>
    private sealed class ReaderTitle : Control
    {
        private string title = "", meta = "";
        private Color fore = SystemColors.ControlText, dim = SystemColors.GrayText;

        public ReaderTitle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public void SetText(string newTitle, string newMeta)
        {
            if (newTitle == title && newMeta == meta) return;
            title = newTitle;
            meta = newMeta;
            Invalidate();
        }

        public void SetColors(Color back, Color foreColor)
        {
            BackColor = back;
            fore = foreColor;
            dim = Themes.Blend(back, foreColor, 0.6f);
            Invalidate();
        }

        private Font TitleFont() => new(Font.FontFamily, 10f, FontStyle.Bold);
        private Font MetaFont() => new(Font.FontFamily, 8.25f);

        public int PreferredWidth
        {
            get
            {
                using var tf = TitleFont();
                using var mf = MetaFont();
                return Math.Max(TextRenderer.MeasureText(title, tf).Width, TextRenderer.MeasureText(meta, mf).Width) + 4;
            }
        }

        public int PreferredHeight
        {
            get
            {
                using var tf = TitleFont();
                using var mf = MetaFont();
                return tf.Height + mf.Height + 2;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var tf = TitleFont();
            using var mf = MetaFont();
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis; // 툴바 가운데 제목
            int top = Math.Max(0, (Height - tf.Height - mf.Height) / 2);
            TextRenderer.DrawText(e.Graphics, title, tf, new Rectangle(0, top, Width, tf.Height), fore, flags);
            TextRenderer.DrawText(e.Graphics, meta, mf, new Rectangle(0, top + tf.Height, Width, mf.Height), dim, flags);
        }
    }

    /// <summary>항상 위가 켜지면 압정 아이콘을 기울어진 모양(그림)으로, 꺼지면 원래 글자 아이콘으로 바꾼다.</summary>
    private void UpdatePinLook(ToolStripMenuItem item)
    {
        var old = item.Image;
        item.DisplayStyle = ToolStripItemDisplayStyle.Image;
        item.ImageScaling = ToolStripItemImageScaling.None;
        item.Image = PinIcon.Make(theme.ChromeFore, LogicalToDeviceUnits(22), item.Checked); // 켜짐 = 기울어진 압정 · 꺼짐 = 가로 압정, 둘 다 그림이라 크기가 같다
        item.Text = "";
        old?.Dispose();
    }

    private ToolStripMenuItem IconItem(string glyph, string tip, Action? onClick)
    {
        var item = new ToolStripMenuItem(glyph)
        {
            Font = toolbarIconFont,
            ToolTipText = tip,
            AutoToolTip = false,
            Padding = new Padding(6, 1, 6, 1),
        };
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>"가" 버튼: 소설을 읽으며 자주 만지는 것만 모은 보기 설정. 원래 메뉴 항목을 그대로 눌러 주는 대리 항목이라 동작 · 체크 상태가 늘 같다.</summary>
    private void BuildLookMenu(ToolStripMenuItem look)
    {
        // 맨 위 두 묶음 중 하나만 보인다: 글 · 책(txt · epub · pdf)이면 글자 크기 · 줄 간격 · 글꼴, 만화(이미지)면 보기 모드.
        var textGroup = new List<ToolStripItem>();
        var imageGroup = new List<ToolStripItem>();

        // 글자 크기 ▸ 자주 쓰는 크기 + 사용자 지정(8~48pt 직접 입력). Ctrl+휠 · Ctrl+- · Ctrl++ 로도 바꾼다.
        var sizeMenu = new ToolStripMenuItem("글자 크기");
        foreach (int v in new[] { 10, 11, 12, 13, 14, 16, 18, 20, 24, 28 })
        {
            int value = v;
            sizeMenu.DropDownItems.Add(new ToolStripMenuItem(value == DefaultFontSize ? $"{value}pt (기본)" : $"{value}pt", null, (_, _) => ZoomTo(value)) { Tag = value });
        }
        sizeMenu.DropDownItems.Add(new ToolStripSeparator());
        var customSize = new ToolStripMenuItem("사용자 지정…", null, (_, _) => AskCustomFontSize());
        sizeMenu.DropDownItems.Add(customSize);
        sizeMenu.DropDownOpening += (_, _) =>
        {
            int now = (int)Math.Round(fontSize);
            bool preset = false;
            foreach (var item in sizeMenu.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is int t) { item.Checked = t == now; preset |= t == now; }
            customSize.Text = preset ? "사용자 지정…" : $"사용자 지정… (지금 {now}pt)";
            customSize.Checked = !preset;
        };
        look.DropDownItems.Add(sizeMenu);
        textGroup.Add(sizeMenu);

        var lineMenu = new ToolStripMenuItem("줄 간격");
        foreach (int v in new[] { 130, 150, 170, 190, 210, 240 })
        {
            int value = v;
            lineMenu.DropDownItems.Add(new ToolStripMenuItem($"{value}%", null, (_, _) =>
            {
                if (lookRec != null) { SetLook(r => r.LookLine = value); return; }
                settings.LineHeight = value;
                settings.Save();
                AppShared.NotifySettingsChanged("margin");
            }) { Tag = value });
        }
        lineMenu.DropDownOpening += (_, _) =>
        {
            foreach (var item in lineMenu.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Tag is int t && t == EffLine;
        };
        look.DropDownItems.Add(lineMenu);
        textGroup.Add(lineMenu);
        var fontItem = new ToolStripMenuItem("글꼴…", null, (_, _) => ChooseFont());
        look.DropDownItems.Add(fontItem);
        textGroup.Add(fontItem);
        var textSep = new ToolStripSeparator();
        look.DropDownItems.Add(textSep);
        textGroup.Add(textSep);

        // 만화 보기 모드(원본 크기 · 꽉차게 …) — 원래 보기 메뉴의 항목을 대신 눌러 준다.
        var viewModeSub = new ToolStripMenuItem("보기 모드");
        foreach (var src in imageViewModeMenuItems) viewModeSub.DropDownItems.Add(Proxy(src));
        look.DropDownItems.Add(viewModeSub);
        imageGroup.Add(viewModeSub);
        // 만화 보기 방식(한쪽 보기 · 두쪽 보기)
        var layoutSub = new ToolStripMenuItem("보기 방식");
        foreach (var src in imageLayoutMenuItems) layoutSub.DropDownItems.Add(Proxy(src));
        look.DropDownItems.Add(layoutSub);
        imageGroup.Add(layoutSub);
        var imageSep = new ToolStripSeparator();
        look.DropDownItems.Add(imageSep);
        imageGroup.Add(imageSep);

        foreach (var src in new[] { scrollModeMenuItem, pageModeMenuItem, pageTouchModeMenuItem }) look.DropDownItems.Add(Proxy(src));
        look.DropDownItems.Add(Proxy(showImagesMenuItem)); // epub 그림 접기 · 펴기(☰ > 보기 > 이미지 표시와 같은 항목)
        look.DropDownItems.Add(new ToolStripSeparator());

        var themeMenu = new ToolStripMenuItem("테마");
        foreach (var src in themeMenuItems) themeMenu.DropDownItems.Add(Proxy(src));
        look.DropDownItems.Add(themeMenu);
        look.DropDownItems.Add(Proxy(navMenuItem));
        // "설정…"은 툴바 오른쪽 끝의 ⚙ 버튼으로 옮겼다.

        // epub · 만화 읽기 전용 서식창에 따로 놓는 [보기 모드] [보기 방식] 버튼. 열 때마다 지금 종류(만화 / 글)에 맞는 항목으로 채운다.
        //   만화: 보기 모드 = 원본 크기 · 꽉차게 …, 보기 방식 = 한쪽 · 두쪽
        //   epub · 글: 보기 모드 = 스크롤 · 페이지 · 터치 페이지, 보기 방식 = 이미지 표시 · 테마
        void SyncProxies(ToolStripItemCollection items)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>())
            {
                if (item.Tag is ToolStripMenuItem src) { item.Checked = src.Checked; item.Enabled = src.Enabled && (src.OwnerItem?.Enabled ?? true); }
                SyncProxies(item.DropDownItems);
            }
        }
        ToolStripMenuItem DynamicMenu(string text, string tip, Func<IEnumerable<ToolStripItem>> fill)
        {
            var m = new ToolStripMenuItem(text) { Font = toolbarIconFont, ToolTipText = tip, AutoToolTip = false, Padding = new Padding(6, 1, 6, 1) };
            m.DropDownItems.Add(new ToolStripMenuItem("…")); // 항목이 하나는 있어야 열린다(열릴 때 바꿔 채운다)
            m.DropDownOpening += (_, _) =>
            {
                m.DropDownItems.Clear();
                foreach (var it in fill()) m.DropDownItems.Add(it);
                SyncProxies(m.DropDownItems);
            };
            return m;
        }
        toolbarViewMode = DynamicMenu("","보기 모드 (만화: 원본 크기 · 꽉차게 …  /  epub: 스크롤 · 페이지)", () =>
            (imageMode ? imageViewModeMenuItems.AsEnumerable() : new[] { scrollModeMenuItem, pageModeMenuItem, pageTouchModeMenuItem })
                .Select(Proxy).Cast<ToolStripItem>());
        toolbarViewLayout = DynamicMenu("","보기 방식 (만화: 한쪽 · 두쪽  /  epub: 이미지 표시 · 테마)", () =>
        {
            if (imageMode) return imageLayoutMenuItems.Select(Proxy).Cast<ToolStripItem>().ToList();
            var theme = new ToolStripMenuItem("테마");
            foreach (var src in themeMenuItems) theme.DropDownItems.Add(Proxy(src));
            return new List<ToolStripItem> { Proxy(showImagesMenuItem), theme };
        });
        refReadMode = DynamicMenu("", "읽기 방식 (스크롤 · 페이지 · 페이지 터치)", () =>
            new[] { scrollModeMenuItem, pageModeMenuItem, pageTouchModeMenuItem }.Select(Proxy).Cast<ToolStripItem>());

        look.DropDownOpening += (_, _) =>
        {
            foreach (var it in textGroup) it.Visible = !imageMode;
            foreach (var it in imageGroup) it.Visible = imageMode;
            sizeMenu.Enabled = lineMenu.Enabled = CanZoomText;
            viewModeSub.Enabled = viewModeMenu.Enabled;
            foreach (var item in look.DropDownItems.OfType<ToolStripMenuItem>().Concat(themeMenu.DropDownItems.OfType<ToolStripMenuItem>()).Concat(viewModeSub.DropDownItems.OfType<ToolStripMenuItem>()).Concat(layoutSub.DropDownItems.OfType<ToolStripMenuItem>()))
                if (item.Tag is ToolStripMenuItem src) { item.Checked = src.Checked; item.Enabled = src.Enabled && (src.OwnerItem?.Enabled ?? true); }
        };
    }

    /// <summary>원래 메뉴 항목을 대신 눌러 주는 항목(같은 항목을 두 메뉴에 동시에 넣을 수는 없어서).</summary>
    private static ToolStripMenuItem Proxy(ToolStripMenuItem src) =>
        new(src.Text, null, (_, _) => src.PerformClick()) { Tag = src };

    /// <summary>SetContentMode · 자동 재생이 바뀔 때 툴바 버튼 상태를 맞춘다.</summary>
    private void UpdateToolbar()
    {
        if (toolbarPlay != null)
        {
            bool playing = ttsActive || slideshowTimer != null;
            toolbarPlay.Text = playing ? GlyphPause : GlyphPlay;
            toolbarPlay.Enabled = autoPlayToggleMenuItem?.Enabled ?? true;
        }
        if (toolbarToc != null) toolbarToc.Checked = settings.NavBarEnabled; // 열려 있으면 눌린 모양
        if (lookNav != null) lookNav.Checked = settings.NavBarEnabled;
        if (lookTts != null) lookTts.Text = (ttsActive || slideshowTimer != null) ? GlyphPause : GlyphPlay;
    }
}
