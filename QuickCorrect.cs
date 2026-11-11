namespace CoffeeViewer;

/// <summary>사용자 사전 한 줄: [틀린 말(입력할 말)] → [맞는 말(바뀔 말)].</summary>
public sealed class WordPair
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>글을 쓰는 동안 자동으로 고쳐 주는 규칙(설정에 저장된 사전과 자동 서식)을 한곳에서 다룬다.</summary>
internal static class QuickCorrect
{
    /// <summary>띄어쓰기 · 엔터 · 문장부호를 쳤을 때(= 낱말이 끝났을 때) true.</summary>
    public static bool IsBoundary(char c) => c == '\r' || c == '\n' || c == ' ' || c == '\t' || (c >= ' ' && !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '\'');
}

/// <summary>사전에 한 줄을 넣거나 고치는 작은 창.</summary>
internal sealed class PairEditDialog : Form
{
    private readonly TextBox from, to;
    public string FromText => from.Text.Trim();
    public string ToText => to.Text;

    public PairEditDialog(string title, string fromLabel, string toLabel, string fromValue, string toValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        from = new TextBox { Text = fromValue, Width = 280, Margin = new Padding(0, 0, 0, 8) };
        to = new TextBox { Text = toValue, Width = 280, Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(new Label { Text = fromLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 8) });
        root.Controls.Add(from);
        root.Controls.Add(new Label { Text = toLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 12) });
        root.Controls.Add(to);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var ok = new Button { Text = "확인", AutoSize = true, MinimumSize = new Size(84, 30) };
        var cancel = new Button { Text = "취소", AutoSize = true, MinimumSize = new Size(84, 30), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        CancelButton = cancel;
        AcceptButton = ok;
        ok.Click += (_, _) =>
        {
            if (FromText.Length == 0) { MessageBox.Show(this, $"{fromLabel}을 입력해 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (FromText.Any(char.IsWhiteSpace)) { MessageBox.Show(this, $"{fromLabel}에는 띄어쓰기를 넣을 수 없어요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            DialogResult = DialogResult.OK;
        };
        Shown += (_, _) => { from.Focus(); from.SelectAll(); };
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}


/// <summary>
/// 도구 > 빠른 교정 내용: 한글(HWP)의 같은 창처럼 탭 세 개 — [빠른 교정 사용자 사전](틀린 말 → 맞는 말) ·
/// [입력 자동 명령 사용자 사전](입력 · 내용 · 종류) · [입력 자동 서식]. 고치는 즉시 설정에 저장된다.
/// </summary>
internal sealed class QuickCorrectDialog : Form
{
    private readonly AppSettings settings;
    private static readonly Color Accent = Color.FromArgb(0x3B, 0x6F, 0xD8);

    public QuickCorrectDialog(AppSettings settings)
    {
        this.settings = settings;
        Text = "빠른 교정 내용";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        ClientSize = new Size(820, 712); // 한글의 같은 창과 같은 크기

        var tabs = new TabControl { Location = new Point(16, 16), Size = new Size(654, 680), Padding = new Point(16, 6), Multiline = false };
        tabs.TabPages.Add(BuildDictTab("빠른 교정 사용자 사전", "빠른 교정 사용자 등록 낱말", settings.QuickCorrect, command: false));
        tabs.TabPages.Add(BuildDictTab("입력 자동 명령 사용자 사전", "입력 자동 명령 사용자 등록 낱말", settings.AutoCommands, command: true));
        tabs.TabPages.Add(BuildFormatTab());
        var close = new Button { Text = "닫기(&D)", Location = new Point(688, 20), Size = new Size(116, 40), DialogResult = DialogResult.Cancel };
        Controls.Add(tabs);
        Controls.Add(close);
        CancelButton = close;
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }

    private TabPage BuildDictTab(string tabTitle, string label, List<WordPair> data, bool command)
    {
        const string KindText = "글자 바꾸기";
        var page = new TabPage(tabTitle) { Padding = new Padding(12) };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(16, 18) };
        TextBox? box = null;
        int listTop = 50;
        if (!command)
        {
            box = new TextBox { Location = new Point(16, 46), Width = 610, PlaceholderText = "찾을 내용을 입력하세요." };
            listTop = 86;
        }
        var list = new ListView { Location = new Point(16, listTop), Size = new Size(610, 450), View = View.Details, FullRowSelect = true, GridLines = false, MultiSelect = false, HideSelection = false };
        if (command)
        {
            list.Columns.Add("입력", 180);
            list.Columns.Add("내용", 240);
            list.Columns.Add("종류", 170);
        }
        else
        {
            list.Columns.Add("틀린 말", 290);
            list.Columns.Add("맞는 말", 300);
        }
        // 자동 명령 사전이 비어 있을 때의 안내(목록 가운데)
        var empty = new Label
        {
            Text = "최근에 입력한 자동 명령이 없습니다. 사용자가 자동 명령을 입력하면 [입력 자동 명령 사용자 사전]에 등록됩니다.",
            ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Visible = false,
        };
        if (command) list.Controls.Add(empty);

        Button? bEdit = null, bDel = null, bUp = null, bDown = null, bSave = null;
        void SyncButtons()
        {
            bool sel = list.SelectedItems.Count > 0;
            if (bEdit != null) bEdit.Enabled = sel;
            if (bDel != null) bDel.Enabled = sel;
            if (bUp != null) bUp.Enabled = sel && list.SelectedIndices[0] > 0;
            if (bDown != null) bDown.Enabled = sel && list.SelectedIndices[0] < list.Items.Count - 1;
            if (bSave != null) bSave.Enabled = data.Count > 0;
            empty.Visible = command && data.Count == 0;
        }
        void Refill(string? select = null)
        {
            string f = box?.Text.Trim() ?? "";
            list.BeginUpdate();
            list.Items.Clear();
            IEnumerable<WordPair> rows = command ? data : data.OrderBy(p => p.From, StringComparer.CurrentCulture);
            foreach (var p in rows)
            {
                if (f.Length > 0 && !p.From.Contains(f, StringComparison.CurrentCultureIgnoreCase)) continue;
                var it = new ListViewItem(p.From);
                it.SubItems.Add(p.To);
                if (command) it.SubItems.Add(KindText);
                list.Items.Add(it);
                if (select != null && p.From == select) { it.Selected = true; it.EnsureVisible(); }
            }
            list.EndUpdate();
            SyncButtons();
        }
        void Persist() { AppShared.Settings.Save(); }
        if (box != null) box.TextChanged += (_, _) => Refill();
        list.SelectedIndexChanged += (_, _) => SyncButtons();

        string fromLabel = command ? "입력할 말" : "틀린 말", toLabel = command ? "바뀔 말" : "맞는 말";
        string addTitle = command ? "입력 자동 명령 추가" : "빠른 교정 추가";
        void Add()
        {
            using var dlg = new PairEditDialog(addTitle, fromLabel, toLabel, box?.Text.Trim() ?? "", "");
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            data.RemoveAll(p => p.From == dlg.FromText);
            data.Add(new WordPair { From = dlg.FromText, To = dlg.ToText });
            Persist();
            box?.Clear();
            Refill(dlg.FromText);
        }
        void Edit()
        {
            if (list.SelectedItems.Count == 0) return;
            string oldFrom = list.SelectedItems[0].Text;
            var cur = data.First(p => p.From == oldFrom);
            using var dlg = new PairEditDialog(addTitle, fromLabel, toLabel, cur.From, cur.To);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int at = data.IndexOf(cur);
            data.RemoveAll(p => p != cur && p.From == dlg.FromText);
            cur.From = dlg.FromText;
            cur.To = dlg.ToText;
            Persist();
            Refill(dlg.FromText);
        }
        void Delete()
        {
            if (list.SelectedItems.Count == 0) return;
            string f = list.SelectedItems[0].Text;
            data.RemoveAll(p => p.From == f);
            Persist();
            Refill();
        }
        void Move(int delta) // 자동 명령 사전의 순서(▲▼)
        {
            if (list.SelectedItems.Count == 0) return;
            int i = data.FindIndex(p => p.From == list.SelectedItems[0].Text), j = i + delta;
            if (i < 0 || j < 0 || j >= data.Count) return;
            (data[i], data[j]) = (data[j], data[i]);
            Persist();
            Refill(data[j].From);
        }
        void Open() // 탭으로 나뉜 글 파일(한 줄에 "틀린 말[탭]맞는 말")에서 불러와 합친다
        {
            using var ofd = new OpenFileDialog { Filter = "사전 파일 (*.txt;*.tsv)|*.txt;*.tsv|모든 파일|*.*", Title = "사전 불러오기" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            int n = 0;
            try
            {
                foreach (var line in File.ReadAllLines(ofd.FileName))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2 || parts[0].Trim().Length == 0 || parts[0].Trim().Any(char.IsWhiteSpace)) continue;
                    string f = parts[0].Trim();
                    data.RemoveAll(p => p.From == f);
                    data.Add(new WordPair { From = f, To = parts[1] });
                    n++;
                }
            }
            catch (Exception ex) { MessageBox.Show(this, $"불러오지 못했어요: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Persist();
            Refill();
            MessageBox.Show(this, $"{n:N0}개를 불러왔어요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        void Save()
        {
            using var sfd = new SaveFileDialog { Filter = "사전 파일 (*.txt)|*.txt", Title = "사전 저장하기", FileName = tabTitle + ".txt" };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllLines(sfd.FileName, data.Select(p => p.From + "\t" + p.To)); }
            catch (Exception ex) { MessageBox.Show(this, $"저장하지 못했어요: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        list.DoubleClick += (_, _) => Edit();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) Delete(); };

        // 아이콘 버튼 줄: 목록 바로 밑. 아이콘이 잘리지 않게 칸을 넉넉히(글꼴 높이보다 크게) 둔다.
        var bar = new FlowLayoutPanel { Location = new Point(16, 0), Size = new Size(610, 56), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        Button Icon(string glyph, string tip, Action click, int gapBefore = 0)
        {
            var b = new Button { Text = glyph, Font = new Font("Segoe MDL2 Assets", 12f), Size = new Size(48, 48), Margin = new Padding(gapBefore, 0, 6, 0), Padding = Padding.Empty, UseCompatibleTextRendering = false };
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => click();
            bar.Controls.Add(b);
            return b;
        }
        Icon("", "추가", Add);
        bEdit = Icon("", "고치기", Edit);
        bDel = Icon("", "지우기 (Delete)", Delete);
        if (command)
        {
            bUp = Icon("", "위로", () => Move(-1), 8);
            bDown = Icon("", "아래로", () => Move(1));
        }
        Icon("", "사전 파일 불러오기(탭으로 나눈 글)", Open, 8);
        bSave = Icon("", "사전 파일로 저장하기", Save);

        page.Controls.AddRange(new Control[] { lbl });
        if (box != null) page.Controls.Add(box);
        page.Controls.Add(list);
        page.Controls.Add(bar);
        void LayoutPage()
        {
            int h = page.ClientSize.Height;
            bar.Top = h - bar.Height - 12;
            list.Height = Math.Max(120, bar.Top - listTop - 12);
        }
        page.SizeChanged += (_, _) => LayoutPage();
        LayoutPage();
        Refill();
        return page;
    }

    /// <summary>[입력 자동 서식]: 한글의 같은 탭처럼 [입력할 때 자동으로 바꾸기] · [입력할 때 자동으로 서식 설정] 두 묶음.</summary>
    private TabPage BuildFormatTab()
    {
        var page = new TabPage("입력 자동 서식") { Padding = new Padding(12) };
        int y = 18;
        void Section(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Location = new Point(18, y), ForeColor = Accent };
            page.Controls.Add(l);
            var line = new Panel { Location = new Point(18 + TextRenderer.MeasureText(text, Font).Width + 14, y + 10), Size = new Size(0, 1), BackColor = Color.FromArgb(200, 200, 200) };
            line.Width = 610 - line.Left;
            page.Controls.Add(line);
            y += 36;
        }
        void Check(string text, bool value, Action<bool>? set, bool available = true)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Location = new Point(40, y), Checked = value && available, Enabled = available };
            if (set != null) c.CheckedChanged += (_, _) => { set(c.Checked); AppShared.Settings.Save(); };
            page.Controls.Add(c);
            y += 34;
        }
        Section("입력할 때 자동으로 바꾸기");
        Check("\"곧은 따옴표\"를 “둥근 따옴표”로 자동 바꾸기", settings.AutoQuotes, v => settings.AutoQuotes = v);
        Check("하이픈(-)을 대시(—)로 바꾸기 (하이픈 두 번)", settings.AutoDash, v => settings.AutoDash = v);
        Check("마침표 세 번을 말줄임표(…)로 바꾸기", settings.AutoEllipsis, v => settings.AutoEllipsis = v);
        Check("마침표 세 번을 가운뎃점 여섯 개 말줄임표(······)로 바꾸기", settings.AutoEllipsisDots, v => settings.AutoEllipsisDots = v);
        Check("서수(1st)에 위 첨자 적용 (1ˢᵗ)", settings.AutoOrdinal, v => settings.AutoOrdinal = v);
        Check("괄호 짝 맞추기", settings.AutoBrackets, v => settings.AutoBrackets = v);
        Check("웹 페이지와 네트워크 경로를 하이퍼링크로 연결 (txt에서는 쓸 수 없어요)", false, null, available: false);
        y += 6;
        Section("입력할 때 자동으로 서식 설정");
        Check("자동 글머리 기호 넣기 (줄이 - 로 시작하면 엔터 뒤에도 이어서)", settings.AutoBullet, v => settings.AutoBullet = v);
        Check("자동 번호 매기기 (1. 로 시작하면 엔터 뒤에 2. …)", settings.AutoNumber, v => settings.AutoNumber = v);
        Check("문단 띠 넣기 (txt에서는 쓸 수 없어요)", false, null, available: false);
        Check("숫자만으로 날짜 표기시 마침표 적용 (2024.5.3 → 2024.5.3.)", settings.AutoDate, v => settings.AutoDate = v);
        return page;
    }
}
