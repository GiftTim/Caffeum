namespace CoffeeViewer;

/// <summary>
/// 글(txt · epub) 위쪽의 얇은 서식 줄: 글꼴 · 크기 · 줄 간격 · 정렬 · 줄바꿈.
/// 값은 문서 전체에 적용되고 파일마다 따로 기억한다(BookRecord.Look*). 한 번도 안 바꾼 파일은 설정창의 기본값을 따른다.
/// </summary>
public partial class MainForm
{
    private BookRecord? lookRec;
    private ToolStrip? lookBar;
    private ToolStripComboBox? lookLangBox, lookFontBox, lookLineBox;
    private NumericUpDown? lookSizeNum;
    private ToolStripControlHost? lookSizeHost;
    private ToolStripLabel? lookPtLabel;
    private readonly List<ToolStripItem> lookFormatItems = new();
    private readonly List<ToolStripSeparator> lookSeps = new();
    private readonly Dictionary<string, List<ToolStripItem>> formatGroups = new();
    private ToolStripButton? lookPaper, lookLeft, lookCenter, lookRight, lookJustify, lookUndo, lookRedo, lookNav, lookTts, lookPara;
    private ToolStripButton? lookDistribute, lookDivide, lookBold, lookItalic, lookUnderline, lookStrike, lookColor;
    private NumericUpDown? lookLineNum;
    private ToolStripControlHost? lookLineHost;
    private ToolStripLabel? lookLineUnit;
    private ToolStrip? readBar; // epub · 만화용 읽기 전용 서식창(네비게이션 | 수정하기 | 보기 설정 | 자동 재생)
    private bool undone;
    private bool syncingLook;

    /// <summary>이 파일의 편집 용지(없으면 새 문서 기본값). 용지 없이 창 폭에 맞추는 상태면 null.</summary>
    private PaperSetup? EffPaper => (lookRec?.Paper ?? settings.Paper) is { Fit: false } p ? p : null;

    private int MmToPx(double mm) => (int)Math.Round(mm * DeviceDpi / 25.4);

