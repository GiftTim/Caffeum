namespace CoffeeViewer;

/// <summary>
/// 참고 자료 문서(작품의 "참고 자료" 폴더 안 파일)의 서식창. 종류(txt · epub · 만화 · pdf)와 상관없이 같은 줄을 쓴다:
/// [파일 추가] | [네비게이션 바] | [용지 편집] | [한쪽/두쪽 보기] [보기 모드] [스크롤/페이지/페이지 터치] | [낭독/슬라이드] [수정하기] | [항상 위] [잠금 모드] [몰입] [설정]
/// (읽기 서식창의 [여백 상 하 좌 우] 칸 대신 [용지 편집]이 들어간다.) 항목 순서는 고정이고, 문서 종류에 안 맞는 버튼은 감춘다.
/// </summary>
public partial class MainForm
{
    /// <summary>참고 자료 서식창 항목의 순서(왼쪽 → 오른쪽). sep로 시작하는 건 구분선.</summary>
    internal static readonly string[] RefBarDefault =
        { "addref", "sepA", "nav", "sepB", "refpaper", "sepC", "vlayout", "order", "vmode", "rmode", "sepD", "play", "edit", "sepE", "pip", "pin", "lock", "immersion", "settings" };

    /// <summary>설정 > 참고파일 서식창에서 정한 순서(저장된 순서에 없는 항목은 기본 자리 뒤에 붙는다).</summary>
    internal static List<string> ToolbarRefOrder(AppSettings s)
    {
        var order = s.ToolbarRef.Where(k => RefBarDefault.Contains(k) || IsCustomSep(k)).Distinct().ToList();
        order.AddRange(RefBarDefault.Where(k => !order.Contains(k)));
        return order;
    }

    /// <summary>설정 > 참고파일 서식창 목록에 보이는 항목 이름.</summary>
    internal static string RefBarLabel(string key) => key switch
    {
        "addref" => "파일 추가",
        "nav" => "네비게이션 바",
        "refpaper" => "용지 편집",
        "vlayout" => "한쪽 · 두쪽 보기 (만화)",
        "order" => "순서 · 나누기 (만화)",
        "vmode" => "보기 모드",
        "rmode" => "스크롤 · 페이지 · 페이지 터치",
        "play" => "낭독 · 슬라이드",
        "edit" => "줄바꿈 · 줄간격",
        "pin" => "항상 위",
        "lock" => "잠금 모드 열기/닫기",
        "pip" => "PIP 모드 (작은 창으로 띄우기)",
        "immersion" => "몰입",
        "settings" => "설정",
        _ => ToolbarLabel(key),
    };

    /// <summary>[파일 추가]를 눌렀다(서재 쪽이 참고 자료 넣기 창을 띄운다).</summary>
    internal event Action? AddReferenceRequested;

    /// <summary>[PIP 모드]를 눌렀다(서재 쪽이 이 참고 자료를 작은 창으로 띄우고 칸의 탭은 닫는다).</summary>
    internal event Action<string>? PipRequested;

    /// <summary>PIP 작은 창 안에서 열린 문서인가(그 안에서는 [PIP] · [파일 추가] · [항상 위] 버튼을 숨긴다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool InPip { get; set; }

    private ToolStripMenuItem? refAdd, refPaper, refReadMode, refLock, refPip, refOrder;
    private Color orderIconColor;
    private Color pipIconColor;
    private readonly Dictionary<string, ToolStripSeparator> refSeps = new();
    private bool refBarActive;
    private readonly Dictionary<ToolStripItem, string> refBarKeys = new(); // 서식창에 올린 항목 → 키(보일지 정할 때 쓴다)
    private bool lockMode;
    private bool lockSavedTapToTurn;

