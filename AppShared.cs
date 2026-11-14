namespace CoffeeViewer;

/// <summary>
/// 프로그램 전체가 함께 쓰는 것들: 설정, 읽은 기록, 그리고 볼트 창(VaultForm)·설정 창을 여는 곳.
/// 카페움 오피스는 볼트(폴더 하나)를 여는 창이 본체이고, 문서마다 읽기 화면(MainForm)이 그 창 안의 탭으로 들어간다.
/// 창이 여러 개여도 설정과 읽은 기록은 하나를 같이 써야 한다(창마다 따로 읽어 저장하면 서로 덮어쓴다).
/// </summary>
internal static class AppShared
{
    /// <summary>exe에 넣어 둔 그림을 px 정사각 칸에 비율을 지켜 맞춰 읽는다. 못 읽으면 null.</summary>
    public static Image? LoadScaledImage(string resource, int px)
    {
        try
        {
            using var s = typeof(AppShared).Assembly.GetManifestResourceStream(resource);
            if (s == null) return null;
            using var src = new Bitmap(s);
            var bmp = new Bitmap(px, px);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            float k = Math.Min((float)px / src.Width, (float)px / src.Height);
            float w = src.Width * k, h = src.Height * k;
            g.DrawImage(src, (px - w) / 2f, (px - h) / 2f, w, h);
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>창 · 작업 표시줄 아이콘: 앱에 넣어 둔 Caffeum.ico에서 48px 그림을 골라 쓴다(ExtractAssociatedIcon은 32px만 줘서 작업 표시줄에서 작게 보였다).</summary>
    public static Icon? LoadAppIcon()
    {
        try
        {
            using var s = typeof(AppShared).Assembly.GetManifestResourceStream("CaffeumTaskbar.ico"); // 창 · 작업 표시줄 아이콘은 흰 바탕 로고(어두운 작업 표시줄에서도 보이게)
            if (s != null) return new Icon(s, 48, 48);
        }
        catch { }
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    /// <summary>화면에 보이는 프로그램 이름과 한 줄 소개(머리 막대 · 제목표시줄). 내부 이름(네임스페이스 CoffeeViewer)은 그대로.</summary>
    public const string AppDisplayName = "Caffeum";
    public const string AppSubtitle = "작은 카페 속 당신의 이야기";

    public static AppSettings Settings { get; } = AppSettings.Load();
    /// <summary>문서별 읽은 위치 · 진행률 · 최근 연 파일 기록(library.json). 서재 화면은 없지만 이어 읽기에 그대로 쓴다.</summary>
    public static LibraryStore Library { get; } = LibraryStore.Load();

    /// <summary>볼트 창이 쓰는 색 테마. 볼트 전용 색을 골랐으면 그것, 아니면 읽기 화면 테마를 그대로 따른다.</summary>
    public static ReaderTheme LibraryTheme =>
        Themes.All.FirstOrDefault(t => t.Name == Settings.LibraryTheme) ?? Themes.Find(Settings.Theme);

    /// <summary>설정이 바뀌었다(종류: theme, font, fontsize, margin, tts, pdf). 열려 있는 읽기 화면들이 각자 반영한다.</summary>
    public static event Action<string>? SettingsChanged;

    private static SettingsForm? settingsForm;
    private static readonly List<MainForm> readers = new();
    private static readonly List<VaultForm> vaults = new();
    private static ApplicationContext? context;
    private static int openWindows;

    /// <summary>
    /// 프로그램 시작. 폴더를 인자로 받으면 그 폴더를 볼트로 열고(뒤따르는 파일 인자는 탭으로), 파일 하나를 받으면(더블클릭 등)
    /// 마지막 볼트(없으면 그 파일이 든 폴더)를 열어 그 문서를 탭으로 띄운다. 인자가 없으면 마지막 볼트를 자동으로 연다.
    /// 처음이거나 마지막 볼트가 사라졌으면 폴더를 고르게 하고, 고르지 않으면 그대로 끝낸다.
    /// </summary>
    public static ApplicationContext Start(string? initialPath, params string[] extraPaths)
    {
        context = new ApplicationContext();
        ShellMenu.Register();
        NovelStore.ListCountWithSpaces = Settings.ListCountWithSpaces;
        ToolStripManager.Renderer = Themes.CreateRenderer(Themes.Find(Settings.Theme));

        // 탐색기에서 파일을 오른쪽 클릭 · 더블클릭으로 열면 볼트 창 없이 PIP 창(참고 자료 서식창 달린 작은 창)으로만 연다.
        var direct = new[] { initialPath }.Concat(extraPaths).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(p => Path.GetFullPath(p!)).ToList();
        if (direct.Count > 0)
        {
            OpenPips(direct);
            return context;
        }

        string? lastVault = Settings.LastVault is { Length: > 0 } last && Directory.Exists(last) ? last : null;
        string? vaultPath;
        var files = new List<string>();
        if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)) { vaultPath = initialPath; files.AddRange(extraPaths.Where(File.Exists)); }
        else if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath)) { vaultPath = lastVault ?? Path.GetDirectoryName(Path.GetFullPath(initialPath)); files.Add(initialPath); }
        else vaultPath = lastVault;

