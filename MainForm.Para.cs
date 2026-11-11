namespace CoffeeViewer;

/// <summary>서식창의 [문단 서식] · 글자 서식: 정렬 · 여백 · 첫 줄 · 줄/문단 간격 · 줄 나눔 기준과 진하게 · 기울임 · 밑줄 · 취소선 · 글자 색을 파일마다 기억하고 적용한다.</summary>
public partial class MainForm
{
    /// <summary>글 칸에 걸 문단 서식: 문단 서식 창의 값에 서식창의 정렬 · 줄 간격 칸 값을 합친 것.</summary>
    private ParaFormat EditorPara()
    {
        var p = (lookRec?.Para ?? settings.DefaultPara)?.Clone() ?? new ParaFormat();
        p.Align = EffAlign;
        if (p.LineMode == 0) p.LinePercent = EffLine;
        return p;
    }

    private ParaFormat EffPara => lookRec?.Para ?? settings.DefaultPara ?? new ParaFormat { Align = EffAlign, LinePercent = EffLine };

    private void ShowParagraphDialog()
    {
        if (lookRec == null) return;
        var cur = lookRec.Para ?? settings.DefaultPara?.Clone() ?? new ParaFormat();
        cur.Align = lookRec.LookAlign ?? cur.Align;
        cur.LinePercent = EffLine;
        using var dlg = new ParagraphDialog(cur);
        if (dlg.ShowDialog(HostForm) != DialogResult.OK) return;
        var p = dlg.Result;
        SetLook(r =>
        {
            r.Para = p;
            // 서식창의 정렬 · 줄 간격 칸과 같은 값을 쓴다
            r.LookAlign = p.Align;
            if (p.LineMode == 0) r.LookLine = p.LinePercent;
        });
        if (mainEditor.Visible) ShowToast("문단 서식을 적용했어요");
    }

    /// <summary>읽기 화면(웹)의 글에 붙일 문단 서식 CSS.</summary>
    private string ParaCss()
    {
        var p = lookRec?.Para ?? settings.DefaultPara;
        if (p == null) return "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string pt(double d) => d.ToString("0.##", inv) + "pt";
        double first = p.FirstMode == 1 ? p.FirstPt : p.FirstMode == 2 ? -p.FirstPt : 0;
        double marginLeft = p.Left + (first < 0 ? -first : 0);
        var css = new System.Text.StringBuilder(" p { ");
        css.Append("margin-left: ").Append(pt(marginLeft)).Append(" !important; ");
        css.Append("margin-right: ").Append(pt(p.Right)).Append(" !important; ");
        css.Append("text-indent: ").Append(pt(first)).Append(" !important; ");
        css.Append("margin-top: ").Append(pt(p.Before)).Append(" !important; ");
        css.Append("margin-bottom: ").Append(pt(p.After)).Append(" !important; ");
        if (p.LineMode == 1) css.Append("line-height: ").Append(pt(p.LinePt)).Append(" !important; ");
        if (p.KoUnit == "word") css.Append("word-break: keep-all !important; ");
        css.Append(p.EnUnit == "char" ? "overflow-wrap: anywhere !important; " : "overflow-wrap: break-word !important; ");
        css.Append("} body.clr-txt p { margin-bottom: ").Append(pt(p.After)).Append(" !important; }");
        return css.ToString();
    }

    // ───────── 글자 서식(진하게 · 기울임 · 밑줄 · 취소선 · 글자 색) — 고른 글자 · 누른 자리부터 ─────────

    private bool formatDirty; // 글은 그대로인데 서식만 바뀌었다(자동저장이 서식 파일을 다시 쓰게)

    private bool CanFormatChars => embedded && directEdit && !mainEditor.ReadOnly && shownKind == ContentKind.Txt && editorHost.Visible;

    /// <summary>서식 버튼을 눌렀다: 글자를 골랐으면 그 글자에, 안 골랐으면 커서 이후에 칠 글자에 적용한다. 이미 그 서식이면 해제한다.</summary>
    private void ApplyCharStyle(FontStyle style)
    {
        if (!CanFormatChars) { ShowToast("글을 고치는 중일 때 쓸 수 있어요"); return; }
        mainEditor.ToggleStyle(style);
        MarkFormatDirty();
        mainEditor.Focus();
        SyncCharButtons();
    }

