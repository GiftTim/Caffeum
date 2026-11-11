namespace CoffeeViewer;

/// <summary>
/// Caffeum의 본체 창(볼트 하나).
///  · 첫 화면: 작품 목록(NovelHome — 왼쪽 옆 막대 · 내 서재 · 작품 카드).
///  · 작품에 들어가면 늘 워크스페이스(NovelWorkspace — 아이콘 막대 · 파일 트리 · 문서 칸들, Muvel처럼).
/// 회차 하나 = 읽기 · 쓰기 화면(MainForm) 하나라서 txt 편집 · 낭독 · 이어 읽기를 그대로 쓴다.
/// </summary>
internal sealed class VaultForm : Form
{
    private readonly string vaultPath;
    private readonly string vaultName;
    private readonly VaultSidebar sidebar;      // 볼트 전체 파일 트리(워크스페이스 아이콘 막대의 [볼트 파일])
    private readonly NovelHome home;
    private readonly NovelWorkspace workspace;
    private bool homeMode = true;
    private bool fullScreen;
    private FormBorderStyle savedBorder;
    private FormWindowState savedState;
    private LibraryPalette palette = LibraryPalette.From(Themes.All[0]);
    private readonly Font baseFont;

    public string VaultPath => vaultPath;

    /// <summary>시험용(CAFFEUM_TEST_NOACTIVATE): 창이 떠도 앞으로 나오지 않게 — 화면 캡처로 확인하는 동안 사용자가 치는 키가 시험 창으로 들어가지 않게 한다.</summary>
    protected override bool ShowWithoutActivation => Environment.GetEnvironmentVariable("CAFFEUM_TEST_NOACTIVATE") == "1";

