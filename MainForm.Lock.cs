namespace CoffeeViewer;

/// <summary>
/// 참고 자료 서식창의 [잠금 모드]. 켜면 문서 화면이 완전히 잠긴다 — 페이지 터치 · 페이지 넘김 · 스크롤 · 클릭 · 키 이동이 모두 안 먹고,
/// 화면을 누르거나 휠을 돌리거나 이동 키를 누르면 "현재 잠금모드 상태입니다"가 뜬다.
/// 웹 화면(WebView2)은 입력을 앱 메시지 루프를 거치지 않고 받아서, 문서 화면 위에 거의 투명한 작은 창을 덮어 마우스 · 터치를 대신 받는다.
/// </summary>
public partial class MainForm
{
    private LockOverlay? lockOverlay;
    private readonly System.Windows.Forms.Timer lockSyncTimer = new() { Interval = 150 };
    private DateTime lockToastAt = DateTime.MinValue;

    /// <summary>잠금을 켜거나 푼 뒤 화면 · 버튼 상태를 맞춘다.</summary>
    private void SetLockUi(bool on)
    {
        contentHost.Enabled = !on;   // 키보드 포커스 · 입력을 문서 화면이 못 받게
        pageNavPanel.Enabled = !on;  // 아래 이전/다음 쪽 줄도
        if (on) StopSlideshow();     // 자동 넘김도 멈춘다
        if (on)
        {
            if (lockOverlay == null)
            {
                lockOverlay = new LockOverlay();
                lockOverlay.Touched += LockToast;
                lockSyncTimer.Tick += (_, _) => SyncLockOverlay();
                FormClosed += (_, _) => { lockSyncTimer.Stop(); lockSyncTimer.Dispose(); lockOverlay?.Close(); lockOverlay = null; };
            }
            lockSyncTimer.Start();
            SyncLockOverlay();
        }
        else
        {
            lockSyncTimer.Stop();
            lockOverlay?.Hide();
        }
        SyncRefBarKind();
        SyncReadBarLabels();
    }

    /// <summary>덮개 창을 문서 화면 자리에 맞추고, 이 문서가 보이지 않는 동안(다른 탭 · 최소화)은 숨긴다.</summary>
    private void SyncLockOverlay()
    {
        if (lockOverlay == null) return;
        var host = HostForm;
        bool show = lockMode && IsHandleCreated && Visible && contentHost.Visible && contentHost.IsHandleCreated
                    && host.Visible && host.WindowState != FormWindowState.Minimized;
        if (!show) { if (lockOverlay.Visible) lockOverlay.Hide(); return; }
        var r = contentHost.RectangleToScreen(contentHost.ClientRectangle);
        if (lockOverlay.Bounds != r) lockOverlay.Bounds = r;
        if (!lockOverlay.Visible) lockOverlay.Show(host);
    }

    /// <summary>"현재 잠금모드 상태입니다" — 연달아 눌러도 안내가 쌓이지 않게 1.2초에 한 번만.</summary>
    private void LockToast()
    {
        if (!lockMode || (DateTime.Now - lockToastAt).TotalMilliseconds < 1200) return;
        lockToastAt = DateTime.Now;
        ShowToast("현재 잠금모드 상태입니다");
    }

    /// <summary>잠금 중에 쪽을 넘기거나 움직이는 키는 막고 안내를 띄운다.</summary>
    private bool BlockedByLock(Keys key)
    {
        if (!lockMode) return false;
        if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space
            or (Keys.Shift | Keys.Space))
        {
            LockToast();
            return true;
        }
        return false;
    }

    /// <summary>문서 화면 위에 덮는 거의 투명한 창: 눌림 · 휠을 받아 알리기만 한다(활성화되지 않고, 작업표시줄에도 안 뜬다).</summary>
    private sealed class LockOverlay : Form
    {
        public event Action? Touched;

        public LockOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.01; // 거의 안 보이지만 마우스는 받는다
            Cursor = Cursors.No;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Touched?.Invoke(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Touched?.Invoke(); }
    }
}
