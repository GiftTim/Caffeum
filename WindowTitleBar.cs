namespace CoffeeViewer;

/// <summary>
/// 창 맨 위 제목줄(윈도우 기본 제목줄 대신): 왼쪽에 앱 이름 Caffeum(필기체)만, 오른쪽에 [최소화] [최대화/복원] [닫기].
/// 높이 32, 이름을 왼쪽 끝에서 조금 오른쪽에 둔다. 빈 곳을 끌면 창이 움직이고 두 번 누르면 최대화된다 —
/// 그건 VaultForm.WndProc이 이 줄의 빈 곳을 "제목줄(HTCAPTION)"로 알려 주어서 윈도우가 알아서 한다.
/// </summary>
internal sealed class WindowTitleBar : Control
{
    private LibraryPalette? palette;
    private int hover = -1; // 0 최소화 · 1 최대화/복원 · 2 닫기
    private static Bitmap? logoMask;
    private Bitmap? logoTinted;
    private Color logoTint;

    public WindowTitleBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette p) { palette = p; BackColor = p.Main; Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(32); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = S(32); }

    private Rectangle ButtonRect(int i) => new(Width - (3 - i) * Height, 0, Height, Height); // 정사각형(폭 = 높이)

    /// <summary>이 자리(이 줄 안 좌표)를 끌어서 창을 움직여도 되는가(버튼 자리는 아니다).</summary>
    public bool IsCaptionPoint(Point p) => ClientRectangle.Contains(p) && IndexAt(p) < 0;

    private int IndexAt(Point p)
    {
        for (int i = 0; i < 3; i++) if (ButtonRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST) // 빈 곳은 창(VaultForm)이 제목줄로 판정하도록 넘긴다
        {
            long lp = m.LParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            if (IsCaptionPoint(pt)) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || FindForm() is not { } f) return;
        switch (IndexAt(e.Location))
        {
            case 0: f.WindowState = FormWindowState.Minimized; break;
            case 1: f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case 2: f.Close(); break;
        }
    }

    /// <summary>앱 아이콘(Caffeum.ico)의 큰 그림을 불러와, 모양(투명도)은 그대로 두고 크기만 맞춰 둔다(흰 테두리가 있어 어두운 테마에서도 보인다).</summary>
    private void EnsureLogo(Color tint, int size)
    {
        if (logoMask == null)
            try { using var s = typeof(WindowTitleBar).Assembly.GetManifestResourceStream("Caffeum.ico"); if (s != null) { using var ic = new Icon(s, 256, 256); logoMask = ic.ToBitmap(); } } catch { }
        if (logoMask == null || (logoTinted != null && logoTint == tint && logoTinted.Width == size)) return;
        logoTinted?.Dispose();
        logoTint = tint;
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
            {
                new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 }, // 색은 그대로: 검은 모양 + 흰 테두리라 어두운 줄에서도 보인다
            });
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(cm);
            g.DrawImage(logoMask, new Rectangle(0, 0, size, size), 0, 0, logoMask.Width, logoMask.Height, GraphicsUnit.Pixel, ia);
        }
        logoTinted = bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(palette?.Main ?? BackColor);
        if (palette is not { } p) return;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        // 왼쪽에 작은 로고(글자색으로 물들여서), 그 옆에 이름 Caffeum(필기체)
        int logoSize = S(20);
        EnsureLogo(p.Fore, logoSize);
        if (logoTinted != null) g.DrawImage(logoTinted, new Rectangle(S(10), (Height - logoSize) / 2, logoSize, logoSize));
        using var nameFont = SidebarBrand.TitleFont(13f);
        TextRenderer.DrawText(g, AppShared.AppDisplayName, nameFont, new Rectangle(S(38), 0, S(260), Height), p.Fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        bool max = FindForm()?.WindowState == FormWindowState.Maximized;
        string[] glyphs = { "", max ? "" : "", "" };
        using var font = new Font("Segoe MDL2 Assets", 9f);
        for (int i = 0; i < 3; i++)
        {
            var r = ButtonRect(i);
            bool close = i == 2;
            if (hover == i)
            {
                using var hb = new SolidBrush(close ? Color.FromArgb(0xE8, 0x11, 0x23) : p.CardHover);
                g.FillRectangle(hb, r);
            }
            TextRenderer.DrawText(g, glyphs[i], font, r, hover == i && close ? Color.White : p.Fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