    /// <summary>시험용: 키보드 · 마우스 입력을 모두 버린다. 창이 앞으로 나와도 사용자가 치던 글이나 누른 자리가 시험 창에 들어가지 않게.</summary>
    private sealed class DropKeyboardFilter : IMessageFilter
    {
        // 키보드 · 한글 조합 · 마우스(창 안 · 제목 표시줄) 메시지를 모두 버린다
        public bool PreFilterMessage(ref Message m) => m.Msg is >= 0x0100 and <= 0x0109 || m.Msg is >= 0x010D and <= 0x010F || m.Msg is 0x0281 or 0x0282 or 0x0286
            || m.Msg is >= 0x0201 and <= 0x020E || m.Msg is >= 0x00A1 and <= 0x00AD;
    }
    private static bool keyboardFilterAdded;
    // ───────── 직접 그린 제목줄(WindowTitleBar) ─────────
    // 윈도우 기본 제목줄은 높이 · 아이콘 자리를 못 바꾸니, 제목줄 · 테두리를 클라이언트 영역으로 먹어 버리고 우리 줄(titleBar)을 맨 위에 둔다.
    // 창 이동 · 두 번 눌러 최대화 · 가장자리로 크기 조절은 WM_NCHITTEST에서 알려 주면 윈도우가 그대로 해 준다.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    private readonly WindowTitleBar titleBar = new() { Dock = DockStyle.Top };
    private bool chromeOn = true; // 직접 그린 제목줄을 쓰는 중(몰입 · 전체 화면이면 false). Visible은 창이 뜨기 전엔 false라서 따로 둔다

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084;
        if (chromeOn && !fullScreen)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                if (IsZoomed(Handle))
                {
                    // 최대화하면 창이 화면 테두리 두께만큼 밖으로 나가 있다 — 그 자리까지 클라이언트로 쓰면 제목줄이 화면 위로 잘린다.
                    // 그래서 최대화 때는 클라이언트를 이 모니터의 작업 영역(작업 표시줄 뺀 자리)에 딱 맞춘다.
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 0, wa.Left);
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 4, wa.Top);
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 8, wa.Right);
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 12, wa.Bottom);
                }
                m.Result = IntPtr.Zero; // 제안된 사각형 전체를 클라이언트로 쓴다(기본 제목줄 · 테두리 없음)
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
                if (!IsZoomed(Handle))
                {
                    int b = LogicalToDeviceUnits(6);
                    bool left = pt.X < b, right = pt.X >= ClientSize.Width - b, top = pt.Y < b, bottom = pt.Y >= ClientSize.Height - b;
                    int hit = top ? (left ? HTTOPLEFT : right ? HTTOPRIGHT : HTTOP) : bottom ? (left ? HTBOTTOMLEFT : right ? HTBOTTOMRIGHT : HTBOTTOM) : left ? HTLEFT : right ? HTRIGHT : 0;
                    if (hit != 0) { m.Result = (IntPtr)hit; return; }
                }
                m.Result = (IntPtr)(titleBar.Bounds.Contains(pt) && titleBar.IsCaptionPoint(new Point(pt.X - titleBar.Left, pt.Y - titleBar.Top)) ? HTCAPTION : HTCLIENT);
                return;
            }
        }
        base.WndProc(ref m);
    }

    private bool clamping;
    private Rectangle normalBounds;
    private FormWindowState prevState = FormWindowState.Normal;

    /// <summary>최대화 / 이전 크기 아이콘을 다시 그리고, 이전 크기로 돌아올 때는 최대화 직전의 자리와 크기로 되돌린다(윈도우가 계산하게 두면 누를 때마다 옆으로 밀린다).</summary>
    private void OnStateOrSizeChanged()
    {
        titleBar.Invalidate();
        var st = WindowState;
        if (st == FormWindowState.Normal && IsHandleCreated)
        {
            if (prevState == FormWindowState.Maximized && !normalBounds.IsEmpty)
            {
                var target = normalBounds;
                clamping = true; // 크기 변경 처리 도중에 바로 바꾸면 안쪽 배치가 옛 크기로 남는다 — 끝난 뒤에 맞추고 배치를 다시 한다
                BeginInvoke(() =>
                {
                    try { if (WindowState == FormWindowState.Normal) { Bounds = target; PerformLayout(); titleBar.Width = ClientSize.Width; Invalidate(true); } }
                    finally { clamping = false; }
                });
            }
            else if (!clamping) normalBounds = Bounds;
            ClampToWorkArea();
        }
        prevState = st;
    }

    private void ClampToWorkArea()
    {
        if (clamping || WindowState != FormWindowState.Normal || !IsHandleCreated) return;
        var wa = Screen.FromControl(this).WorkingArea;
        var b = Bounds;
        int w = Math.Min(b.Width, wa.Width), h = Math.Min(b.Height, wa.Height);
        int x = Math.Clamp(b.X, wa.Left, wa.Right - w), y = Math.Clamp(b.Y, wa.Top, wa.Bottom - h);
        if (w == b.Width && h == b.Height && x == b.X && y == b.Y) return;
        clamping = true;
        try { SetBounds(x, y, w, h); } finally { clamping = false; }
    }

    public VaultForm(string vaultPath)
    {
        this.vaultPath = Path.GetFullPath(vaultPath).TrimEnd('\\');
        baseFont = Font;
        vaultName = Path.GetFileName(this.vaultPath);
        if (vaultName.Length == 0) vaultName = this.vaultPath; // 드라이브 루트(D:\)

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = vaultName + " — " + AppShared.AppDisplayName;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized; // 기본은 최대화로 시작(풀면 위 크기로 돌아온다)
        SizeChanged += (_, _) => OnStateOrSizeChanged();
        Shown += (_, _) => { if (normalBounds.IsEmpty && WindowState == FormWindowState.Maximized) { var wa = Screen.FromControl(this).WorkingArea; int w = Math.Min(RestoreBounds.Width, wa.Width - 40), h = Math.Min(RestoreBounds.Height, wa.Height - 40); normalBounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h); } };
        LocationChanged += (_, _) => { if (WindowState == FormWindowState.Normal && prevState == FormWindowState.Normal && !clamping) normalBounds = Bounds; };
        Icon = AppShared.LoadAppIcon();
        AllowDrop = true;
        KeyPreview = true;
        if (Environment.GetEnvironmentVariable("CAFFEUM_TEST_NOACTIVATE") == "1" && !keyboardFilterAdded) { keyboardFilterAdded = true; Application.AddMessageFilter(new DropKeyboardFilter()); }
        Load += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(Width, wa.Width - LogicalToDeviceUnits(24)), h = Math.Min(Height, wa.Height - LogicalToDeviceUnits(24));
            if (w == Width && h == Height) return;
            Size = new Size(Math.Max(MinimumSize.Width, w), Math.Max(MinimumSize.Height, h));
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        };

        sidebar = new VaultSidebar(this.vaultPath, vaultName);
        sidebar.OpenRequested += OpenDocument;

        workspace = new NovelWorkspace(sidebar) { Dock = DockStyle.Fill, Visible = false };
        sidebar.PathAboutToChange += p => workspace.CloseDocsUnder(p);
        workspace.HomeRequested += ShowHome;
        workspace.HelpRequested += ShowHelp;
        workspace.SettingsRequested += () => AppShared.ShowSettings(this);
        workspace.FullScreenRequested += ToggleFullScreen;
        workspace.PinRequested += () => { TopMost = !TopMost; workspace.SetPinned(TopMost); };
        workspace.SearchRequested += () => sidebar.FocusSearch();
        workspace.TitleChanged += t => Text = (t.Length > 0 ? t + " · " : "") + Path.GetFileName(workspace.NovelPath ?? "") + " — " + AppShared.AppDisplayName;

        home = new NovelHome(this.vaultPath) { Dock = DockStyle.Fill };
        home.NovelOpened += EnterNovel;
        home.PathAboutToChange += p => workspace.CloseDocsUnder(p);
        home.ChangeVaultRequested += () => AppShared.PickAndOpenVault(this);
        home.SettingsRequested += () => AppShared.ShowSettings(this);
        home.HelpRequested += ShowHelp;
        home.FullScreenRequested += ToggleFullScreenPinned;

        Controls.Add(workspace);
        Controls.Add(home);
        Controls.Add(titleBar); // 맨 나중에 넣어야 맨 위에 붙는다(Dock 순서)
        workspace.ChromeVisibilityChanged += v => { chromeOn = v && !fullScreen; titleBar.Visible = chromeOn; RecreateHandleFrame(); }; // 문서 몰입 모드에서는 제목줄도 숨긴다
        Shown += (_, _) => RecreateHandleFrame(); // 창이 뜬 뒤에 제목줄 · 테두리 계산을 한 번 다시 시킨다

        FormClosing += (_, _) => workspace.CloseAll(); // 고친 글 · 읽던 자리를 저장하고 닫는다
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (paths.FirstOrDefault(Directory.Exists) is string dir) AppShared.OpenVault(dir); // 폴더를 놓으면 그 폴더를 볼트로
            else if (paths.FirstOrDefault(File.Exists) is string f) OpenDocument(f);
        };
        KeyDown += (_, e) =>
        {
            if (e.Alt && e.KeyCode == Keys.W && !homeMode) { workspace.Tree.CreateWiki(); e.Handled = e.SuppressKeyPress = true; } // Muvel처럼 Alt+W = 빠른 위키 문서
            else if (e.Control && e.KeyCode == Keys.W && !homeMode) { workspace.CloseFocused(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Tab && !homeMode) { workspace.CycleFocus(); e.Handled = true; }
            else if (e.Control && e.Shift && e.KeyCode == Keys.F && !homeMode) { workspace.Tree.ShowPage(2); sidebar.FocusSearch(); e.Handled = true; }
            else if (e.KeyCode == Keys.F1) { ShowHelp(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Oemcomma) { AppShared.ShowSettings(this); e.Handled = true; }
            else if (e.KeyCode == Keys.F5 && homeMode) { home.Reload(); e.Handled = true; }
        };

        ApplyTheme(AppShared.LibraryTheme);
        ApplyVaultSettings();
        ShowHome();
        // 시험용: 작품 폴더(CAFFEUM_TEST_NOVEL)로 시작하고, CAFFEUM_TEST_WIKI가 파일이면 그 문서를 연다(화면 캡처로 확인할 때만 쓴다).
        if (Environment.GetEnvironmentVariable("CAFFEUM_TEST_NOVEL") is { Length: > 0 } testNovel && Directory.Exists(testNovel))
        {
            EnterNovel(testNovel);
            if (Environment.GetEnvironmentVariable("CAFFEUM_TEST_WIKI") is { Length: > 0 } tw && File.Exists(tw)) workspace.Open(tw, split: false);
            if (Environment.GetEnvironmentVariable("CAFFEUM_TEST_SPLIT") is { Length: > 0 } ts && File.Exists(ts)) workspace.Open(ts, split: true);
        }
    }

    private void ShowHelp()
    {
        using var help = new HelpForm();
        help.ApplyTheme(Themes.Find(AppShared.Settings.Theme));
        help.TopMost = TopMost;
        help.ShowDialog(this);
    }

    // ───────────────────────── 작품 목록 · 워크스페이스 ─────────────────────────

    /// <summary>첫 화면(작품 목록)으로. 워크스페이스의 문서들은 저장하고 닫는다.</summary>
    public void ShowHome()
    {
        workspace.CloseAll();
        homeMode = true;
        workspace.Visible = false;
        home.Visible = true;
        home.BringToFront();
        home.Reload();
        Text = vaultName + " — " + AppShared.AppDisplayName;
    }

    /// <summary>작품(폴더)의 워크스페이스로 들어간다. 지난번에 열어 둔 칸들이 되살아난다.</summary>
    private void EnterNovel(string novelDir)
    {
        homeMode = false;
        home.Visible = false;
        workspace.Visible = true;
        workspace.BringToFront();
        if (!string.Equals(workspace.NovelPath, novelDir, StringComparison.OrdinalIgnoreCase) || workspace.Focused.DocPath == null)
            workspace.LoadNovel(novelDir);
        Text = Path.GetFileName(novelDir) + " — " + AppShared.AppDisplayName;
    }

    /// <summary>문서를 연다(작품 목록에 있었거나 다른 작품이면 그 작품의 워크스페이스로 들어가서).</summary>
    public void OpenDocument(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) return;
        string novel = NovelDirFor(full);
        if (homeMode || !string.Equals(workspace.NovelPath, novel, StringComparison.OrdinalIgnoreCase))
        {
            homeMode = false;
            home.Visible = false;
            workspace.Visible = true;
            workspace.BringToFront();
            workspace.LoadNovel(novel);
        }
        workspace.Open(full, split: false);
    }

    /// <summary>파일이 속한 작품: 볼트 바로 아래 폴더(볼트 밖이거나 맨 위에 있으면 그 파일이 든 폴더).</summary>
    private string NovelDirFor(string file)
    {
        string rel = Path.GetRelativePath(vaultPath, file);
        if (!rel.StartsWith("..") && !Path.IsPathRooted(rel) && rel.IndexOf('\\') is int i && i > 0)
            return Path.Combine(vaultPath, rel[..i]);
        return Path.GetDirectoryName(file)!;
    }

    /// <summary>창 전체 화면(테두리 없이 화면 가득). 문서 안의 F11 몰입 모드와는 따로.</summary>
    // 서재의 전체 화면 버튼: 전체 화면이 되면서 [항상 위]도 같이 켠다. 나갈 때는 켜기 전 상태로 되돌린다.
    private bool pinnedByFull;
    private bool topBeforeFull;

    private void ToggleFullScreenPinned()
    {
        if (!fullScreen)
        {
            topBeforeFull = TopMost;
            pinnedByFull = true;
            ToggleFullScreen();
            TopMost = true;
            workspace.SetPinned(true);
        }
        else ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        if (fullScreen && pinnedByFull) // 전체 화면과 함께 건 고정은 나올 때 함께 푼다(어디서 나가든)
        {
            pinnedByFull = false;
            TopMost = topBeforeFull;
            workspace.SetPinned(topBeforeFull);
        }
        if (!fullScreen)
        {
            savedBorder = FormBorderStyle; savedState = WindowState;
            FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized;
        }
        else { FormBorderStyle = savedBorder; WindowState = savedState; }
        fullScreen = !fullScreen;
        workspace.SetFullScreenIcon(fullScreen); // 머리줄의 전체 화면 아이콘을 "나가기" 모양으로
        home.SetFullScreenIcon(fullScreen);
        chromeOn = !fullScreen;
        titleBar.Visible = chromeOn; // 창 전체 화면에서는 직접 그린 제목줄도 숨긴다
        RecreateHandleFrame();
    }

    /// <summary>제목줄 · 테두리 계산(WM_NCCALCSIZE)을 다시 하게 한다.</summary>
    private void RecreateHandleFrame() => SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020 /*NOSIZE|NOMOVE|NOZORDER|FRAMECHANGED*/);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>설정 > 아이콘 막대(순서 · 켜고 끄기)가 바뀌었다(설정 종류 "toolbar").</summary>
    public void ApplyHeaderButtons() => workspace.ApplyRailSettings();

    // ───────────────────────── 테마 · 글자 크기 ─────────────────────────

    public void ApplyTheme(ReaderTheme t)
    {
        Themes.ApplyChrome(this, t); // 제목표시줄(다크)과 기본 색
        palette = LibraryPalette.From(t);
        BackColor = palette.Main;
        sidebar.ApplyTheme(palette);
        home.ApplyTheme(palette);
        workspace.ApplyTheme(palette);
        titleBar.SetPalette(palette);
    }

    /// <summary>설정의 "목록 글자 크기"를 파일 트리 · 볼트 파일에 반영한다.</summary>
    public void ApplyVaultSettings()
    {
        var f = new Font(baseFont.FontFamily, Math.Clamp(AppShared.Settings.LibraryFontSize, 8, 24), baseFont.Style);
        sidebar.ApplyFont(f);
        workspace.ApplyFont(f);
    }
}