    /// <summary>참고 자료 서식창에만 쓰는 버튼들을 처음 한 번 만든다.</summary>
    private void EnsureRefItems()
    {
        if (refAdd != null) return;
        refAdd = IconItem("", "참고 자료 파일 추가", () => AddReferenceRequested?.Invoke());
        refPaper = IconItem("", "용지 편집 — 판형 · 방향 · 여백 (글 문서)", () => { if (shownKind == ContentKind.Image) ShowMarginDrop(); else OpenPaperSetup(); }); // 만화는 누르면 여백 칸이 밑으로 펼쳐진다(ShowMarginDrop)
        refLock = IconItem("", "잠금 모드 열기/닫기 — 잠그면 수정하기 · 화면 터치 넘김이 막혀요", ToggleLockMode);
        refPip = new ToolStripMenuItem { DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None, ToolTipText = "PIP 모드 — 이 참고 자료를 항상 위에 뜨는 작은 창으로 띄워요", AutoToolTip = false, Padding = new Padding(6, 1, 6, 1) };
        refPip.Click += (_, _) => { if (!string.IsNullOrEmpty(currentPath)) PipRequested?.Invoke(currentPath); };
        refOrder = new ToolStripMenuItem { DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None, ToolTipText = "순서 · 나누기 — 만화 읽는 순서(왼→오 · 오→왼)와 한 장을 둘로 나누기 (만화에서만)", AutoToolTip = false, Padding = new Padding(6, 1, 6, 1) };
        refOrder.Click += (_, _) => ShowImageOrderMenu();
        RefreshOrderIcon();
        RefreshPipIcon();
    }

    /// <summary>잠금 모드: 읽다가 실수로 글을 고치거나 화면을 눌러 쪽이 넘어가는 걸 막는다(스크롤 · 키 이동은 그대로).</summary>
    private void ToggleLockMode()
    {
        lockMode = !lockMode;
        if (lockMode)
        {
            lockSavedTapToTurn = tapToTurnEnabled;
            if (!mainEditor.ReadOnly) SetEditMode(false); // 고치는 중이었다면 저장하고 읽기로
            tapToTurnEnabled = false;
        }
        else tapToTurnEnabled = lockSavedTapToTurn;
        imageCanvas.TapToTurnEnabled = tapToTurnEnabled;
        SetLockUi(lockMode);
        if (refLock != null) { refLock.Checked = lockMode; refLock.Text = lockMode ? "" : ""; }
        editModeMenuItem.Enabled = !lockMode && shownKind is ContentKind.Txt or ContentKind.Epub;
        ShowToast(lockMode ? "잠금 모드 — 터치 · 페이지 · 스크롤 · 수정이 모두 잠겼어요" : "잠금 모드를 풀었어요");
    }

    private ToolStripDropDown? marginDrop;

    /// <summary>만화의 [용지 편집]: 읽기 서식창의 [여백 상 하 좌 우] 칸을 버튼 바로 밑에 직접 펼친다.
    /// (버튼에 드롭다운으로 달아 두면 처음 열 때 위치를 못 잡고 화면 왼쪽 위로 가서, 자리를 직접 계산해 띄운다.)</summary>
    private void ShowMarginDrop()
    {
        if (marginHost == null || refPaper == null || readBar == null) return;
        marginDrop ??= new ToolStripDropDown { AutoClose = true, Font = Font };
        if (!marginDrop.Items.Contains(marginHost)) marginDrop.Items.Add(marginHost);
        SyncMarginBoxes();
        var at = readBar.PointToScreen(new Point(refPaper.Bounds.Left, readBar.Height));
        marginDrop.Show(at);
    }

    /// <summary>읽기 서식창을 참고 자료용(true)이나 보통(epub · 만화, false)으로 바꿔 둔다.</summary>
    private void SyncRefBar(bool refDoc)
    {
        if (readBar == null) return;
        if (refDoc != refBarActive)
        {
            refBarActive = refDoc;
            ApplyReadBarLayout();
        }
        else if (refDoc) SyncRefBarKind();
    }

