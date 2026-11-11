namespace CoffeeViewer;

/// <summary>
/// epub · 만화 읽기 전용 서식창의 [여백 상 하 좌 우] 칸과, 지금 보기 모드를 글자로 보여 주는 [페이지] 버튼.
/// 여백 값은 설정(AppSettings)에 따로 저장한다(비어 있으면 설정창의 상하·좌우 값을 그대로 쓴다).
/// </summary>
public partial class MainForm
{
    private NumericUpDown? marginTopBox, marginBottomBox, marginLeftBox, marginRightBox;
    private ToolStripControlHost? marginHost;
    private readonly System.Windows.Forms.Timer marginApplyTimer = new() { Interval = 350 };
    private bool syncingMargins;
    private bool marginBoxesForImage;
    private bool marginBoxesFilled;

    /// <summary>웹 화면(epub · 웹으로 보는 txt)에서 쓸 여백(px). epub만 서식창 값을 따른다.</summary>
    private (int T, int B, int L, int R) WebMargins()
    {
        if (epubMode && !txtWeb)
            return (settings.EpubMarginTop ?? settings.VerticalMargin, settings.EpubMarginBottom ?? settings.VerticalMargin,
                    settings.EpubMarginLeft ?? settings.SideMargin, settings.EpubMarginRight ?? settings.SideMargin);
        return (settings.VerticalMargin, settings.VerticalMargin, settings.SideMargin, settings.SideMargin);
    }

    /// <summary>만화 여백(px).</summary>
    private (int T, int B, int L, int R) ImageMargins() =>
        (settings.ImageMarginTop ?? settings.ImageVerticalMargin, settings.ImageMarginBottom ?? settings.ImageVerticalMargin,
         settings.ImageMarginLeft ?? settings.ImageSideMargin, settings.ImageMarginRight ?? settings.ImageSideMargin);

    private void ApplyImageMargins()
    {
        var m = ImageMargins();
        imageCanvas.SetMargins(m.L, m.R, m.T, m.B);
    }

    /// <summary>[여백 상 하 좌 우] 칸을 만들어 toolbarItems["margins"]에 등록한다(한 번만).</summary>
    private void InitMarginBar()
    {
        if (marginHost != null) return;
        var tips = new ToolTip();
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        NumericUpDown Box(string name, string tip)
        {
            var lbl = new Label { Text = name, AutoSize = true, Margin = new Padding(6, 5, 1, 0), BackColor = Color.Transparent };
            var box = new NumericUpDown { Minimum = 0, Maximum = 400, Increment = 4, Width = LogicalToDeviceUnits(56), Margin = new Padding(0, 1, 2, 1), BorderStyle = BorderStyle.FixedSingle };
            tips.SetToolTip(box, tip);
            tips.SetToolTip(lbl, tip);
            box.ValueChanged += (_, _) => { if (!syncingMargins) { marginApplyTimer.Stop(); marginApplyTimer.Start(); } };
            panel.Controls.Add(lbl);
            panel.Controls.Add(box);
            return box;
        }
        panel.Controls.Add(new Label { Text = "여백", AutoSize = true, Margin = new Padding(0, 5, 2, 0), BackColor = Color.Transparent });
        marginTopBox = Box("상", "위쪽 여백(px)");
        marginBottomBox = Box("하", "아래쪽 여백(px)");
        marginLeftBox = Box("좌", "왼쪽 여백(px)");
        marginRightBox = Box("우", "오른쪽 여백(px)");
        marginHost = new ToolStripControlHost(panel) { ToolTipText = "여백 — 상 · 하 · 좌 · 우(px)", Margin = new Padding(1, 0, 2, 0) };
        toolbarItems["margins"] = marginHost;
        // [보기 방식]은 목록이 열리지 않는 일반 단추로 바꿔 단다(메뉴 항목은 눌렀을 때 목록 채우기가 되살아난다).
        layoutButton = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None, ToolTipText = "한쪽 보기 — 누르면 두쪽 보기" };
        layoutButton.Click += (_, _) => OnViewLayoutClick();
        toolbarItems["vlayout"] = layoutButton;

