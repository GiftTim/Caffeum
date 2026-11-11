namespace CoffeeViewer;

/// <summary>
/// 화면 아래쪽에 잠깐 떴다 사라지는 짧은 알림("이어서 읽습니다", "낭독 시작" 등). 대화상자처럼 흐름을 끊지 않으려고 쓴다.
/// 자리는 MainForm이 잡아준다(RepositionOverlays).
/// </summary>
public sealed class ToastLabel : Label
{
    private readonly System.Windows.Forms.Timer hideTimer = new();

    public ToastLabel()
    {
        AutoSize = true;
        Visible = false;
        Padding = new Padding(16, 9, 16, 9);
        BackColor = Color.FromArgb(0x2b, 0x2b, 0x2b);
        ForeColor = Color.White;
        Font = new Font("맑은 고딕", 10f);
        TextAlign = ContentAlignment.MiddleCenter;
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Visible = false; };
    }

    public void ShowMessage(string text, int milliseconds = 2200)
    {
        Text = text;
        Visible = true;
        BringToFront();
        hideTimer.Stop();
        hideTimer.Interval = milliseconds;
        hideTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
