using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CoffeeViewer;

/// <summary>
/// 소설 "기본 정보" 창: 제목(= 소설 폴더 이름)과 작품 설명을 고친다. 소설 메인의 제목이나 설명을 누르면 뜬다.
/// 왼쪽은 표지 자리, 오른쪽에 제목 · 작품 설명, 오른쪽 아래 [수정하기]. 바뀐 게 있어야만 [수정하기]가 진해진다.
/// </summary>
internal sealed class NovelInfoDialog : Form
{
    private readonly LibraryPalette p;
    private readonly TextBox title, desc;
    private readonly PillButton ok;
    private readonly string origTitle, origDesc;
    private Bitmap? preview;      // 표지 미리보기(저장된 것 또는 방금 고른 것)
    private bool coverHover;
    /// <summary>방금 고른(아직 저장 전) 표지 이미지 경로. 없으면 null.</summary>
    public string? NewCoverSource { get; private set; }
    /// <summary>저장돼 있던 표지를 [×]로 지웠다(수정하기를 누르면 파일을 지운다).</summary>
    public bool RemoveCover { get; private set; }
    private bool hadSavedCover;
    private Rectangle coverX; // 표지 오른쪽 위 [×] 자리(그릴 때마다 정해진다)
    private bool coverXHover;

    public string TitleText => title.Text.Trim();
    public string DescText => desc.Text.Trim();

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    /// <param name="creating">서재에서 새 작품을 만들 때(버튼이 [만들기], 제목 · 설명이 비어 있다).</param>
    public NovelInfoDialog(string novelTitle, string? description, string? novelDir = null, bool creating = false)
    {
        p = LibraryPalette.From(AppShared.LibraryTheme);
        origTitle = novelTitle;
        origDesc = description?.Trim() ?? "";
        string ff = NovelStore.UiFontFamily;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Shown += (_, _) => { Activate(); BringToFront(); }; // 앱 안에서만 앞으로 온다(다른 프로그램 위로 뜨지는 않는다)
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(900, 580);
        Font = new Font(ff, 11f);
        BackColor = p.Main;
        ForeColor = p.Fore;
        DoubleBuffered = true;
        KeyPreview = true;

        // 머리: [✎ 기본 정보] 가운데, [×] 오른쪽 (머리를 끌어 창을 옮긴다)
        var head = new Panel();
        Controls.Add(head);
        head.SetBounds(0, 0, 900, 68);
        head.BackColor = p.Main;
        head.Paint += (_, e) =>
        {
            using var icon = new Font("Segoe MDL2 Assets", 12f);
            using var tf = new Font(ff, 12.5f);
            int tw = TextRenderer.MeasureText("기본 정보", tf, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = (head.Width - tw - 34) / 2;
            TextRenderer.DrawText(e.Graphics, "", icon, new Rectangle(x, 0, 26, head.Height), p.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "기본 정보", tf, new Rectangle(x + 34, 0, tw + 4, head.Height), p.Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var pen = new Pen(p.Line);
            e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
        };
        head.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
        var close = new Label
        {
            Text = "", Font = new Font("Segoe MDL2 Assets", 12f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand, ForeColor = p.Fore, BackColor = p.Main,
        };
        close.SetBounds(900 - 64, 14, 40, 40);
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        close.MouseEnter += (_, _) => close.BackColor = p.CardHover;
        close.MouseLeave += (_, _) => close.BackColor = p.Main;
        head.Controls.Add(close);

        // 왼쪽 표지 자리: 누르면 컴퓨터에서 이미지를 골라 넣는다(400 × 600 px · 20MB 이하만 받는다)
        preview = novelDir != null ? NovelStore.LoadCover(novelDir) : null;
        hadSavedCover = preview != null;
        var cover = new Panel { BackColor = p.Main, Cursor = Cursors.Hand };
        cover.SetBounds(36, 108, 244, 362); // 안쪽 표지 칸이 236 × 354 = 400:600 비율
        new ToolTip().SetToolTip(cover, "눌러서 표지 이미지 넣기");
        cover.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2, 2, cover.Width - 8, cover.Height - 8);
            using (var sh = LibraryPalette.Rounded(new RectangleF(r.X + 2, r.Y + 3, r.Width, r.Height), 12))
            using (var sb = new SolidBrush(Color.FromArgb(p.Dark ? 70 : 28, Color.Black))) g.FillPath(sb, sh);
            using (var path = LibraryPalette.Rounded(r, 12))
            {
                using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.04f))) g.FillPath(b, path);
                if (preview != null)
                {
                    var old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(preview, Rectangle.Round(r));
                    g.Clip = old;
                }
                using (var pen = new Pen(coverHover ? p.Accent : p.Line, coverHover ? 1.6f : 1f)) g.DrawPath(pen, path);
            }
            if (preview != null)
            {
                // 표지 오른쪽 위 [×]: 누르면 이 그림을 뺀다
                coverX = new Rectangle((int)r.Right - 38, (int)r.Y + 12, 26, 26);
                using (var xb = new SolidBrush(Color.FromArgb(coverXHover ? 230 : 170, coverXHover ? Color.FromArgb(0xE5, 0x39, 0x35) : Color.Black))) g.FillEllipse(xb, coverX);
                using var xp = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int cx = coverX.X + 13, cy = coverX.Y + 13;
                g.DrawLine(xp, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(xp, cx - 5, cy + 5, cx + 5, cy - 5);
            }
            else coverX = Rectangle.Empty;
            if (preview != null) return;
            // 아직 표지가 없을 때: 연필 그림 + 그 밑에 권장 크기
            using var icon = new Font("Segoe MDL2 Assets", 30f);
            using var small = new Font(NovelStore.UiFontFamily, 9.5f);
            var dim = Themes.Blend(p.Main, p.Dim, 0.75f);
            var ir = new Rectangle((int)r.X, (int)r.Y + (int)r.Height / 2 - 52, (int)r.Width, 64);
            TextRenderer.DrawText(g, "", icon, ir, dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"({NovelStore.CoverWidth} X {NovelStore.CoverHeight} px)", small, new Rectangle((int)r.X, ir.Bottom + 4, (int)r.Width, 24), dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
        cover.MouseEnter += (_, _) => { coverHover = true; cover.Invalidate(); };
        cover.MouseLeave += (_, _) => { coverHover = false; cover.Invalidate(); };
        cover.MouseMove += (_, e) =>
        {
            bool h = preview != null && coverX.Contains(e.Location);
            if (h != coverXHover) { coverXHover = h; cover.Invalidate(); }
        };
        cover.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (preview != null && coverX.Contains(e.Location))
            {
                // [×]: 방금 고른 그림이면 취소, 저장돼 있던 표지면 지우기로 표시
                preview.Dispose();
                preview = null;
                NewCoverSource = null;
                RemoveCover = hadSavedCover;
                coverXHover = false;
                cover.Invalidate();
                SyncButton();
            }
            else PickCover(cover);
        };
        Controls.Add(cover);

        // 오른쪽 입력칸들
        int x0 = 312, w = 900 - x0 - 36;
        AddLabel("", "제목", x0, 100);
        title = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "소설 제목", Text = novelTitle, Font = new Font(ff, 12f), BackColor = p.Main, ForeColor = p.Fore };
        AddBox(title, new Rectangle(x0, 140, w, 48));
        AddLabel("", "작품 설명", x0, 212);
        desc = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "작품 설명(비워 두면 안내 문구가 들어가요)", Text = origDesc, Font = new Font(ff, 11.5f), Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, BackColor = p.Main, ForeColor = p.Fore };
        AddBox(desc, new Rectangle(x0, 252, w, 200));

        ok = new PillButton(creating ? "만들기" : "수정하기") { Primary = true, Glyph = "", Font = new Font(ff, 11.5f, FontStyle.Bold), AutoSize = false, Muted = true };
        ok.SetBounds(900 - 36 - 160, 580 - 24 - 52, 160, 52);
        ok.SetPalette(p);
        ok.Click += (_, _) => Submit();
        Controls.Add(ok);

        title.TextChanged += (_, _) => SyncButton();
        desc.TextChanged += (_, _) => SyncButton();
        Shown += (_, _) => { title.Focus(); title.SelectionStart = title.TextLength; };
        Themes.ApplyChrome(this, AppShared.LibraryTheme); // 제목줄 색 등
        BackColor = p.Main;
        SyncButton();
    }

    private bool Changed => TitleText != origTitle || DescText != origDesc || NewCoverSource != null || RemoveCover;
    /// <summary>[표지] 칸을 눌렀을 때: 이미지를 고르고, 400 × 600 px, 20MB 이하인 것만 받는다.</summary>
    private void PickCover(Control cover)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "표지 이미지 고르기",
            Filter = "이미지 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            long bytes = new FileInfo(dlg.FileName).Length;
            if (bytes > NovelStore.MaxCoverBytes)
            {
                MessageBox.Show(this, $"표지 이미지는 20MB 이하만 넣을 수 있어요.\n고른 파일은 {bytes / 1048576.0:0.0}MB예요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var img = Image.FromFile(dlg.FileName);
            if (!NovelStore.IsCoverSize(img.Width, img.Height))
            {
                MessageBox.Show(this,
                    $"표지는 {NovelStore.CoverWidth} X {NovelStore.CoverHeight} px 크기의 이미지만 넣을 수 있어요.\n고른 이미지는 {img.Width} X {img.Height} px예요.",
                    AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            preview?.Dispose();
            preview = new Bitmap(img);
            NewCoverSource = dlg.FileName;
            RemoveCover = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"이미지를 열지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        cover.Invalidate();
        SyncButton();
    }

    protected override void Dispose(bool disposing) { if (disposing) preview?.Dispose(); base.Dispose(disposing); }

    private bool Valid => NovelStore.ValidName(TitleText);

    private void SyncButton() { ok.Muted = !(Changed && Valid); ok.Invalidate(); }

    private void Submit()
    {
        if (!Changed) return;
        if (!Valid)
        {
            MessageBox.Show(this, "제목이 비어 있거나 쓸 수 없는 글자(\\ / : * ? \" < > |)가 들어 있어요.", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private void AddLabel(string glyph, string text, int x, int y)
    {
        var g = new Label { Text = glyph, Font = new Font("Segoe MDL2 Assets", 12f), ForeColor = p.Accent, BackColor = p.Main, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        g.SetBounds(x, y, 28, 30);
        var t = new Label { Text = text, Font = new Font(NovelStore.UiFontFamily, 11.5f), ForeColor = p.Fore, BackColor = p.Main, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        t.SetBounds(x + 36, y, 200, 30);
        Controls.Add(g);
        Controls.Add(t);
    }

    /// <summary>둥근 테두리 칸 안에 테두리 없는 입력칸을 넣는다(포커스가 가면 테두리가 강조색).</summary>
    private void AddBox(TextBox tb, Rectangle r)
    {
        var box = new Panel { BackColor = p.Main };
        box.SetBounds(r.X, r.Y, r.Width, r.Height);
        box.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, box.Width - 1.5f, box.Height - 1.5f), 9);
            using var pen = new Pen(tb.Focused ? p.Accent : p.Line, tb.Focused ? 1.6f : 1f);
            e.Graphics.DrawPath(pen, path);
        };
        int padX = 16, padY = tb.Multiline ? 12 : 0;
        tb.GotFocus += (_, _) => box.Invalidate();
        tb.LostFocus += (_, _) => box.Invalidate();
        box.Click += (_, _) => tb.Focus();
        box.Controls.Add(tb);
        if (tb.Multiline) tb.SetBounds(padX, padY, r.Width - padX * 2, r.Height - padY * 2);
        else tb.SetBounds(padX, (r.Height - tb.PreferredHeight) / 2, r.Width - padX * 2, tb.PreferredHeight);
        Controls.Add(box);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { Submit(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(p.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); // 테두리 없는 창의 바깥 선
    }
}