    /// <summary>참고 자료 서식창을 늘어놓는다(보통 읽기 서식창의 설정 순서 · 숨김은 따르지 않는다).</summary>
    private void ApplyRefBarLayout()
    {
        if (readBar == null) return;
        EnsureRefItems();
        readBar.SuspendLayout();
        foreach (var old in readBar.Items.Cast<ToolStripItem>().ToList()) readBar.Items.Remove(old);
        foreach (var key in ToolbarRefOrder(settings))
        {
            ToolStripItem? it;
            if (key.StartsWith("sep"))
            {
                if (!refSeps.TryGetValue(key, out var sp)) refSeps[key] = sp = new ToolStripSeparator();
                it = sp;
            }
            else
            {
                it = key switch
                {
                    "addref" => refAdd,
                    "refpaper" => refPaper,
                    "rmode" => refReadMode,
                    "lock" => refLock,
                    "pip" => refPip,
                    "order" => refOrder,
                    _ => toolbarItems.TryGetValue(key, out var found) ? found : null,
                };
                if (it == null) continue;
            }
            it.Owner?.Items.Remove(it); // 항상 위 · 몰입 · 설정은 머리 막대(메뉴 줄)에 있던 걸 가져온다
            it.Alignment = ToolStripItemAlignment.Left;
            it.Tag ??= null;
            refBarKeys[it] = key;
            readBar.Items.Add(it);
        }
        readBar.ResumeLayout(true);
        SyncRefBarKind();
    }