        marginApplyTimer.Tick += (_, _) => { marginApplyTimer.Stop(); CommitMargins(); };
        FormClosed += (_, _) => { marginApplyTimer.Dispose(); tips.Dispose(); };
    }

    /// <summary>칸의 값을 설정에 저장하고 지금 화면에 반영한다(만화: 바로, epub: 지금 자리에서 다시 배치).</summary>
    private void CommitMargins()
    {
        if (marginTopBox == null || marginBottomBox == null || marginLeftBox == null || marginRightBox == null) return;
        int t = (int)marginTopBox.Value, b = (int)marginBottomBox.Value, l = (int)marginLeftBox.Value, r = (int)marginRightBox.Value;
        if (marginBoxesForImage)
        {
            settings.ImageMarginTop = t; settings.ImageMarginBottom = b; settings.ImageMarginLeft = l; settings.ImageMarginRight = r;
            settings.Save();
            ApplyImageMargins();
        }
        else
        {
            settings.EpubMarginTop = t; settings.EpubMarginBottom = b; settings.EpubMarginLeft = l; settings.EpubMarginRight = r;
            settings.Save();
            if (epubMode) ApplyEpubLook();
        }
    }

    /// <summary>지금 보는 종류(만화 / epub)에 맞는 값을 칸에 채운다.</summary>
    private void SyncMarginBoxes()
    {
        if (marginTopBox == null || marginBottomBox == null || marginLeftBox == null || marginRightBox == null) return;
        bool forImage = shownKind == ContentKind.Image;
        if (marginBoxesFilled && forImage == marginBoxesForImage) return;
        marginBoxesForImage = forImage;
        marginBoxesFilled = true;
        var m = forImage ? ImageMargins() : (T: settings.EpubMarginTop ?? settings.VerticalMargin, B: settings.EpubMarginBottom ?? settings.VerticalMargin,
                                              L: settings.EpubMarginLeft ?? settings.SideMargin, R: settings.EpubMarginRight ?? settings.SideMargin);
        syncingMargins = true;
        try
        {
            marginTopBox.Value = Math.Clamp(m.T, 0, 400);
            marginBottomBox.Value = Math.Clamp(m.B, 0, 400);
            marginLeftBox.Value = Math.Clamp(m.L, 0, 400);
            marginRightBox.Value = Math.Clamp(m.R, 0, 400);
        }
        finally { syncingMargins = false; }
    }

    /// <summary>[보기 모드] 버튼에 지금 모드 이름을 적는다(예: "페이지"). 만화는 원본 크기 · 꽉차게 …, epub은 스크롤 · 페이지 · 페이지 터치.</summary>
    private void SyncReadBarLabels()
    {
        if (toolbarViewMode == null || readBar is not { Visible: true }) return;
        IEnumerable<ToolStripMenuItem> items = imageMode
            ? imageViewModeMenuItems
            : new[] { scrollModeMenuItem, pageModeMenuItem, pageTouchModeMenuItem };
        var checkedItem = items.FirstOrDefault(i => i.Checked);
        string name = checkedItem?.Text.Replace("&", "") ?? "";
        string text = name.Length == 0 || (refBarActive && !imageMode) ? "보기 모드 ▾" : name + " ▾"; // 참고 자료의 글 문서에서는 읽기 방식 이름이 아니라 그냥 [보기 모드](회색)
        if (!ReferenceEquals(toolbarViewMode.Font, Font)) toolbarViewMode.Font = Font; // 아이콘 글꼴이 아니라 보통 글꼴로

        // 만화: 지금 보기 모드를 그림으로(폭 맞춤 ↔ / 꽉차게 ⛶). 그림이 없는 모드(원본 크기 · 사용자 지정)와 epub은 글자로.
        Image? modeImg = null;
        if (imageMode && checkedItem?.Tag is ImageViewMode vm)
            modeImg = vm switch
            {
                ImageViewMode.ActualSize => readImgActual ??= LoadReadImage("ViewActual.png"),
                ImageViewMode.FitWindow => readImgFit ??= LoadReadImage("ViewFit.png"),
                ImageViewMode.FitWidth => readImgFitWidth ??= LoadReadImage("ViewFitWidth.png"),
                ImageViewMode.FitWindowUpscale => readImgFitUpscale ??= LoadReadImage("ViewFitUpscale.png"),
                _ => null, // 사용자 지정은 그림이 없어 글자로
            };
        else if (refBarActive && !imageMode) modeImg = readImgFit ??= LoadReadImage("ViewFit.png"); // 참고 자료의 글 문서: 만화와 같은 그림으로 두고 눌리지 않게(SyncRefBarKind)
        if (modeImg != null)
        {
            if (toolbarViewMode.Image != modeImg) toolbarViewMode.Image = modeImg;
            if (toolbarViewMode.DisplayStyle != ToolStripItemDisplayStyle.Image) toolbarViewMode.DisplayStyle = ToolStripItemDisplayStyle.Image;
            if (toolbarViewMode.Text.Length != 0) toolbarViewMode.Text = "";
        }
        else
        {
            if (toolbarViewMode.Image != null) toolbarViewMode.Image = null;
            if (toolbarViewMode.DisplayStyle != ToolStripItemDisplayStyle.Text) toolbarViewMode.DisplayStyle = ToolStripItemDisplayStyle.Text;
            if (toolbarViewMode.Text != text) toolbarViewMode.Text = text;
        }

        // [보기 방식]: 드롭다운이 없는 단추. 만화에서 한쪽(OnePage) ↔ 두쪽(TwoPage)을 누를 때마다 바꾼다. epub에는 없어서 감춘다.
        if (layoutButton != null)
        {
            bool dbl = imageMode ? settings.ImageDoublePage : settings.EpubDoublePage;
            var img = dbl ? (readImgTwo ??= LoadReadImage("TwoPage.png")) : (readImgOne ??= LoadReadImage("OnePage.png"));
            if (layoutButton.Image != img) layoutButton.Image = img;
            string tip = !imageMode && !epubPageMode ? "한쪽 · 두쪽 보기는 페이지 방식에서 쓸 수 있습니다" : dbl ? "두쪽 보기 — 누르면 한쪽 보기" : "한쪽 보기 — 누르면 두쪽 보기";
            if (layoutButton.ToolTipText != tip) layoutButton.ToolTipText = tip;
            bool show = (imageMode || epubMode) && !(refBarActive ? settings.ToolbarRefHidden : settings.ToolbarHidden).Contains("vlayout");
            layoutButton.Enabled = (imageMode || epubPageMode) && !lockMode; // epub 스크롤 방식에는 쪽이 없다
            if (layoutButton.Visible != show)
            {
                layoutButton.Visible = show;
                CenterReadBar();
            }
        }
        if (marginHost != null) SyncMarginBoxes();
    }

    private Image? readImgOne, readImgTwo, readImgActual, readImgFit, readImgFitWidth, readImgFitUpscale;
    private ToolStripButton? layoutButton;

    /// <summary>exe에 넣어 둔 그림(Img\OnePage.png 등)을 서식창 버튼 크기(약 22px)로 줄여 읽는다. 못 읽으면 null.</summary>
    private Image? LoadReadImage(string resource)
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream(resource);
            if (s == null) return null;
            using var src = new Bitmap(s);
            int px = LogicalToDeviceUnits(22);
            var bmp = new Bitmap(px, px);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            float k = Math.Min((float)px / src.Width, (float)px / src.Height); // 그림 비율은 그대로
            float w = src.Width * k, h = src.Height * k;
            g.DrawImage(src, (px - w) / 2f, (px - h) / 2f, w, h);
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>만화에서 [보기 방식] 버튼을 누르면 한쪽 ↔ 두쪽을 바꾼다.</summary>
    private void OnViewLayoutClick()
    {
        if (imageMode) { ChooseImageLayout(!settings.ImageDoublePage); return; }
        if (!epubMode || !epubPageMode) return; // epub · txt(웹 화면)는 페이지 방식에서만 한쪽 · 두쪽
        settings.EpubDoublePage = !settings.EpubDoublePage;
        settings.Save();
        SyncReadBarLabels();
        ReloadEpubKeepingPlace(); // 한 화면에 1단 ↔ 2단으로 다시 배치(보던 자리 유지)
    }
}
