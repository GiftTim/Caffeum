namespace CoffeeViewer;

/// <summary>"서식" 메뉴(윈도우 메모장처럼): 자동 줄바꿈, 글꼴.</summary>
public partial class MainForm
{
    private ToolStripMenuItem? wordWrapMenuItem;

    private ToolStripMenuItem BuildFormatMenu()
    {
        var formatMenu = new ToolStripMenuItem("서식");
        wordWrapMenuItem = new ToolStripMenuItem("자동 줄바꿈", null, (_, _) => ToggleWordWrap());
        var fontItem = new ToolStripMenuItem("글꼴…", null, (_, _) => ChooseFont());
        formatMenu.DropDownItems.Add(wordWrapMenuItem);
        formatMenu.DropDownItems.Add(fontItem);
        formatMenu.DropDownOpening += (_, _) =>
        {
            // 페이지 방식은 한 화면에 들어가는 글자 수를 줄바꿈으로 재므로 항상 켜진 채로 두고 바꿀 수 없게 한다.
            wordWrapMenuItem.Enabled = shownKind == ContentKind.Txt && !pageMode;
            wordWrapMenuItem.Checked = EffWrap || pageMode;
        };
        return formatMenu;
    }

    private void ToggleWordWrap()
    {
        if (lookRec != null) { SetLook(r => r.LookWrap = !EffWrap); return; }
        settings.TxtWordWrap = !settings.TxtWordWrap;
        settings.Save();
        ApplyEditorWrap();
    }

    /// <summary>
    /// 설정(자동 줄바꿈)과 읽기 방식(페이지 방식은 항상 줄바꿈)에 맞춰 본문 상자의 줄바꿈을 정한다.
    /// 줄바꿈을 바꾸면 상자가 다시 만들어지므로, 선택 위치와 보던 자리를 되돌려 놓는다.
    /// </summary>
    private void ApplyEditorWrap()
    {
        bool wrap = EffWrap || pageMode;
        mainEditor.ScrollLocked = pageMode;
        // 페이지 방식은 한 화면에 들어가는 만큼만 보여주므로 스크롤바가 필요 없다(넘기기는 화살표 · 탭 · 휠로).
        var bars = mainEditor.TallHost != null ? ScrollBars.None : pageMode ? ScrollBars.None : wrap ? ScrollBars.Vertical : ScrollBars.Both;
        if (mainEditor.WordWrap == wrap && mainEditor.ScrollBars == bars) return;

        int start = mainEditor.SelectionStart, length = mainEditor.SelectionLength;
        int top = mainEditor.IsHandleCreated && !pageMode ? TxtTopOffset() : -1;
        mainEditor.WordWrap = wrap;
        mainEditor.ScrollBars = bars;
        mainEditor.Select(start, length);
        if (top >= 0 && mainEditor.IsHandleCreated) ScrollCharToTop(top);
    }

    // ───────────────────────── 글자 크기 확대 · 축소 (Ctrl+- / Ctrl++ / Ctrl+0 / Ctrl+휠) ─────────────────────────

    private const int DefaultFontSize = 11;
    private readonly System.Windows.Forms.Timer zoomTimer = new() { Interval = 120 };
    private int? zoomPending;

    /// <summary>글자 크기를 바꿀 수 있는 화면(txt · epub). PDF · 만화에서는 키를 가로채지 않는다.</summary>
    private bool CanZoomText => shownKind is ContentKind.Txt or ContentKind.Epub;

    /// <summary>
    /// 1pt씩 키우거나 줄인다(8~48pt). 휠을 빨리 돌리거나 키를 꾹 누르면 큰 글을 매번 다시 배치하느라 버벅이므로,
    /// 목표 크기만 바로 쌓아 두고 잠깐 멈추면 한 번만 실제로 반영한다.
    /// </summary>
    private void ZoomBy(int delta) => ZoomTo((zoomPending ?? (int)Math.Round(fontSize)) + delta);

    private void ZoomReset() => ZoomTo(DefaultFontSize);

    private void ZoomTo(int size)
    {
        zoomPending = Math.Clamp(size, 8, 48);
        zoomTimer.Stop();
        zoomTimer.Start();
    }

    private void ApplyPendingZoom()
    {
        if (zoomPending is not int size) return;
        zoomPending = null;
        if (size == (int)Math.Round(fontSize)) return;
        SetFontSize(size); // 이 파일에만 적용하고 기억한다
    }

    // ───────────────────────── 글자 크기 표시(보기 버튼 밑에 잠깐) ─────────────────────────

    private ZoomBubble? zoomBubble;

