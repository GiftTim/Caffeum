namespace CoffeeViewer;

/// <summary>
/// 읽기 화면(탐색기 "Caffeum으로 보기" · 참고 자료)의 [줄바꿈 · 줄간격] 설정.
/// 예전의 [수정하기] 버튼 자리에 들어갔다: 누르면 이 작은 창이 뜬다. 읽는 글은 이제 고칠 수 없다.
/// </summary>
public partial class MainForm
{
    /// <summary>[줄바꿈 · 줄간격] 창을 열고, 확인하면 바로 읽기 화면에 적용한다.</summary>
    private void OpenViewSettings()
    {
        bool canBreak = shownKind == ContentKind.Txt; // 문장 단위 줄바꿈은 txt에만 있다
        using var dlg = new ViewSettingsDialog(theme, EffLine, settings.ParagraphSpacing, settings.TxtAutoBreak, canBreak);
        if (dlg.ShowDialog(HostForm) != DialogResult.OK) return;

        bool breakChanged = canBreak && dlg.AutoBreak != settings.TxtAutoBreak;
        if (canBreak) settings.TxtAutoBreak = dlg.AutoBreak;
        settings.ParagraphSpacing = dlg.ParagraphSpacing;
        if (lookRec != null) SetLook(r => r.LookLine = dlg.LineHeight); // 이 파일만의 줄 간격이 있으면 그쪽에
        else settings.LineHeight = dlg.LineHeight;
        settings.Save();
        AppShared.NotifySettingsChanged("margin");              // 줄 간격 · 문단 간격
        if (breakChanged) AppShared.NotifySettingsChanged("txtbreak"); // 문장 단위 줄바꿈: 글을 다시 나눠 만든다
    }
}

/// <summary>줄바꿈 · 줄간격 설정 창: [문장이 끝나면 줄 바꾸기] · 줄 간격(%) · 문단 간격(px).</summary>
internal sealed class ViewSettingsDialog : Form
{
    private readonly CheckBox autoBreak;
    private readonly NumericUpDown line, para;

    public bool AutoBreak => autoBreak.Checked;
    public int LineHeight => (int)line.Value;
    public int ParagraphSpacing => (int)para.Value;

    public ViewSettingsDialog(ReaderTheme theme, int lineHeight, int paraSpacing, bool breakOn, bool canBreak)
    {
        Text = "줄바꿈 · 줄간격";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        BackColor = theme.Back;
        ForeColor = theme.Fore;
        Shown += (_, _) => { Activate(); BringToFront(); };

        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        autoBreak = new CheckBox { Text = "자동 줄바꿈 — 문장이 끝나면 줄을 바꿔서 보기", AutoSize = true, Checked = breakOn, Enabled = canBreak, Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(autoBreak, 0, 0);
        root.SetColumnSpan(autoBreak, 2);
        var hint = new Label
        {
            Text = canBreak ? "마침표 · 물음표 · 느낌표 · …로 끝난 문장마다 새 줄이 됩니다. 원본 파일은 그대로예요." : "txt 문서에서만 쓸 수 있어요.",
            AutoSize = true, Margin = new Padding(22, 0, 0, 16), ForeColor = Themes.Blend(theme.Back, theme.Fore, 0.55f),
        };
        root.Controls.Add(hint, 0, 1);
        root.SetColumnSpan(hint, 2);

        line = new NumericUpDown { Minimum = 100, Maximum = 300, Increment = 10, Value = Math.Clamp(lineHeight, 100, 300), Width = 90, Margin = new Padding(0, 0, 0, 10) };
        para = new NumericUpDown { Minimum = 0, Maximum = 60, Increment = 2, Value = Math.Clamp(paraSpacing, 0, 60), Width = 90, Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(new Label { Text = "줄 간격 (%)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 14, 10) }, 0, 2);
        root.Controls.Add(line, 1, 2);
        root.Controls.Add(new Label { Text = "줄 사이 간격 (px)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 14, 10) }, 0, 3);
        root.Controls.Add(para, 1, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(84, 30), Padding = new Padding(8, 2, 8, 2) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(84, 30), Padding = new Padding(8, 2, 8, 2) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 4);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
