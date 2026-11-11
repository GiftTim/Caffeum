namespace CoffeeViewer;

/// <summary>
/// 서식창의 [낭독 · 자동 재생] 버튼을 누르면 뜨는 작은 창.
///   만화: 몇 초마다 다음 장으로 넘길지(슬라이드쇼)
///   epub · txt: 어떤 목소리로 얼마나 빠르게 읽을지(낭독)
/// 값은 바꾸는 즉시 설정에 저장하고 적용한다. [시작] / [정지]는 F5와 같다.
/// </summary>
public partial class MainForm
{
    private Form? playPopup;

    private void ShowPlayPopup()
    {
        if (playPopup is { IsDisposed: false }) { playPopup.Close(); return; } // 같은 버튼을 다시 누르면 닫기

        bool comic = imageMode;
        bool playing = ttsActive || slideshowTimer != null;
        var f = new Form
        {
            Text = comic ? "자동 넘김 설정" : "낭독 설정",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            MaximizeBox = false,
            MinimizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 10, 14, 12),
            Font = Font,
            BackColor = theme.Chrome,
            ForeColor = theme.ChromeFore,
        };
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        f.Controls.Add(table);
        int row = 0;
        Label Lbl(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) };
        void AddRow(string label, Control c)
        {
            table.RowCount = row + 1;
            table.Controls.Add(Lbl(label), 0, row);
            c.Margin = new Padding(0, 4, 0, 4);
            table.Controls.Add(c, 1, row);
            row++;
        }

        if (comic)
        {
            var sec = new NumericUpDown
            {
                Minimum = 1, Maximum = 120, Value = Math.Clamp(settings.SlideshowSeconds, 1, 120), Width = 70,
                BackColor = theme.Back, ForeColor = theme.Fore,
            };
            sec.ValueChanged += (_, _) => SetSlideshowInterval((int)sec.Value);
            var secRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            secRow.Controls.Add(sec);
            secRow.Controls.Add(new Label { Text = "초마다 다음 장으로", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
            AddRow("넘김 간격", secRow);
        }
        else
        {
            var voiceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, BackColor = theme.Back, ForeColor = theme.Fore };
            var voices = TtsEngine.InstalledVoices().ToList();
            if (voices.Count == 0) { voiceBox.Items.Add("(설치된 음성이 없습니다)"); voiceBox.SelectedIndex = 0; voiceBox.Enabled = false; }
            else
            {
                foreach (var v in voices) voiceBox.Items.Add(v);
                int at = settings.TtsVoice != null ? voices.IndexOf(settings.TtsVoice) : -1;
                voiceBox.SelectedIndex = at >= 0 ? at : 0;
                voiceBox.SelectedIndexChanged += (_, _) =>
                {
                    if (voiceBox.SelectedItem is not string name) return;
                    settings.TtsVoice = name;
                    settings.Save();
                    AppShared.NotifySettingsChanged("tts");
                };
            }
            AddRow("목소리", voiceBox);

            var rate = new TrackBar
            {
                Minimum = -10, Maximum = 10, TickFrequency = 5, SmallChange = 1, LargeChange = 2, Width = 190, AutoSize = false, Height = 30,
                Value = Math.Clamp(settings.TtsRate, -10, 10),
            };
            var rateLabel = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
            void ShowRate() => rateLabel.Text = rate.Value == 0 ? "보통" : rate.Value > 0 ? $"빠르게 +{rate.Value}" : $"느리게 {rate.Value}";
            ShowRate();
            rate.ValueChanged += (_, _) =>
            {
                ShowRate();
                settings.TtsRate = rate.Value;
                settings.Save();
                AppShared.NotifySettingsChanged("tts");
            };
            // 왼쪽 끝이 느리게, 오른쪽 끝이 빠르게. 가운데 현재 값은 슬라이더 밑에 보인다.
            var rateRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            rateRow.Controls.Add(new Label { Text = "느리게", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
            rateRow.Controls.Add(rate);
            rateRow.Controls.Add(new Label { Text = "빠르게", AutoSize = true, Margin = new Padding(4, 8, 0, 0) });
            rateLabel.Margin = new Padding(8, 8, 0, 0);
            rateRow.Controls.Add(rateLabel);
            AddRow("빠르기", rateRow);
        }

        var go = new Button
        {
            Text = playing ? "정지" : "시작 (F5)",
            AutoSize = true,
            Padding = new Padding(14, 3, 14, 3),
            Anchor = AnchorStyles.Right,
            FlatStyle = FlatStyle.System,
        };
        go.Click += (_, _) => { f.Close(); ToggleAutoPlay(); };
        table.RowCount = row + 1;
        table.Controls.Add(go, 1, row);

        // 단추 바로 밑에(화면 밖으로 나가지 않게) 띄운다.
        var from = shownKind == ContentKind.Txt ? (ToolStripItem?)lookTts : toolbarPlay; // 어느 서식창의 낭독 버튼을 눌렀는지
        var anchor = from?.Owner is { } owner && from.Available
            ? owner.RectangleToScreen(from.Bounds)
            : HostForm.RectangleToScreen(new Rectangle(Width / 2, 0, 1, 40));
        f.Load += (_, _) =>
        {
            var wa = Screen.FromRectangle(anchor).WorkingArea;
            int x = Math.Clamp(anchor.Left, wa.Left, Math.Max(wa.Left, wa.Right - f.Width));
            int y = Math.Min(anchor.Bottom + 4, Math.Max(wa.Top, wa.Bottom - f.Height));
            f.Location = new Point(x, y);
        };
        f.KeyPreview = true;
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += (_, _) => { if (ReferenceEquals(playPopup, f)) playPopup = null; f.Dispose(); };
        f.TopMost = HostForm.TopMost; // 항상 위인 창 위에도 뜨도록
        playPopup = f;
        f.Show(HostForm);
    }
}
