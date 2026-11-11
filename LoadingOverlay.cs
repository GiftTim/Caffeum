namespace CoffeeViewer;

/// <summary>
/// 큰 파일(용량이 큰 zip 만화, 이미지가 많은 epub, 긴 txt 등)을 불러올 때 걸리는 시간을 게이지로 보여주는
/// 오버레이. contentHost 위에 겹쳐서 뜨고, 얼마나 걸릴지 모르는 구간(파일 목록을 훑는 중 등)에서는
/// 마퀴(왔다갔다) 막대로, 진행률을 알 수 있는 구간(바이트 단위로 읽는 중)에서는 꽉 찬 막대 + 남은
/// 시간 추정으로 바뀐다. 금방 끝나는 파일에서는(지연 시간 안에 Finish가 불리면) 아예 화면에 나타나지
/// 않아서 깜빡이지 않는다.
/// </summary>
public sealed class LoadingOverlay : Panel
{
    private readonly Label titleLabel;
    private readonly ProgressBar progressBar;
    private readonly Label detailLabel;
    private readonly Button cancelButton;
    private readonly Panel card;
    private readonly System.Windows.Forms.Timer showDelayTimer;
    private readonly System.Windows.Forms.Timer keepOnTopTimer; // 다른 컨트롤이 뒤늦게 앞으로 올라와도 게이지가 항상 맨 위에 있도록 주기적으로 다시 올려준다
    private DateTime startedAt;

    // 이 시간보다 빨리 끝나는 파일이면 아예 안 보여준다 — 매번 반짝였다 사라지면 오히려 거슬린다.
    private const int ShowDelayMs = 300;

    /// <summary>사용자가 "취소" 버튼을 눌렀다. 실제로 불러오기를 멈추는 건 호출한 쪽(MainForm)의 몫이다.</summary>
    public event Action? CancelRequested;

    public LoadingOverlay()
    {
        Visible = false;
        BackColor = Color.FromArgb(160, 255, 255, 255); // 반투명한 흰 배경 — 뒤 내용이 살짝 비친다

        titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
        };
        progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
        };
        detailLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
        };
        cancelButton = new Button
        {
            Text = "취소",
            Dock = DockStyle.Bottom,
            Height = 32,
        };
        cancelButton.Click += (_, _) => CancelRequested?.Invoke();
        card = new Panel
        {
            Width = 320,
            Height = 122,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };
        card.Controls.Add(detailLabel);
        card.Controls.Add(progressBar);
        card.Controls.Add(titleLabel);
        card.Controls.Add(cancelButton);
        Controls.Add(card);

        // 오래 걸리는 파일일수록 그동안 메뉴를 열거나 다른 조작을 할 기회가 많아지고, 그런 조작 중
        // 일부가 다른 컨트롤을 앞으로 올릴 수 있다(예: RepositionOverlays가 화살표를 다시 올리는 경우).
        // 그래서 보이는 동안은 이 타이머로 계속 맨 위를 다시 주장한다.
        keepOnTopTimer = new System.Windows.Forms.Timer { Interval = 250 };
        keepOnTopTimer.Tick += (_, _) => { if (Visible) BringToFront(); };

        showDelayTimer = new System.Windows.Forms.Timer { Interval = ShowDelayMs };
        showDelayTimer.Tick += (_, _) =>
        {
            showDelayTimer.Stop();
            CenterCard();
            Visible = true;
            BringToFront();
            keepOnTopTimer.Start();
        };

        Resize += (_, _) => CenterCard();
    }

    private void CenterCard()
    {
        card.Left = (Width - card.Width) / 2;
        card.Top = (Height - card.Height) / 2;
    }

    /// <summary>불러오기를 시작한다. 진행률을 아직 모르면 Report를 부르기 전까지는 마퀴로 보인다.</summary>
    public void Begin(string title)
    {
        startedAt = DateTime.UtcNow;
        titleLabel.Text = title;
        detailLabel.Text = "";
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.Value = 0;
        Visible = false; // 지연 타이머가 끝나야 실제로 보인다(짧게 끝나면 아예 안 보임)
        CenterCard();
        showDelayTimer.Stop();
        showDelayTimer.Start();
    }

    /// <summary>0.0~1.0 진행률 보고. 어느 정도 지나야(그래야 추정이 의미 있어서) 남은 시간도 같이 보여준다.</summary>
    public void Report(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        if (progressBar.Style != ProgressBarStyle.Continuous) progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Value = (int)Math.Round(fraction * 100);

        int percent = (int)Math.Round(fraction * 100);
        double elapsedSec = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (fraction > 0.02 && elapsedSec > 0.3)
        {
            double remainSec = Math.Max(0, elapsedSec / fraction - elapsedSec);
            detailLabel.Text = $"{percent}% · 약 {FormatSeconds(remainSec)} 남음";
        }
        else
        {
            detailLabel.Text = $"{percent}%";
        }
        if (Visible) BringToFront();
    }

    private static string FormatSeconds(double sec)
    {
        if (sec < 1) return "1초 미만";
        if (sec < 60) return $"{(int)Math.Ceiling(sec)}초";
        int min = (int)(sec / 60);
        int remSec = (int)Math.Ceiling(sec - min * 60);
        return remSec > 0 ? $"{min}분 {remSec}초" : $"{min}분";
    }

    /// <summary>불러오기가 끝났다(성공/실패/취소 상관없이). 아직 안 보이던 중이었다면 지연 타이머도 같이 멈춘다.</summary>
    public void Finish()
    {
        showDelayTimer.Stop();
        keepOnTopTimer.Stop();
        Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            showDelayTimer.Dispose();
            keepOnTopTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
