namespace CoffeeViewer;

/// <summary>
/// 소설 삭제 확인 창: 소설 이름을 그대로 적어야만 [소설 삭제하기]가 눌린다(실수로 지우는 걸 막는다).
/// </summary>
internal sealed class NovelDeleteDialog : Form
{
    private readonly TextBox input;
    private readonly Button delete;
    private readonly string name;

    public NovelDeleteDialog(string novelName)
    {
        name = novelName;
        Text = "소설 삭제";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true; // 앱 뒤로 숨어 앱이 먹통처럼 보이지 않게
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        var title = new Label { Text = "⚠   이 소설을 삭제하실 건가요…?", AutoSize = true, Font = new Font(Font.FontFamily, 13f, FontStyle.Regular), Margin = new Padding(0, 0, 0, 18) };
        var body = new Label
        {
            AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4),
            Text = $"'{novelName}' 소설을 삭제하면 다시는 되돌릴 수 없어요.",
        };
        // 두 번째 줄은 길어서 끊기니 글자를 작게 해서 한 줄로 둔다
        var body2 = new Label
        {
            AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 14),
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            Text = "진심으로 삭제를 원하시는 거라면 이 소설의 이름을 아래에 적어주세요.",
        };
        input = new TextBox { Width = 520, PlaceholderText = novelName, Margin = new Padding(0, 0, 0, 20) };
        input.Font = new Font(Font.FontFamily, 11.5f);
        input.TextChanged += (_, _) => SyncButton();

        delete = new Button
        {
            Text = "🗑  소설 삭제하기", AutoSize = true, MinimumSize = new Size(150, 38), FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Right, Margin = new Padding(0), UseVisualStyleBackColor = false,
        };
        delete.FlatAppearance.BorderSize = 0;
        delete.Click += (_, _) =>
        {
            if (Matches) DialogResult = DialogResult.OK;
        };

        root.Controls.Add(title);
        root.Controls.Add(body);
        root.Controls.Add(body2);
        root.Controls.Add(input);
        root.Controls.Add(delete);
        Controls.Add(root);
        AcceptButton = delete;

        Themes.ApplyChrome(this, AppShared.LibraryTheme);
        Shown += (_, _) => input.Focus();
        SyncButton();
    }

    private bool Matches => input.Text.Trim() == name;

    /// <summary>이름이 맞으면 진한 빨강, 아니면 옅은 빨강(눌러도 아무 일 없음).</summary>
    private void SyncButton()
    {
        bool ok = Matches;
        delete.BackColor = ok ? Color.FromArgb(0xE5, 0x39, 0x35) : Color.FromArgb(0xF0, 0x9A, 0x98);
        delete.ForeColor = Color.White;
        delete.Cursor = ok ? Cursors.Hand : Cursors.No;
    }
}