    /// <summary>지금 열린 문서 종류에 안 맞는 버튼은 감추고, 설정 > 참고파일 서식창에서 뺀 항목도 감춘다.</summary>
    private void SyncRefBarKind()
    {
        if (!refBarActive || refAdd == null) return;
        bool img = shownKind == ContentKind.Image, pdf = shownKind == ContentKind.Pdf, text = shownKind is ContentKind.Txt or ContentKind.Epub;
        if (refPaper != null)
        {
            // 글 문서: 용지 편집 창 / 만화: 용지는 없으니 [여백 상 하 좌 우] 칸만 밑으로 펼쳐 쓴다(읽기 서식창의 그 칸을 그대로 가져온다)
            refPaper.Enabled = (text || img) && !lockMode;
            refPaper.ToolTipText = img ? "여백 — 상 · 하 · 좌 · 우(px)" : "용지 편집 — 판형 · 방향 · 여백 (글 문서)";
        }
        bool KindOk(string key) => key switch
        {
            "addref" => !InPip,   // PIP 작은 창에는 서재 칸이 없어 [파일 추가]는 숨긴다. [항상 위]는 PIP에서도 쓴다(기본은 꺼짐)
            "vlayout" => img || text,           // 한쪽 · 두쪽은 만화와 글(txt · epub)에서
            "order" => true,                    // 순서 · 나누기: 늘 보이고, 글 문서에서는 눌리지 않는다
            "vmode" => img || text,             // 보기 모드는 만화에서 쓰이고, 글(txt · epub)에서는 버튼만 보이게(회색)
            "rmode" or "play" => !pdf,           // 읽기 방식 · 낭독/슬라이드는 pdf 뷰어에 없다
            // 수정하기는 모든 종류에서 보이고, 글 문서가 아니면 눌리지 않게 회색으로만 둔다(editModeMenuItem.Enabled가 이미 그렇게 맞춰 준다)
            _ => true,
        };
        foreach (var (item, key) in refBarKeys)
        {
            if (item.Owner != readBar) continue;
            item.Visible = KindOk(key) && !settings.ToolbarRefHidden.Contains(key);
            if (key == "order") item.Enabled = img && !lockMode; // 만화에서만 눌린다(글 문서에서는 아이콘만 보인다)
            if (key == "pip") item.Enabled = !InPip; // 이미 PIP 모드로 열린 창에서는 PIP 해제를 못 하게 막아 둔다
            if (ReferenceEquals(item, toolbarViewMode)) item.Enabled = img && !lockMode; // 보기 모드(원본 크기 · 꽉차게 …)는 만화 것 — 글 문서에서는 버튼만 보이고 회색
            else if (key is "rmode" or "nav") item.Enabled = !lockMode;
            else if (key == "play") item.Enabled = !lockMode && (autoPlayToggleMenuItem?.Enabled ?? true);
        }
        RefreshOrderIcon();
        RefreshPipIcon();
        CenterReadBar();
    }
    /// <summary>PIP 아이콘(큰 네모 안의 ↘ 화살표 + 오른쪽 아래의 작은 꽉 찬 네모)을 서식창 글자색으로 그려 둔다. 글자색이 바뀌면 다시 그린다.</summary>
    /// <summary>[순서 · 나누기] 아이콘: 펼친 책 위에 1 2 3. 서식창 글자색으로 그려 둔다.</summary>
    private void RefreshOrderIcon()
    {
        if (refOrder == null) return;
        var c = readBar?.ForeColor ?? SystemColors.ControlText;
        if (c == orderIconColor && refOrder.Image != null) return;
        orderIconColor = c;
        int s = LogicalToDeviceUnits(22);
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            float u = s / 22f;
            using var pen = new Pen(c, 0.9f * u) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            // 펼친 책: 양쪽 쪽(네모) 둘 + 가운데 책등
            g.DrawRectangle(pen, 2.5f * u, 9.5f * u, 8.5f * u, 9f * u);
            g.DrawRectangle(pen, 11f * u, 9.5f * u, 8.5f * u, 9f * u);
            // 위의 1 2 3
            using var f = new Font(NovelStore.UiFontFamily, 6.2f * u, FontStyle.Bold, GraphicsUnit.Pixel);
            using var b = new SolidBrush(c);
            g.DrawString("1", f, b, 4.2f * u, 0.6f * u);
            g.DrawString("2", f, b, 9.4f * u, 0.6f * u);
            g.DrawString("3", f, b, 14.6f * u, 0.6f * u);
        }
        var old = refOrder.Image;
        refOrder.Image = bmp;
        old?.Dispose();
    }

    /// <summary>순서 · 나누기 목록(만화): 읽는 순서와 한 장을 둘로 나누기. 메뉴에 있는 같은 항목들을 그대로 쓴다.</summary>
    private void ShowImageOrderMenu()
    {
        if (shownKind != ContentKind.Image) return;
        var m = new ContextMenuStrip { Font = orderMenu.Font };
        foreach (var o in imageOrderMenuItems)
        {
            var src = o;
            var it = new ToolStripMenuItem(src.Text, null, (_, _) => src.PerformClick()) { Checked = src.Checked };
            m.Items.Add(it);
        }
        m.Show(readBar!, new Point(refOrder!.Bounds.Left, refOrder.Bounds.Bottom + 2));
    }

    private void RefreshPipIcon()
    {
        if (refPip == null) return;
        var c = readBar?.ForeColor ?? SystemColors.ControlText;
        if (c == pipIconColor && refPip.Image != null) return;
        pipIconColor = c;
        int s = LogicalToDeviceUnits(22);
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float u = s / 22f;
            using var pen = new Pen(c, 0.85f * u) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            using (var frame = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float r = 3f * u;
                var rc = new RectangleF(2f * u, 3f * u, 17f * u, 13f * u);
                frame.AddArc(rc.X, rc.Y, r * 2, r * 2, 180, 90);
                frame.AddArc(rc.Right - r * 2, rc.Y, r * 2, r * 2, 270, 90);
                frame.AddArc(rc.Right - r * 2, rc.Bottom - r * 2, r * 2, r * 2, 0, 90);
                frame.AddArc(rc.X, rc.Bottom - r * 2, r * 2, r * 2, 90, 90);
                frame.CloseFigure();
                g.DrawPath(pen, frame);
            }
            // 오른쪽 아래 작은 네모 둘레의 틀을 비워서(투명) 꽉 찬 네모가 또렷하게 보이게
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 9.5f * u, 10.5f * u, 12.5f * u, 10f * u);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using (var fill = new SolidBrush(c))
            using (var small = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float r = 2f * u;
                var rc = new RectangleF(11.5f * u, 12.5f * u, 9f * u, 6f * u);
                small.AddArc(rc.X, rc.Y, r * 2, r * 2, 180, 90);
                small.AddArc(rc.Right - r * 2, rc.Y, r * 2, r * 2, 270, 90);
                small.AddArc(rc.Right - r * 2, rc.Bottom - r * 2, r * 2, r * 2, 0, 90);
                small.AddArc(rc.X, rc.Bottom - r * 2, r * 2, r * 2, 90, 90);
                small.CloseFigure();
                g.FillPath(fill, small);
            }
            // 안쪽 ↘ 화살표
            g.DrawLine(pen, 6.5f * u, 6.5f * u, 11.5f * u, 11.5f * u);
            g.DrawLine(pen, 11.5f * u, 8f * u, 11.5f * u, 11.5f * u);
            g.DrawLine(pen, 8f * u, 11.5f * u, 11.5f * u, 11.5f * u);
        }
        var old = refPip.Image;
        refPip.Image = bmp;
        old?.Dispose();
    }
}