    private void PickLookColor()
    {
        if (!CanFormatChars) { ShowToast("글을 고치는 중일 때 쓸 수 있어요"); return; }
        using var dlg = new ColorDialog { FullOpen = true, Color = mainEditor.SelectionColor };
        if (dlg.ShowDialog(HostForm) != DialogResult.OK) return;
        mainEditor.ApplyColor(dlg.Color);
        MarkFormatDirty();
        mainEditor.Focus();
    }

    /// <summary>서식만 바뀌었음을 알리고 자동저장 시계를 돌린다.</summary>
    private void MarkFormatDirty()
    {
        if (!directEdit || mainEditor.ReadOnly) return;
        formatDirty = true;
        autoSaveTimer.Stop(); autoSaveTimer.Start();
        if (!autoSaveMaxTimer.Enabled) autoSaveMaxTimer.Start();
        UpdateSavedDot();
    }

    /// <summary>서식 버튼이 지금 커서(또는 고른 글자)의 서식대로 눌려 보이게 한다.</summary>
    private void SyncCharButtons()
    {
        if (lookBold == null || lookItalic == null || lookUnderline == null || lookStrike == null || lookColor == null) return;
        bool can = CanFormatChars;
        lookBold.Enabled = lookItalic.Enabled = lookUnderline.Enabled = lookStrike.Enabled = lookColor.Enabled = can;
        lookBold.Checked = can && mainEditor.HasStyle(FontStyle.Bold);
        lookItalic.Checked = can && mainEditor.HasStyle(FontStyle.Italic);
        lookUnderline.Checked = can && mainEditor.HasStyle(FontStyle.Underline);
        lookStrike.Checked = can && mainEditor.HasStyle(FontStyle.Strikeout);
    }

    // ───────── 서식 보관: 글(.txt)은 순수한 글로 두고, 서식은 옆 숨김 폴더의 .rtf에 따로 둔다 ─────────

    private string? FormatSidecarPath()
    {
        if (string.IsNullOrEmpty(currentPath) || Path.GetDirectoryName(currentPath) is not string dir) return null;
        return Path.Combine(dir, ".caffeum-fmt", Path.GetFileName(currentPath) + ".rtf");
    }

    private static readonly System.Text.RegularExpressions.Regex HasFormatRegex =
        new(@"\\(b|i|ul|strike)(?![a-z0-9\-])|\\cf[1-9]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>서식이 하나라도 있으면 옆 .rtf에 저장하고, 없으면 있던 서식 파일을 지운다.</summary>
    private void SaveFormatSidecar()
    {
        string? p = FormatSidecarPath();
        if (p == null) return;
        try
        {
            string rtf = mainEditor.Rtf ?? "";
            if (HasFormatRegex.IsMatch(rtf))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                var di = new DirectoryInfo(Path.GetDirectoryName(p)!);
                if ((di.Attributes & FileAttributes.Hidden) == 0) di.Attributes |= FileAttributes.Hidden;
                File.WriteAllText(p, rtf, System.Text.Encoding.ASCII);
            }
            else if (File.Exists(p)) File.Delete(p);
        }
        catch { /* 서식 보관에 실패해도 글 저장은 그대로 */ }
    }

    /// <summary>글을 연 직후: 이 파일의 서식 보관본이 있고 글자가 똑같으면 서식을 입힌다(글이 밖에서 바뀌었으면 서식은 버린다).</summary>
    private void TryLoadFormatSidecar(string plainText)
    {
        string? p = FormatSidecarPath();
        if (p == null || !File.Exists(p)) return;
        try
        {
            mainEditor.Rtf = File.ReadAllText(p, System.Text.Encoding.ASCII);
            if (mainEditor.Text != plainText) mainEditor.Text = plainText; // 글이 달라졌다 — 서식 없이
        }
        catch { mainEditor.Text = plainText; }
    }
}
