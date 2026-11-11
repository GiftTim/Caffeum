namespace CoffeeViewer;

/// <summary>
/// PIP 모드: 참고 자료를 다른 창 위에 늘 떠 있는 작은 창으로 띄운다. 원고를 쓰면서 옆에 참고 자료를 보는 용도.
/// 안에는 참고 자료 서식창을 그대로 단 문서 화면(MainForm)이 들어 있고, 크기를 늘이고 줄이거나 끌어 옮길 수 있다.
/// </summary>
internal sealed class PipForm : Form
{
    private readonly MainForm doc;
    public PipForm(string path)
    {
        Text = Path.GetFileNameWithoutExtension(path) + " — PIP";
        TopMost = false;                                 // 기본은 일반 창(작업표시줄로 꺼낼 수 있다). 서식창의 [항상 위] 버튼으로 켠다
        ShowInTaskbar = true;                            // 작업표시줄에 따로 떠서, 가려져도 눌러서 다시 꺼낼 수 있다
        FormBorderStyle = FormBorderStyle.Sizable;      // 도구 창이 아니어야 작업표시줄에 뜬다
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Icon = AppShared.LoadAppIcon();
        MinimumSize = new Size(360, 320);
        Size = new Size(720, 860);

        doc = new MainForm(path, embedded: true) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill, InPip = true };
        AppShared.RegisterReader(doc);
        Controls.Add(doc);
        doc.Show();

        // 중간 크기(화면 작업 영역의 가로 40% · 세로 80%쯤)로, 오른쪽 아래 구석에 띄운다.
        Load += (_, _) =>
        {
            var wa = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
            int w = Math.Clamp((int)(wa.Width * 0.40), LogicalToDeviceUnits(640), Math.Min(wa.Width, LogicalToDeviceUnits(1100)));
            int h = Math.Clamp((int)(wa.Height * 0.80), LogicalToDeviceUnits(640), wa.Height);
            Size = new Size(w, h);
            Location = new Point(Math.Max(wa.Left, wa.Right - Width - 24), Math.Max(wa.Top, wa.Bottom - Height - 24));
        };
        FormClosing += (_, _) => doc.Close(); // 읽던 자리를 저장하고 닫는다
    }
}
