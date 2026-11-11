using System.Drawing.Drawing2D;
using Microsoft.VisualBasic.FileIO;

namespace CoffeeViewer;

/// <summary>
/// 첫 화면(Muvel의 "로컬 도서관"식 작품 목록).
/// 위: 은은한 그라데이션 띠 가운데에 [서재 아이콘] 내 서재 · N작품 / 한 줄 소개 / [검색칸] [+ 새 작품] [볼트 바꾸기].
/// 아래: 책 표지 모양(세로로 긴 네모) 옆에 제목 · 편수 · 글자 수 · 소개 · 고친 때가 붙은 작품들이 가운데 정렬로 줄지어 놓인다.
/// 누르면 그 작품으로 들어간다.
/// </summary>
internal sealed class NovelHome : Panel
{
    private readonly string vaultPath;
    private readonly HomeHeader top;
    private readonly Label titleLabel, countLabel, subLabel;
    private readonly Panel searchBox;
    private readonly TextBox searchText;
    private readonly PillButton newButton, vaultButton;
    private readonly FlowLayoutPanel flow;
    private readonly Label emptyLabel;
    private LibraryPalette? palette;
    private int loadGen;
    private int novelCount;

    public event Action<string>? NovelOpened;
    /// <summary>작품 폴더 이름을 바꾸거나 지우기 직전(그 안의 회차 탭을 닫으라고).</summary>
    public event Action<string>? PathAboutToChange;
    public event Action? ChangeVaultRequested;
    public event Action? SettingsRequested;
    public event Action? HelpRequested;
    /// <summary>오른쪽 위의 전체 화면 버튼.</summary>
    public event Action? FullScreenRequested;
    private readonly FullScreenButton fullButton = new();
    public void SetFullScreenIcon(bool on) => fullButton.On = on;

    // Muvel처럼 왼쪽 옆 막대: 맨 위 로고(접기 버튼) · 메뉴 · 맨 아래 볼트 이름
    private readonly Panel nav;
    private readonly SidebarBrand brand;
    private readonly SidebarNavItem libraryItem, vaultItem, settingsItem, helpItem, updateItem;
    private readonly Label vaultLabel;
    private readonly Panel main;
    private bool navCollapsed;

    public NovelHome(string vaultPath)
    {
        this.vaultPath = vaultPath;
        DoubleBuffered = true;
        string ff = NovelStore.UiFontFamily;

        titleLabel = new Label { Text = "내 서재", AutoSize = true, Font = new Font(ff, 20f, FontStyle.Bold), UseMnemonic = false, BackColor = Color.Transparent };
        countLabel = new Label { AutoSize = true, Font = new Font(ff, 9.5f), UseMnemonic = false, BackColor = Color.Transparent };
        subLabel = new Label { AutoSize = true, Font = new Font(ff, 10f), UseMnemonic = false, BackColor = Color.Transparent, Text = "이 폴더의 작품들은 모두 내 컴퓨터에 txt로 저장돼요" };

        searchText = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "작품 검색…", Font = new Font(ff, 10.5f) };
        searchText.TextChanged += (_, _) => ApplyFilter();
        searchBox = new Panel { Cursor = Cursors.IBeam };
        searchBox.Controls.Add(searchText);
        searchBox.Click += (_, _) => searchText.Focus();
        searchBox.Paint += PaintSearchBox;
        searchBox.Resize += (_, _) => { PlaceSearchText(); searchBox.Invalidate(); };
        searchText.GotFocus += (_, _) => searchBox.Invalidate();
        searchText.LostFocus += (_, _) => searchBox.Invalidate();