    /// <summary>글자 크기가 바뀔 때마다 보기 버튼 밑에 "12pt (109%)  −  +  초기화"를 잠깐 띄운다(기본 11pt = 100%).</summary>
    private void ShowZoomBubble(string? imageText = null)
    {
        if (zoomBubble == null)
        {
            zoomBubble = new ZoomBubble { Font = Font };
            // 만화를 보는 중이면 − / + / 초기화는 그림 확대 · 축소 · 맞춤으로, 아니면 글자 크기로.
            zoomBubble.StepRequested += d => { if (imageMode) imageCanvas.ZoomStep(d); else ViewZoomBy(d); };
            zoomBubble.ResetRequested += () => { if (imageMode) imageCanvas.ResetZoom(); else ViewZoomReset(); };
            Controls.Add(zoomBubble);
        }
        zoomBubble.SetPalette(LibraryPalette.From(theme));
        int size = (int)Math.Round(fontSize);
        zoomBubble.ShowValue(imageText ?? $"{ViewZoom}%");

        // 보기 버튼(AA) 바로 밑, 버튼 오른쪽 끝에 맞춰서. 툴바가 숨은 몰입 모드면 본문 오른쪽 위에.
        var menu = MainMenuStrip;
        int right = ClientSize.Width - LogicalToDeviceUnits(8), top = contentHost.Top + LogicalToDeviceUnits(8);
        if (menu != null && toolbarShown && toolbarLook != null)
        {
            // 툴바가 서재 머리 막대 안에 있을 수도 있어서 화면 좌표를 거쳐 이 창 좌표로 바꾼다.
            right = PointToClient(menu.PointToScreen(new Point(toolbarLook.Bounds.Right, 0))).X;
            top = Math.Max(contentHost.Top, PointToClient(menu.PointToScreen(new Point(0, menu.Height))).Y) + LogicalToDeviceUnits(4);
        }
        zoomBubble.Location = new Point(Math.Max(LogicalToDeviceUnits(8), Math.Min(right, ClientSize.Width - LogicalToDeviceUnits(8)) - zoomBubble.Width), top);
        zoomBubble.BringToFront();
    }

    /// <summary>보기 > 글자 크기 > 사용자 지정: 8~48pt를 직접 넣는다.</summary>
    private void AskCustomFontSize()
    {
        using var dlg = new Form
        {
            Text = "글자 크기",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16), Dock = DockStyle.Fill };
        var label = new Label { Text = "글자 크기(pt, 8~48)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 0) };
        var box = new NumericUpDown { Minimum = 8, Maximum = 48, Value = Math.Clamp((int)Math.Round(fontSize), 8, 48), Width = 80 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(box, 1, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        Themes.ApplyChrome(dlg, theme);
        dlg.Shown += (_, _) => { box.Select(0, box.Text.Length); box.Focus(); };
        if (dlg.ShowDialog(HostForm) == DialogResult.OK) ZoomTo((int)box.Value);
    }

    /// <summary>보기 > 보기 모드 > 사용자 지정: 원본 대비 10~500%를 직접 넣는다. 취소하면 null.</summary>
    private int? AskCustomImagePercent(int current)
    {
        using var dlg = new Form
        {
            Text = "보기 영역 사용자 지정",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16), Dock = DockStyle.Fill };
        var label = new Label { Text = "배율(%, 10~500, 100 = 원본 크기)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 0) };
        var box = new NumericUpDown { Minimum = 10, Maximum = 500, Increment = 10, Value = Math.Clamp(current, 10, 500), Width = 80 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(box, 1, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        Themes.ApplyChrome(dlg, theme);
        dlg.Shown += (_, _) => { box.Select(0, box.Text.Length); box.Focus(); };
        return dlg.ShowDialog(HostForm) == DialogResult.OK ? (int)box.Value : null;
    }

    /// <summary>글꼴 대화상자. 고른 글꼴 · 크기는 설정창과 같은 설정값(FontFamily · FontSize)에 저장되어 txt · epub 모두에 적용된다.</summary>
    private void ChooseFont()
    {
        using var dlg = new FontDialog
        {
            Font = new Font(EffFont, EffSize),
            ShowEffects = false,
            FontMustExist = true,
            MinSize = 8,
            MaxSize = 48,
            AllowVerticalFonts = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string family = dlg.Font.FontFamily.Name;
        int size = Math.Clamp((int)Math.Round(dlg.Font.Size), 8, 48);
        if (lookRec != null) { SetLook(r => { r.LookFont = family; r.LookSize = size; }); return; }
        settings.FontFamily = family;
        settings.FontSize = size;
        settings.Save();
        AppShared.NotifySettingsChanged("font");
        AppShared.NotifySettingsChanged("fontsize");
    }
}