        vaultPath ??= PickVaultFolder(null);
        if (vaultPath == null) { Environment.Exit(0); return context; } // 창이 하나도 없는 채로 남지 않게

        var vault = OpenVault(vaultPath);
        foreach (var f in files) vault.OpenDocument(f);
        return context;
    }

    private static void OpenPips(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            var pip = new PipForm(f);
            Track(pip);
            pip.Show(vaults.LastOrDefault(v => !v.IsDisposed)); // 열려 있는 Caffeum 창이 있으면 그 앞에(없으면 혼자 뜬다)
        }
    }

    /// <summary>이미 떠 있는 프로그램에 다른 실행이 넘긴 인자를 처리한다: 파일은 PIP 창(여러 개 가능)으로, 폴더는 볼트 창으로, 인자가 없으면 기존 창을 앞으로.</summary>
    public static void HandleExternal(string[] args)
    {
        var files = args.Where(File.Exists).Select(Path.GetFullPath).ToList();
        var dir = args.FirstOrDefault(Directory.Exists);
        if (dir != null) { var v = OpenVault(dir); foreach (var f in files) v.OpenDocument(f); return; }
        if (files.Count > 0) { OpenPips(files); return; }
        var vault = vaults.LastOrDefault(v => !v.IsDisposed);
        if (vault == null)
        {
            string? last = Settings.LastVault is { Length: > 0 } l && Directory.Exists(l) ? l : PickVaultFolder(null);
            if (last != null) OpenVault(last);
            return;
        }
        if (vault.WindowState == FormWindowState.Minimized) vault.WindowState = FormWindowState.Normal;
        vault.Activate();
        vault.BringToFront();
    }

    /// <summary>창이 모두 닫히면 프로그램을 끝내기 위해, 최상위 창(볼트 창)을 세어둔다.</summary>
    private static void Track(Form form)
    {
        openWindows++;
        form.FormClosed += (_, _) =>
        {
            if (--openWindows <= 0) context?.ExitThread();
        };
    }

    // ───────────────────────── 읽기 화면 ─────────────────────────

    /// <summary>볼트 창 안의 읽기 화면(탭)을 설정 · 미리보기 · "지금 읽는 중" 판정에 쓰이도록 등록한다(닫히면 저절로 빠진다).</summary>
    public static void RegisterReader(MainForm reader)
    {
        readers.Add(reader);
        reader.FormClosed += (_, _) => readers.Remove(reader);
    }

    /// <summary>설정창 미리보기용: 열려 있는 읽기 화면 중 txt를 보고 있는 가장 최근 것의 전체 글. 없으면 false.</summary>
    public static bool TryGetOpenTxt(out string text, out string name)
    {
        for (int i = readers.Count - 1; i >= 0; i--)
            if (!readers[i].IsDisposed && readers[i].TryGetTxtForPreview(out text, out name)) return true;
        text = name = "";
        return false;
    }

    /// <summary>이 문서가 지금 탭으로 열려 있는지(열려 있으면 그 화면이 위치를 계속 저장한다).</summary>
    public static bool IsOpenInReader(string path)
    {
        string full = Path.GetFullPath(path);
        return readers.Any(r => !r.IsDisposed && string.Equals(r.CurrentFilePath, full, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── 볼트 창 ─────────────────────────

    /// <summary>폴더를 볼트로 연다. 같은 폴더가 이미 열려 있으면 그 창을 앞으로 가져온다. file이 있으면 그 문서를 탭으로 연다.</summary>
    public static VaultForm OpenVault(string folder, string? file = null)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        var form = vaults.FirstOrDefault(v => !v.IsDisposed && string.Equals(v.VaultPath, full, StringComparison.OrdinalIgnoreCase));
        if (form == null)
        {
            form = new VaultForm(full);
            vaults.Add(form);
            var created = form;
            created.FormClosed += (_, _) => vaults.Remove(created);
            Track(created);
            created.Show();
            Settings.LastVault = full;
            Settings.Save();
        }
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        if (file != null) form.OpenDocument(file);
        return form;
    }

    /// <summary>[볼트 열기]: 폴더를 골라 볼트로 연다(이미 열린 볼트가 있으면 새 창으로 뜬다).</summary>
    public static void PickAndOpenVault(IWin32Window? owner)
    {
        if (PickVaultFolder(owner) is string folder) OpenVault(folder);
    }

    private static string? PickVaultFolder(IWin32Window? owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "볼트로 열 폴더를 고르세요", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Settings.LastVault is { Length: > 0 } last && Directory.Exists(last)) dlg.InitialDirectory = last;
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>폴더를 끌어다 놓았을 때: 그 폴더를 볼트로 연다.</summary>
    public static void AddFoldersToLibrary(IEnumerable<string> dirs)
    {
        if (dirs.FirstOrDefault() is string dir) OpenVault(dir);
    }

    // ───────────────────────── 설정 창 ─────────────────────────

    public static void ShowSettings(Form? owner)
    {
        if (settingsForm == null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(Settings, NotifySettingsChanged);
            settingsForm.ApplyTheme(Themes.Find(Settings.Theme));
        }
        // 읽기 화면은 창 안의 탭(TopLevel=false)이라 실제 창(볼트)을 주인으로 삼는다. 주인이 "항상 위"이면 설정도 같이 항상 위로, 주인보다 늘 위에 뜬다.
        var top = owner?.TopLevelControl as Form ?? owner;
        settingsForm.TopMost = top?.TopMost == true;
        if (!settingsForm.Visible) settingsForm.Show(top);
        settingsForm.Activate();
        settingsForm.BringToFront();
    }

    /// <summary>설정 값이 바뀐 걸 모든 창에 알린다. 테마면 볼트 · 설정 창의 색도 바꾼다.</summary>
    public static void NotifySettingsChanged(string kind)
    {
        // 볼트 전용 설정(libtheme · libfont)은 볼트 창만 다시 그리면 되고, 읽기 화면들에는 알릴 게 없다.
        if (kind.StartsWith("lib", StringComparison.Ordinal))
        {
            foreach (var v in vaults.Where(v => !v.IsDisposed))
            {
                if (kind == "libtheme") v.ApplyTheme(LibraryTheme);
                else v.ApplyVaultSettings();
            }
            return;
        }

        if (kind == "theme")
        {
            var theme = Themes.Find(Settings.Theme);
            ToolStripManager.Renderer = Themes.CreateRenderer(theme);
            foreach (var v in vaults.Where(v => !v.IsDisposed)) v.ApplyTheme(LibraryTheme); // 볼트 전용 색을 골라뒀으면 그대로 유지된다
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.ApplyTheme(theme);
        }
        if (kind == "toolbar") foreach (var v in vaults.Where(v => !v.IsDisposed)) v.ApplyHeaderButtons();
        SettingsChanged?.Invoke(kind);
    }
}