        newButton = new PillButton("+  새 작품") { Primary = true, Font = new Font(ff, 10f, FontStyle.Bold) };
        newButton.Click += (_, _) => CreateNovel();
        vaultButton = new PillButton("폴더 열기") { Font = new Font(ff, 10f) };
        vaultButton.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{vaultPath}\"");

        top = new HomeHeader { Dock = DockStyle.Top };
        top.Controls.AddRange(new Control[] { titleLabel, countLabel, subLabel, searchBox, newButton, vaultButton, fullButton });
        fullButton.Click += (_, _) => FullScreenRequested?.Invoke();
        top.Layout += (_, _) => LayoutTop();

        flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        flow.Resize += (_, _) => CenterCards();
        emptyLabel = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false, UseMnemonic = false,
            Text = "아직 작품이 없어요.\n[+ 새 작품]을 눌러 첫 이야기를 시작해 보세요.", Font = new Font(ff, 11f),
        };
        main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(flow);
        main.Controls.Add(emptyLabel);
        main.Controls.Add(top);

        brand = new SidebarBrand { ShowToggle = false }; // 사이드바는 접지 않는다
        brand.BrandClicked += () => { searchText.Text = ""; Reload(); };
        libraryItem = new SidebarNavItem("", "내 서재") { Selected = true };
        libraryItem.Click += (_, _) => { searchText.Text = ""; Reload(); };
        vaultItem = new SidebarNavItem("", "볼트 바꾸기");
        vaultItem.Click += (_, _) => ChangeVaultRequested?.Invoke();
        settingsItem = new SidebarNavItem("", "설정");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        helpItem = new SidebarNavItem("", "도움말");
        helpItem.Click += (_, _) => HelpRequested?.Invoke();
        updateItem = new SidebarNavItem("", "업데이트 확인");
        updateItem.Click += (_, _) => _ = Updater.CheckAsync(FindForm());
        vaultLabel = new Label { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Font = new Font(ff, 8.5f), Text = vaultPath };
        nav = new Panel();
        nav.Controls.AddRange(new Control[] { brand, libraryItem, vaultItem, settingsItem, helpItem, updateItem, vaultLabel });
        nav.Layout += (_, _) => LayoutNav();
        nav.Paint += (_, e) => { if (palette != null) { using var pen = new Pen(palette.Line); e.Graphics.DrawLine(pen, nav.Width - 1, 0, nav.Width - 1, nav.Height); } };
        Controls.Add(main);
        Controls.Add(nav);
        nav.Dock = DockStyle.Left;
        SetNavCollapsed(false);
    }

    private void SetNavCollapsed(bool c)
    {
        navCollapsed = c;
        nav.Width = S(c ? 64 : 236);
        brand.Collapsed = c;
        foreach (var i in new[] { libraryItem, vaultItem, settingsItem, helpItem, updateItem }) i.Collapsed = c;
        vaultLabel.Visible = !c;
        nav.PerformLayout();
        nav.Invalidate();
    }

    private void LayoutNav()
    {
        int w = nav.ClientSize.Width - 1;
        brand.SetBounds(0, S(10), w, S(navCollapsed ? 100 : 88));
        int y = brand.Bottom + S(14), h = S(38);
        foreach (var i in new[] { libraryItem, vaultItem }) { i.SetBounds(0, y, w, h); y += h + S(2); }
        // 맨 아래: 볼트 경로 위에 [도움말] 그 밑에 [설정] 순서로 붙인다
        int labelTop = nav.ClientSize.Height - S(44);
        settingsItem.SetBounds(0, labelTop - S(10) - h, w, h);
        helpItem.SetBounds(0, settingsItem.Top - S(2) - h, w, h);
        updateItem.SetBounds(0, helpItem.Top - S(2) - h, w, h);
        vaultLabel.SetBounds(S(22), labelTop, Math.Max(0, w - S(36)), vaultLabel.Font.Height * 2);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int Gutter => S(40);
    private int CardH => S(178);   // 한 줄에 한 작품: 표지 150 + 위아래 여백
    private int GapX => S(24);     // 책과 책 사이(가로)
    private int GapY => 0;         // 줄 사이는 구분선이 맡는다
    private int MinCardW => S(300);

    private void LayoutTop()
    {
        int cx = top.ClientSize.Width / 2;
        fullButton.SetBounds(top.ClientSize.Width - S(24) - S(40), S(14), S(40), S(40)); // 오른쪽 위 구석
        int y = S(48);
        int tw = titleLabel.Width + S(10) + countLabel.Width;
        titleLabel.Location = new Point(cx - tw / 2, y);
        countLabel.Location = new Point(titleLabel.Right + S(10), titleLabel.Bottom - countLabel.Height - S(5));
        y = titleLabel.Bottom + S(10);
        subLabel.Location = new Point(cx - subLabel.Width / 2, y);
        y = subLabel.Bottom + S(26);
        int bh = Math.Max(newButton.Height, S(38));
        int searchW = Math.Clamp(top.ClientSize.Width - Gutter * 2 - newButton.Width - vaultButton.Width - S(16), S(160), S(400));
        int rowW = searchW + S(8) + newButton.Width + S(8) + vaultButton.Width;
        int x = cx - rowW / 2;
        searchBox.SetBounds(x, y, searchW, bh);
        newButton.SetBounds(searchBox.Right + S(8), y, newButton.Width, bh);
        vaultButton.SetBounds(newButton.Right + S(8), y, vaultButton.Width, bh);
        int h = y + bh + S(40);
        if (top.Height != h) top.Height = h;
    }

    private void PlaceSearchText()
    {
        int pad = S(36);
        searchText.SetBounds(pad, (searchBox.Height - searchText.Height) / 2, Math.Max(10, searchBox.Width - pad - S(12)), searchText.Height);
    }

    private void PaintSearchBox(object? sender, PaintEventArgs e)
    {
        var p = palette;
        if (p == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(top.BackColor);
        var r = new RectangleF(0.5f, 0.5f, searchBox.Width - 1.5f, searchBox.Height - 1.5f);
        using var path = LibraryPalette.Rounded(r, S(8));
        using (var b = new SolidBrush(searchText.BackColor)) g.FillPath(b, path);
        using (var pen = new Pen(searchText.Focused ? Themes.Blend(p.Line, p.Accent, 0.6f) : p.Line)) g.DrawPath(pen, path);
        // 돋보기
        float k = S(1);
        using var mp = new Pen(p.Dim, 1.6f * k);
        float cx = S(18), cy = searchBox.Height / 2f - 1.5f * k, rr = 5.5f * k;
        g.DrawEllipse(mp, cx - rr, cy - rr, rr * 2, rr * 2);
        g.DrawLine(mp, cx + rr * 0.7f, cy + rr * 0.7f, cx + rr * 1.6f, cy + rr * 1.6f);
    }

    /// <summary>카드 한 장의 폭: 한 줄에 한 작품이라 쪽 폭 전체(양옆 여백만 뺀다).</summary>
    private int CardWidth() => Math.Max(MinCardW, flow.ClientSize.Width - Gutter * 2 - SystemInformation.VerticalScrollBarWidth);

    private void CenterCards()
    {
        var pad = new Padding(Gutter, S(8), 0, S(40)); // 왼쪽부터 채운다(Muvel 도서관처럼 칸이 폭을 나눠 가진다)
        if (flow.Padding != pad) flow.Padding = pad;
        int cw = CardWidth();
        foreach (var c in flow.Controls.OfType<NovelCard>())
            if (c.Width != cw || c.Height != CardH) c.Size = new Size(cw, CardH);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); top.PerformLayout(); }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = flow.BackColor = emptyLabel.BackColor = main.BackColor = p.Main;
        nav.BackColor = vaultLabel.BackColor = p.Side;
        vaultLabel.ForeColor = p.Dim;
        brand.SetPalette(p);
        foreach (var i in new[] { libraryItem, vaultItem, settingsItem, helpItem, updateItem }) i.SetPalette(p);
        top.SetPalette(p);
        fullButton.SetPalette(p);
        titleLabel.BackColor = countLabel.BackColor = subLabel.BackColor = Color.Transparent; // 창 테마 적용이 칠해 둔 바탕을 걷어 낸다(그라데이션이 비치게)
        titleLabel.ForeColor = p.Fore;
        countLabel.ForeColor = subLabel.ForeColor = emptyLabel.ForeColor = p.Dim;
        searchText.BackColor = p.Dark ? Themes.Blend(p.Main, Color.White, 0.05f) : Color.White;
        searchText.ForeColor = p.Fore;
        newButton.SetPalette(p);
        vaultButton.SetPalette(p);
        foreach (var c in flow.Controls.OfType<NovelCard>()) c.SetPalette(p);
        searchBox.Invalidate();
    }

    /// <summary>작품 목록을 다시 읽는다(글자 수는 뒤에서 세어 카드에 채운다).</summary>
    public void Reload()
    {
        int gen = ++loadGen;
        var novels = NovelStore.ListNovels(vaultPath);
        novelCount = novels.Count;
        flow.SuspendLayout();
        foreach (Control c in flow.Controls.Cast<Control>().ToList()) { flow.Controls.Remove(c); c.Dispose(); }
        foreach (var dir in novels)
        {
            var meta = NovelStore.LoadMeta(dir);
            var card = new NovelCard(dir, meta.Description, meta.Tags) { Size = new Size(CardWidth(), CardH), Margin = new Padding(0, 0, 0, GapY) };
            if (palette != null) card.SetPalette(palette);
            card.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) NovelOpened?.Invoke(dir);
                else if (e.Button == MouseButtons.Right) ShowCardMenu(card, e.Location);
            };
            flow.Controls.Add(card);
        }
        flow.ResumeLayout();
        CenterCards();
        countLabel.Text = $"{novels.Count}작품";
        ApplyFilter();
        top.PerformLayout();

        var cards = flow.Controls.OfType<NovelCard>().ToList();
        Task.Run(() =>
        {
            for (int wait = 0; !IsHandleCreated && wait < 100 && gen == loadGen; wait++) Thread.Sleep(50); // 창이 뜨기 전이면 기다렸다가 채운다
            foreach (var card in cards)
            {
                if (gen != loadGen) return;
                var eps = NovelStore.Episodes(card.NovelPath);
                long chars = eps.Sum(p => (long)NovelStore.CountChars(p));
                var edited = NovelStore.LastEdited(card.NovelPath);
                try { BeginInvoke(() => { if (gen == loadGen && !card.IsDisposed) card.SetStats(eps.Count, chars, edited); }); } catch { return; }
            }
        });
    }

    private void ApplyFilter()
    {
        string q = searchText.Text.Trim();
        int shown = 0;
        flow.SuspendLayout();
        foreach (var c in flow.Controls.OfType<NovelCard>())
        {
            bool v = q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || (c.Description?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
            c.Visible = v;
            if (v) shown++;
        }
        flow.ResumeLayout();
        emptyLabel.Text = novelCount == 0 ? "아직 작품이 없어요.\n[+ 새 작품]을 눌러 첫 이야기를 시작해 보세요." : $"'{q}'에 맞는 작품이 없어요.";
        emptyLabel.Visible = shown == 0;
        flow.Visible = shown > 0;
        if (shown == 0) emptyLabel.BringToFront();
    }

    private void CreateNovel()
    {
        using var dlg = new NovelInfoDialog("", null, null, creating: true); // 기본 정보 창과 같은 모양(제목 · 작품 설명 · 표지)
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            string dir = NovelStore.CreateNovel(vaultPath, dlg.TitleText, dlg.DescText);
            if (dlg.RemoveCover) NovelStore.DeleteCover(dir);
            if (dlg.NewCoverSource != null) NovelStore.SaveCover(dir, dlg.NewCoverSource);
            NovelStore.AddEpisode(dir); // 바로 쓸 수 있게 1편을 하나 만들어 둔다
            NovelOpened?.Invoke(dir);
        }
        catch (Exception ex) { Warn($"작품을 만들지 못했어요: {ex.Message}"); }
    }

    private ContextMenuStrip? cardMenu;

    private void ShowCardMenu(NovelCard card, Point at)
    {
        cardMenu?.Dispose();
        var m = cardMenu = new ContextMenuStrip();
        string dir = card.NovelPath;
        m.Items.Add("열기", null, (_, _) => NovelOpened?.Invoke(dir));
        m.Items.Add("기본 정보…", null, (_, _) => BeginInvoke(() => EditInfo(dir)));
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\""));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(() => DeleteNovel(dir)));
        m.Show(card, at);
    }

    /// <summary>[기본 정보] 창: 제목(폴더 이름)과 작품 설명을 한 번에 고친다.</summary>
    private void EditInfo(string dir)
    {
        using var dlg = new NovelInfoDialog(Path.GetFileName(dir), NovelStore.LoadMeta(dir).Description, dir);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            NovelStore.SaveDescription(dir, dlg.DescText);
            if (dlg.NewCoverSource != null) NovelStore.SaveCover(dir, dlg.NewCoverSource);
            if (dlg.TitleText != Path.GetFileName(dir)) { PathAboutToChange?.Invoke(dir); NovelStore.RenameNovel(dir, dlg.TitleText); }
        }
        catch (Exception ex) { Warn($"고치지 못했어요: {ex.Message}"); }
        Reload();
    }

    private void DeleteNovel(string dir)
    {
        if (MessageBox.Show(FindForm(), $"'{Path.GetFileName(dir)}' 작품을 폴더째 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { PathAboutToChange?.Invoke(dir); FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { Warn($"삭제하지 못했어요: {ex.Message}"); }
        Reload();
    }

    private void Warn(string msg) => MessageBox.Show(FindForm(), msg, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>첫 화면 머리: 위쪽에서 아래로 강조색이 아주 옅게 번지다 바탕색으로 사라지는 띠(Muvel 도서관 머리처럼).</summary>
    private sealed class HomeHeader : Panel
    {
        private LibraryPalette? palette;
        public HomeHeader() { DoubleBuffered = true; ResizeRedraw = true; }
        public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Main; Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var p = palette;
            if (p == null || Height <= 0) { base.OnPaintBackground(e); return; }
            Color tint = Themes.Blend(p.Main, p.Accent, p.Dark ? 0.12f : 0.08f);
            using var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Height), tint, p.Main, LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }
}

/// <summary>작품 한 칸: 왼쪽은 책 표지(세로로 긴 둥근 네모, 제목 첫 글자), 오른쪽은 제목 · N편 · 글자 수 · 소개 · 고친 때.</summary>
internal sealed class NovelCard : Control
{
    public string NovelPath { get; }
    public string Title { get; }
    public string? Description { get; }
    private LibraryPalette? palette;
    private bool hover;
    private int episodes = -1;
    private long chars;
    private DateTime edited;

    public List<string> Tags { get; }

    public NovelCard(string path, string? description, List<string>? tags = null)
    {
        NovelPath = path;
        Tags = tags ?? new();
        Title = Path.GetFileName(path);
        Description = description;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    private Bitmap? coverImg;
    private bool coverTried;
    protected override void Dispose(bool disposing) { if (disposing) coverImg?.Dispose(); base.Dispose(disposing); }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    public void SetStats(int eps, long chars, DateTime edited) { episodes = eps; this.chars = chars; this.edited = edited; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    /// <summary>제목마다 정해진 표지 색(커피 · 크림 계열에서 고른다).</summary>
    private static Color CoverColor(string s, bool dark)
    {
        Color[] tones =
        {
            Color.FromArgb(0xC8, 0x9F, 0x7A), Color.FromArgb(0x9C, 0xAF, 0x88), Color.FromArgb(0x8F, 0xA8, 0xC0),
            Color.FromArgb(0xC9, 0x8E, 0x8E), Color.FromArgb(0xB3, 0x9C, 0xC4), Color.FromArgb(0xD4, 0xB4, 0x6A),
            Color.FromArgb(0x7F, 0xB0, 0xA8), Color.FromArgb(0xA8, 0x8A, 0x70),
        };
        uint h = 2166136261;
        foreach (char c in s) h = unchecked((h ^ c) * 16777619);
        var t = tones[h % (uint)tones.Length];
        return dark ? Themes.Blend(t, Color.Black, 0.35f) : t;
    }

    /// <summary>한 줄에 한 작품: 왼쪽 표지, 오른쪽에 [제목 N편] / 소개(두 줄) / [로컬 · 글자 수 · 고친 때] / 태그 알약. 밑에 구분선.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        string ff = NovelStore.UiFontFamily;

        if (hover)
        {
            using var hb = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.05f : 0.03f));
            g.FillRectangle(hb, 0, 0, Width, Height - 1);
        }

        // 책 표지(400:600)
        int padY = S(14);
        int coverH = Height - padY * 2, coverW = (int)(coverH * (400.0 / 600.0));
        var cover = new RectangleF(S(10), padY, coverW, coverH);
        using (var cp = LibraryPalette.Rounded(cover, S(5)))
        {
            var col = CoverColor(Title, p.Dark);
            using (var cb = new LinearGradientBrush(Rectangle.Round(cover), Themes.Blend(col, Color.White, 0.2f), col, LinearGradientMode.ForwardDiagonal)) g.FillPath(cb, cp);
            using (var spine = new SolidBrush(Color.FromArgb(40, Color.Black))) g.FillRectangle(spine, cover.X + S(5), cover.Y + 1, S(2), cover.Height - 2); // 책등 자국
            if (!coverTried) { coverTried = true; coverImg = NovelStore.LoadCover(NovelPath); } // 표지 이미지가 있으면 처음 그릴 때 한 번 읽는다
            if (coverImg != null)
            {
                var old = g.Clip;
                g.SetClip(cp, System.Drawing.Drawing2D.CombineMode.Intersect);
                g.DrawImage(coverImg, Rectangle.Round(cover));
                g.Clip = old;
            }
            using (var pen = new Pen(Themes.Blend(col, Color.Black, 0.15f))) g.DrawPath(pen, cp);
        }
        if (coverImg == null)
        {
            string initial = Title.Length > 0 ? char.IsSurrogate(Title[0]) && Title.Length > 1 ? Title[..2] : Title[..1] : "?";
            using var bigFont = new Font(ff, 20f, FontStyle.Bold);
            TextRenderer.DrawText(g, initial, bigFont, Rectangle.Round(cover), Color.FromArgb(240, Color.White),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // 오른쪽 글
        int x = (int)cover.Right + S(24), w = Math.Max(0, Width - x - S(24)), y = padY - S(2);
        var one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        using var tf = new Font(ff, 14f, FontStyle.Bold);
        using var df = new Font(ff, 9.5f);
        using var sf = new Font(ff, 9f);
        TextRenderer.DrawText(g, Title, tf, new Rectangle(x, y, w, tf.Height), p.Fore, one);
        y += tf.Height + S(6);

        // 소개(최대 두 줄)
        bool noDesc = string.IsNullOrWhiteSpace(Description);
        string desc = noDesc ? "소개가 아직 없어요." : Description!.Replace("\r", "").Replace('\n', ' ');
        int dh = df.Height * 2 + 2;
        TextRenderer.DrawText(g, desc, df, new Rectangle(x, y, w, dh), noDesc ? Themes.Blend(p.Main, p.Dim, 0.7f) : p.Dim,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        y += dh + S(10);

        // 한 줄 정보: [문서 아이콘] 로컬 · 글자 수 · 고친 때
        using (var gf = new Font("Segoe MDL2 Assets", 10.5f))
        {
            int lh = df.Height + S(6);
            TextRenderer.DrawText(g, "", gf, new Rectangle(x, y, S(22), lh), p.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int lw = TextRenderer.MeasureText("로컬", df, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, "로컬", df, new Rectangle(x + S(24), y, lw + S(4), lh), p.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            string info = episodes < 0 ? "" : $"·  {episodes}편  ·  {NovelStore.Ago(edited)} 고침"; // 편 수는 로컬 옆으로, 글자 수는 뺐다
            TextRenderer.DrawText(g, info, df, new Rectangle(x + S(24) + lw + S(10), y, Math.Max(0, w - S(34) - lw), lh), p.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            y += lh + S(10);
        }

        // 태그: 강조색 테두리 알약(자리가 모자라면 거기서 멈춘다)
        if (Tags.Count > 0)
        {
            using var bf = new Font(ff, 9f, FontStyle.Bold);
            int ch = bf.Height + S(8), tx = x;
            if (y + ch <= Height - padY + S(4))
                foreach (var tag in Tags)
                {
                    int cw = TextRenderer.MeasureText("#" + tag, bf, Size.Empty, TextFormatFlags.NoPadding).Width + S(24);
                    if (tx + cw > x + w) break;
                    var cr = new RectangleF(tx + 0.5f, y + 0.5f, cw - 1, ch - 1);
                    using (var tp = LibraryPalette.Rounded(cr, ch / 2f))
                    using (var tpen = new Pen(p.Accent, Math.Max(1.4f, DeviceDpi / 96f * 1.4f))) g.DrawPath(tpen, tp);
                    TextRenderer.DrawText(g, "#" + tag, bf, Rectangle.Round(cr), p.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    tx += cw + S(8);
                }
        }

        // 작품 사이 구분선
        using (var lp = new Pen(p.Line)) g.DrawLine(lp, 0, Height - 1, Width, Height - 1);
    }
}