    /// <summary>웹 화면(txt · epub)에서 글이 놓이는 칸을 용지 폭 · 위아래 여백대로 만드는 CSS(CSS의 mm는 화면 배율과 상관없이 실제 비율).</summary>
    private string PaperCss()
    {
        if (EffPaper is not { } p) return "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return " html body { box-sizing: content-box !important; max-width: none !important; margin: 0 auto !important;" +
            " width: min(" + p.ContentWidth.ToString("0.##", inv) + "mm, calc(100% - 24px)) !important;" +
            " padding: " + p.Top.ToString("0.##", inv) + "mm 0 " + p.Bottom.ToString("0.##", inv) + "mm 0 !important; }";
    }

    private void OpenPaperSetup()
    {
        if (lookRec == null) return;
        using var dlg = new PaperForm(lookRec.Paper ?? settings.Paper);
        if (dlg.ShowDialog(HostForm) != DialogResult.OK) return;
        var result = dlg.Result;
        if (dlg.AsDefault) { settings.Paper = result.Fit ? null : result.Clone(); settings.Save(); }
        SetLook(r => r.Paper = result);
    }

    private string EffFont => lookRec?.LookFont ?? settings.FontFamily;
    private int EffSize => Math.Clamp(lookRec?.LookSize ?? settings.FontSize, 8, 48);
    private int EffLine => Math.Clamp(lookRec?.LookLine ?? settings.LineHeight, 100, 300);
    private string? EffFontLatin => lookRec?.LookFontLatin;
    private string EffAlign => lookRec?.LookAlign ?? settings.DefaultPara?.Align ?? "left";
    private bool EffWrap => lookRec?.LookWrap ?? settings.TxtWordWrap;

    private void InitLookBar()
    {
        lookBar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Visible = false,
            Padding = new Padding(6, 2, 6, 2),
            ShowItemToolTips = true,
        };
        ToolStripComboBox Combo(int width, string tip)
        {
            var c = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = width, ToolTipText = tip };
            lookBar.Items.Add(c);
            return c;
        }
        ToolStripButton Btn(string text, string tip, Action click)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
            b.Click += (_, _) => click();
            lookBar.Items.Add(b);
            return b;
        }

        lookPaper = Btn("📄", "편집 용지 — 판형 · 방향 · 여백", OpenPaperSetup);
        { var sp = new ToolStripSeparator(); lookBar.Items.Add(sp); lookSeps.Add(sp); }
        // 되돌리기 · 다시 실행 — 고치는 중(txt)일 때만 쓸 수 있다. 메모장 상자는 한 단계만 기억하므로 다시 실행은 방금 한 되돌리기를 되돌린다.
        var iconFont = new Font("Segoe UI Symbol", 12f);
        Disposed += (_, _) => iconFont.Dispose();
        lookUndo = Btn("↶", "되돌리기 (Ctrl+Z)", () => { mainEditor.Focus(); mainEditor.Undo(); undone = true; SyncLookBar(); });
        lookRedo = Btn("↷", "다시 실행", () => { if (!undone) return; mainEditor.Focus(); mainEditor.Undo(); undone = false; SyncLookBar(); });
        lookUndo.Font = lookRedo.Font = iconFont;
        mainEditor.TextChanged += (_, _) => UpdateUndoState();
        mainEditor.KeyPress += (_, _) => undone = false;
        { var sp = new ToolStripSeparator(); lookBar.Items.Add(sp); lookSeps.Add(sp); }
        // 언어: 어느 글자에 이 글꼴을 쓸지 — 대표(한글 · 영문 모두) / 한글 / 영어. 영어에 따로 고르지 않으면 대표 글꼴을 쓴다.
        lookLangBox = Combo(76, "언어 — 글꼴을 적용할 글자");
        lookLangBox.Items.AddRange(new object[] { "대표", "한글", "영어" });
        lookLangBox.SelectedIndex = 0;
        lookLangBox.SelectedIndexChanged += (_, _) => { if (!syncingLook) SyncLookBar(); };
        lookFontBox = Combo(170, "글꼴 (이 파일만)");
        using (var fonts = new System.Drawing.Text.InstalledFontCollection())
            lookFontBox.Items.AddRange(fonts.Families.Select(f => f.Name).Cast<object>().ToArray());
        lookFontBox.DropDownWidth = 260;
        lookFontBox.SelectedIndexChanged += (_, _) =>
        {
            if (syncingLook || lookFontBox.SelectedItem is not string f) return;
            switch (lookLangBox!.SelectedIndex)
            {
                case 0: SetLook(r => { r.LookFont = f; r.LookFontLatin = null; }); break; // 대표: 모두 같은 글꼴
                case 1: SetLook(r => r.LookFont = f); break;
                default: SetLook(r => r.LookFontLatin = f); break;
            }
        };

        // 크기: "10.0 pt ⇅" — 값을 직접 쓰거나 화살표로 1pt씩(소수는 반올림해 정수 pt로 기억).
        lookSizeNum = new UnitUpDown { Unit = "pt", Minimum = 8, Maximum = 48, DecimalPlaces = 1, Increment = 1, Width = 80, BorderStyle = BorderStyle.FixedSingle };
        lookSizeNum.ValueChanged += (_, _) =>
        {
            if (!syncingLook) SetFontSize((int)Math.Round(lookSizeNum.Value));
        };
        lookSizeHost = new ToolStripControlHost(lookSizeNum) { AutoSize = false, Size = new Size(LogicalToDeviceUnits(80), lookSizeNum.Height), ToolTipText = "글자 크기 (이 파일만)" };
        lookBar.Items.Add(lookSizeHost);
        lookPtLabel = new ToolStripLabel("") { AutoSize = false, Width = 0 }; // 단위(pt)는 칸 안에 같이 보인다
        lookBar.Items.Add(lookPtLabel);

        lookLineNum = new UnitUpDown { Unit = "%", Minimum = 100, Maximum = 300, Increment = 5, Width = 80, BorderStyle = BorderStyle.FixedSingle };
        lookLineNum.ValueChanged += (_, _) => { if (!syncingLook) SetLook(r => { r.LookLine = (int)lookLineNum.Value; if (r.Para != null) { r.Para.LineMode = 0; r.Para.LinePercent = (int)lookLineNum.Value; } }); };
        lookLineHost = new ToolStripControlHost(lookLineNum) { AutoSize = false, Size = new Size(LogicalToDeviceUnits(80), lookLineNum.Height), ToolTipText = "줄 간격 (이 파일만 · 읽기 화면에 적용)" };
        lookBar.Items.Add(lookLineHost);
        lookLineUnit = new ToolStripLabel("") { AutoSize = false, Width = 0 }; // 단위(%)는 칸 안에 같이 보인다
        lookBar.Items.Add(lookLineUnit);

        { var sp = new ToolStripSeparator(); lookBar.Items.Add(sp); lookSeps.Add(sp); }
        lookJustify = Btn("", "양쪽 정렬 (읽기 화면에 적용)", () => SetLook(r => r.LookAlign = "justify"));
        lookLeft = Btn("", "왼쪽 정렬", () => SetLook(r => r.LookAlign = "left"));
        lookCenter = Btn("", "가운데 정렬", () => SetLook(r => r.LookAlign = "center"));
        lookRight = Btn("", "오른쪽 정렬", () => SetLook(r => r.LookAlign = "right"));
        lookDistribute = Btn("", "배분 정렬 (읽기 화면에 적용)", () => SetLook(r => { r.LookAlign = "distribute"; (r.Para ??= new ParaFormat()).Align = "distribute"; }));
        lookDivide = Btn("", "나눔 정렬 (읽기 화면에 적용)", () => SetLook(r => { r.LookAlign = "divide"; (r.Para ??= new ParaFormat()).Align = "divide"; }));
        lookPara = Btn("¶", "문단 서식", ShowParagraphDialog);
        lookBold = Btn("가", "진하게 (Ctrl+B) — 고른 글자 · 누른 자리부터", () => ApplyCharStyle(FontStyle.Bold));
        lookItalic = Btn("가", "기울임 (Ctrl+I) — 고른 글자 · 누른 자리부터", () => ApplyCharStyle(FontStyle.Italic));
        lookUnderline = Btn("가", "밑줄 (Ctrl+U) — 고른 글자 · 누른 자리부터", () => ApplyCharStyle(FontStyle.Underline));
        lookStrike = Btn("가", "취소선 — 고른 글자 · 누른 자리부터", () => ApplyCharStyle(FontStyle.Strikeout));
        lookColor = Btn("가", "글자 색 — 고른 글자 · 누른 자리부터", PickLookColor);
        lookBold.Font = new Font(NovelStore.UiFontFamily, 11f, FontStyle.Bold);
        lookItalic.Font = new Font(NovelStore.UiFontFamily, 11f, FontStyle.Italic);
        lookUnderline.Font = new Font(NovelStore.UiFontFamily, 11f, FontStyle.Underline);
        lookStrike.Font = new Font(NovelStore.UiFontFamily, 11f, FontStyle.Strikeout);
        lookColor.Font = new Font(NovelStore.UiFontFamily, 11f);
        foreach (var b in new[] { lookJustify, lookLeft, lookCenter, lookRight, lookDistribute, lookDivide }) { b.DisplayStyle = ToolStripItemDisplayStyle.Image; b.AutoSize = false; b.Size = new Size(LogicalToDeviceUnits(28), LogicalToDeviceUnits(26)); }
        var emoji = new Font("Segoe UI Emoji", 11f);
        lookPaper.Font = emoji;
        lookPaper.AutoSize = false; lookPaper.Size = new Size(LogicalToDeviceUnits(28), LogicalToDeviceUnits(26)); lookPaper.TextAlign = ContentAlignment.MiddleCenter; lookPaper.Padding = Padding.Empty; // 네모 칸 한가운데에
        Disposed += (_, _) => emoji.Dispose();
        MakeAlignIcons();

        // 네비게이션 바 열기/닫기 — 서식창 맨 앞(뒤에 구분선). 머리 막대 목차 버튼과 같은 메뉴 항목을 누른다.
        var navIconFont = new Font(IconFontName(), 12f);
        Disposed += (_, _) => navIconFont.Dispose();
        lookNav = Btn(GlyphToc, "네비게이션 바 열기/닫기", () => navMenuItem?.PerformClick());
        lookNav.Font = navIconFont;
        lookNav.Checked = settings.NavBarEnabled;
        { var sp = new ToolStripSeparator(); lookBar.Items.Add(sp); lookSeps.Add(sp); }
        { var sp = new ToolStripSeparator(); lookBar.Items.Add(sp); lookSeps.Add(sp); } // 낭독 앞 구분선(sepF5)
        lookTts = Btn(GlyphPlay, "낭독 · 자동 재생 설정 (바로 시작/정지: F5)", ShowPlayPopup);
        lookTts.Font = navIconFont;

        // 항목 사이는 3px(왼쪽 1 + 오른쪽 2). 편집 용지는 구분선에 3px, 글자 크기 칸과 pt는 1px.
        foreach (ToolStripItem item in lookBar.Items) item.Margin = new Padding(1, 2, 2, 3);
        lookPaper!.Margin = new Padding(0, 2, 2, 3);
        // 언어 · 글꼴 · 크기 · 줄 간격 사이는 8px(크기 칸과 pt는 그대로 1px).
        lookLangBox!.Margin = new Padding(1, 2, 4, 3);
        lookFontBox!.Margin = new Padding(4, 2, 4, 3);
        lookSizeHost!.Margin = new Padding(4, 2, 0, 3);
        lookPtLabel!.Margin = new Padding(1, 2, 4, 3);
        lookLineHost!.Margin = new Padding(4, 2, 0, 3);
        lookLineUnit!.Margin = new Padding(1, 2, 2, 3);
        lookBar.Padding = new Padding(LogicalToDeviceUnits(16), 3, LogicalToDeviceUnits(10), 3); // 왼쪽 벽에서는 그대로 띄운다

        // 서식 항목 묶음(설정 > 서식창 · 머리 막대에서 순서를 정한다)
        formatGroups["paper"] = new() { lookPaper! };
        formatGroups["hist"] = new() { lookUndo!, lookRedo! };
        formatGroups["lang"] = new() { lookLangBox! };
        formatGroups["font"] = new() { lookFontBox! };
        formatGroups["size"] = new() { lookSizeHost!, lookPtLabel! };
        formatGroups["line"] = new() { lookLineHost!, lookLineUnit! };
        formatGroups["align"] = new() { lookJustify!, lookLeft!, lookCenter!, lookRight!, lookDistribute!, lookDivide! };
        formatGroups["chars"] = new() { lookBold!, lookItalic!, lookUnderline!, lookStrike!, lookColor! };
        formatGroups["para"] = new() { lookPara! };
        formatGroups["fnav"] = new() { lookNav! };
        formatGroups["tts"] = new() { lookTts! };
        for (int i = 0; i < lookSeps.Count && i < 5; i++) formatGroups["sepF" + (i + 1)] = new() { lookSeps[i] };
        lookFormatItems.AddRange(lookBar.Items.Cast<ToolStripItem>());
        ApplyLookBarLayout();
        lookBar.Resize += (_, _) => CenterLookBar();
        lookBar.VisibleChanged += (_, _) => CenterLookBar();
        Controls.Add(lookBar);
    }

    /// <summary>서식 항목 묶음을 창 가운데로: 항목들이 차지하는 폭만큼을 빼고 남는 폭을 양쪽 여백으로 나눈다(좁으면 왼쪽 기본 여백).</summary>
    private bool centeringLook;
    private void CenterLookBar()
    {
        if (lookBar == null || centeringLook) return;
        centeringLook = true;
        try
        {
            int items = lookBar.Items.Cast<ToolStripItem>().Where(i => i.Available).Sum(i => i.Width + i.Margin.Horizontal);
            int min = LogicalToDeviceUnits(16);
            int left = Math.Max(min, (lookBar.ClientSize.Width - items) / 2);
            if (lookBar.Padding.Left != left) lookBar.Padding = new Padding(left, lookBar.Padding.Top, lookBar.Padding.Right, lookBar.Padding.Bottom);
        }
        finally { centeringLook = false; }
    }

    /// <summary>서식창은 글(txt · epub)일 때 + 설정에서 켜 두었을 때만 보인다.</summary>
    private void ApplyLookBarShown()
    {
        // 참고 자료는 종류(txt · epub · 만화 · pdf)와 상관없이 참고 자료 서식창(MainForm.RefBar.cs)을 쓰고,
        // 에피소드(내 원고)의 txt만 편집용 서식창을 쓴다. epub · 만화는 보통 읽기 서식창이다.
        bool refDoc = IsReferenceFile(currentPath);
        if (lookBar != null) lookBar.Visible = settings.ShowFormatBar && shownKind is ContentKind.Txt && !refDoc;
        EnsureReadBar();
        if (readBar != null)
        {
            readBar.Visible = shownKind is ContentKind.Epub or ContentKind.Image || (refDoc && shownKind != null && shownKind != ContentKind.Pdf); // PDF는 서식창 없이(뷰어 자체 툴바를 쓴다)
            SyncRefBar(refDoc);
        }
    }

    /// <summary>epub · 만화의 읽기 전용 서식창. 예전 머리 막대에 있던 네비게이션 · 수정하기 · 보기 설정 · 자동 재생 버튼을 그대로 옮겨 온다(처음 한 번만 만든다).</summary>
    private void EnsureReadBar()
    {
        if (readBar != null || lookBar == null || !toolbarItems.ContainsKey("nav")) return;
        readBar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Visible = false,
            Padding = new Padding(LogicalToDeviceUnits(16), 2, 6, 2),
            ShowItemToolTips = true,
        };
        // txt 서식창과 같은 높이(글꼴 상자가 들어가는 높이)로 맞춘다.
        readBar.AutoSize = false;
        readBar.Height = Math.Max(lookBar.GetPreferredSize(Size.Empty).Height, LogicalToDeviceUnits(34));
        readBar.Padding = new Padding(LogicalToDeviceUnits(16), 3, 6, 3);
        foreach (var key in new[] { "nav", "edit", "look", "play" })
            if (toolbarItems.TryGetValue(key, out var it)) it.Owner?.Items.Remove(it); // 머리 막대 메뉴에서 떼어 낸다
        readBar.Resize += (_, _) => CenterReadBar();
        readBar.VisibleChanged += (_, _) => CenterReadBar();
        Controls.Add(readBar);
        Controls.SetChildIndex(readBar, Controls.GetChildIndex(lookBar));
        ApplyReadBarLayout();
        if (theme != null) ApplyLookBarTheme();
    }

    private readonly Dictionary<string, ToolStripSeparator> readSeps = new();

    /// <summary>설정 > 서식창(epub · 만화)에서 정한 순서대로 읽기 전용 서식창 항목을 늘어놓고, 뺀 항목은 감춘다. 구분선(sepR · 직접 추가한 sepX)은 필요할 때 만든다.</summary>
    private void ApplyReadBarLayout()
    {
        if (readBar == null) return;
        if (refBarActive) { ApplyRefBarLayout(); return; }
        if (toolbarViewMode != null) toolbarViewMode.Enabled = true; // 참고 자료 서식창에서 회색으로 뒀던 걸 되돌린다
        readBar.SuspendLayout();
        foreach (var old in readBar.Items.Cast<ToolStripItem>().ToList()) readBar.Items.Remove(old);
        foreach (var key in ToolbarReadOrder(settings))
        {
            ToolStripItem? it;
            if (key.StartsWith("sep"))
            {
                if (!readSeps.TryGetValue(key, out var sp)) readSeps[key] = sp = new ToolStripSeparator();
                it = sp;
            }
            else if (!toolbarItems.TryGetValue(key, out it)) continue;
            it.Alignment = ToolStripItemAlignment.Left;
            it.Visible = !settings.ToolbarHidden.Contains(key);
            readBar.Items.Add(it);
        }
        readBar.ResumeLayout(true);
        CenterReadBar();
    }

    /// <summary>읽기 전용 서식창도 txt 서식창처럼 항목 묶음을 창 가운데에 놓는다(좁으면 왼쪽 기본 여백).</summary>
    private bool centeringRead;
    private void CenterReadBar()
    {
        if (readBar == null || centeringRead) return;
        centeringRead = true;
        try
        {
            int items = readBar.Items.Cast<ToolStripItem>().Where(i => i.Available).Sum(i => i.Width + i.Margin.Horizontal);
            int left = Math.Max(LogicalToDeviceUnits(16), (readBar.ClientSize.Width - items) / 2);
            if (readBar.Padding.Left != left) readBar.Padding = new Padding(left, readBar.Padding.Top, readBar.Padding.Right, readBar.Padding.Bottom);
        }
        finally { centeringRead = false; }
    }

    /// <summary>설정 > 서식창 · 머리 막대에서 정한 순서대로 서식 항목을 다시 늘어놓고, 뺀 항목은 감춘다.</summary>
    private void ApplyLookBarLayout()
    {
        if (lookBar == null || formatGroups.Count == 0) return;
        lookBar.SuspendLayout();
        int at = 0;
        var order = ToolbarLeftOrder(settings);
        // 설정에서 지운 사용자 구분선은 서식창에서도 뗀다.
        foreach (var gone in formatGroups.Keys.Where(k => IsCustomSep(k) && !order.Contains(k)).ToList())
        {
            foreach (var i in formatGroups[gone]) lookBar.Items.Remove(i);
            formatGroups.Remove(gone);
        }
        foreach (var key in order)
        {
            // 설정에서 새로 추가한 구분선은 처음 나올 때 만든다.
            if (IsCustomSep(key) && !formatGroups.ContainsKey(key))
                formatGroups[key] = new() { new ToolStripSeparator { Margin = new Padding(1, 2, 2, 3) } };
            if (!formatGroups.TryGetValue(key, out var group)) continue;
            bool visible = !settings.ToolbarHidden.Contains(key);
            foreach (var i in group)
            {
                lookBar.Items.Remove(i);
                lookBar.Items.Insert(Math.Min(at++, lookBar.Items.Count), i);
                i.Visible = visible;
            }
        }
        lookBar.ResumeLayout(true);
        CenterLookBar();
    }

    // ───────────────────────── 화면 배율 (Ctrl + / Ctrl - / Ctrl 휠 / Ctrl+0) ─────────────────────────
    // 글자 크기(pt)는 그대로 두고 화면만 키우고 줄인다. 웹 화면은 WebView 배율로, 쓰는 화면은 글꼴을 배율만큼 곱해 그린다. 파일마다 기억한다.

    private static readonly int[] ZoomSteps = { 50, 60, 70, 80, 90, 100, 110, 120, 130, 150, 175, 200, 250, 300 };
    private int ViewZoom => Math.Clamp(lookRec?.LookZoom ?? 100, 50, 300);

    private void ViewZoomBy(int dir)
    {
        int cur = ViewZoom;
        int next = dir > 0 ? ZoomSteps.FirstOrDefault(z => z > cur, 300) : ZoomSteps.LastOrDefault(z => z < cur, 50);
        SetViewZoom(next);
    }

    private void ViewZoomReset() => SetViewZoom(100);

    private void SetViewZoom(int percent)
    {
        if (lookRec == null) return;
        lookRec.LookZoom = percent == 100 ? null : percent;
        library.MarkDirty();
        ApplyViewZoom();
        if (CanZoomText) ShowZoomBubble($"{percent}%");
    }

    private void ApplyViewZoom()
    {
        // 글꼴 · 크기는 바뀔 때만 다시 지정한다(다시 지정하면 글자마다 준 서식이 지워진다). 배율은 글꼴 크기를 바꾸지 않고 ZoomFactor로 건다.
        if (mainEditor.Font.Name != EffFont || Math.Abs(mainEditor.Font.SizeInPoints - fontSize) > 0.01f) mainEditor.Font = new Font(EffFont, fontSize);
        try { mainEditor.ZoomFactor = Math.Clamp(ViewZoom / 100f, 0.2f, 5f); } catch { }
        ApplySideMargin();
        try { if (epubView != null) epubView.ZoomFactor = ViewZoom / 100.0; } catch { /* 웹 화면이 아직 준비 안 됐으면 문서를 불러올 때 다시 맞춘다 */ }
        statusKeyZoom = -1; // 상태 줄의 배율 표시를 다시 그리게
    }

    /// <summary>이 파일의 서식 값을 바꾸고 기억한 뒤 화면에 반영한다. 열린 파일이 없으면 무시한다.</summary>
    private void SetLook(Action<BookRecord> change)
    {
        if (lookRec == null) return;
        change(lookRec);
        library.MarkDirty();
        library.SaveIfDirty();
        ApplyLook();
    }

    /// <summary>지금 파일의 서식(없으면 설정창 기본값)을 본문 · 웹 화면 · 서식 줄에 모두 맞춘다.</summary>
    private void ApplyLook()
    {
        fontSize = EffSize;
        ApplyViewZoom();
        mainEditor.ApplyParaFormat(EditorPara()); // 정렬 · 여백 · 들여쓰기/내어쓰기 · 줄/문단 간격을 글 칸에도 적용
        ApplyMargins(); // 편집 용지의 위아래 · 좌우 여백
        foreach (var item in fontSizeMenuItems) item.Checked = item.Text == $"{(int)fontSize}pt";
        ApplyEditorWrap();
        if (epubMode) ApplyEpubFontSize(keepPlace: true);
        SyncLookBar();
    }

    private void SyncLookBar()
    {
        if (lookBar == null) return;
        syncingLook = true;
        try
        {
            string shown = lookLangBox!.SelectedIndex == 2 ? (lookRec?.LookFontLatin ?? EffFont) : EffFont;
            lookFontBox!.SelectedItem = shown;
            if (lookFontBox.SelectedItem == null) lookFontBox.Text = shown;
            lookSizeNum!.Value = EffSize;
            lookLineNum!.Value = Math.Clamp(EffLine, 100, 300);
            lookJustify!.Checked = EffAlign == "justify";
            lookLeft!.Checked = EffAlign == "left";
            lookCenter!.Checked = EffAlign == "center";
            lookRight!.Checked = EffAlign == "right";
            lookDistribute!.Checked = EffAlign == "distribute";
            lookDivide!.Checked = EffAlign == "divide";
            SyncCharButtons();
            lookPaper!.Checked = false; // 편집 용지는 켜고 끄는 버튼이 아니라 설정이라, 눌린 네모 표시를 하지 않는다(용지가 바뀌었는지는 툴팁으로)
            bool has = lookRec != null;
            foreach (var i in lookFormatItems) i.Enabled = has;
            bool editing = has && editorHost.Visible && !mainEditor.ReadOnly;
            lookUndo!.Enabled = editing && mainEditor.CanUndo;
            lookRedo!.Enabled = editing && undone;
        }
        finally { syncingLook = false; }
    }

    private void ApplyLookBarTheme()
    {
        if (readBar != null) { readBar.BackColor = theme.Chrome; readBar.ForeColor = theme.ChromeFore; }
        if (marginHost?.Control is { } mp)
        {
            mp.ForeColor = theme.ChromeFore;
            foreach (var c in mp.Controls.OfType<NumericUpDown>()) { c.BackColor = theme.Back; c.ForeColor = theme.Fore; }
        }
        if (lookBar == null) return;
        lookBar.BackColor = theme.Chrome;
        lookBar.ForeColor = theme.ChromeFore;
        foreach (var c in new[] { lookLangBox, lookFontBox })
        {
            if (c == null) continue;
            c.BackColor = theme.Back;
            c.ForeColor = theme.Fore;
        }
        if (lookSizeNum != null) { lookSizeNum.BackColor = theme.Back; lookSizeNum.ForeColor = theme.Fore; }
        if (lookLineNum != null) { lookLineNum.BackColor = theme.Back; lookLineNum.ForeColor = theme.Fore; }
        MakeAlignIcons();
    }

    private void UpdateUndoState()
    {
        if (lookUndo == null || lookRedo == null) return;
        bool editing = lookRec != null && editorHost.Visible && !mainEditor.ReadOnly;
        lookUndo.Enabled = editing && mainEditor.CanUndo;
        lookRedo.Enabled = editing && undone;
    }

    /// <summary>정렬 버튼 아이콘: 한글처럼 가는 줄 다섯 개로 정렬 모양을 그린다(양쪽 · 왼쪽 · 가운데 · 오른쪽).</summary>
    private void MakeAlignIcons()
    {
        if (lookLeft == null || lookCenter == null || lookRight == null || lookJustify == null || lookDistribute == null || lookDivide == null) return;
        int s = LogicalToDeviceUnits(16);
        Color ink = theme?.ChromeFore ?? SystemColors.ControlText;
        Bitmap Draw(string kind)
        {
            var bmp = new Bitmap(s, s);
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(ink, Math.Max(1f, s / 14f));
            for (int i = 0; i < 5; i++)
            {
                float y = s * (2.5f + i * 2.8f) / 16f;
                float full = s - 3f, w = (i % 2 == 0 || kind is "justify" or "distribute" or "divide") ? full : full * 0.6f;
                if (kind == "distribute" && i % 2 == 1) { g.DrawLine(pen, 1.5f, y, 1.5f + full * 0.3f, y); g.DrawLine(pen, 1.5f + full * 0.7f, y, 1.5f + full, y); continue; }
                if (kind == "divide" && i == 4) { for (float d = 0; d < full; d += s / 5f) g.DrawLine(pen, 1.5f + d, y, 1.5f + d + s / 10f, y); continue; }
                float x = kind switch { "center" => (s - w) / 2f, "right" => s - 1.5f - w, _ => 1.5f };
                g.DrawLine(pen, x, y, x + w, y);
            }
            return bmp;
        }
        foreach (var (btn, kind) in new[] { (lookJustify, "justify"), (lookLeft, "left"), (lookCenter, "center"), (lookRight, "right"), (lookDistribute, "distribute"), (lookDivide, "divide") })
        {
            var old = btn.Image;
            btn.Image = Draw(kind);
            old?.Dispose();
        }
    }
}
