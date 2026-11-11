namespace CoffeeViewer;

/// <summary>
/// 하단 상태 표시줄 오른쪽 끝의 윈도우 메모장식 칸들: [줄 N, 열 M] [100%] [Windows (CRLF)] [UTF-8].
/// txt를 보고 있을 때만 나타난다. 왼쪽의 기존 안내 글(statusLabel)은 그대로 둔다.
/// </summary>
public partial class MainForm
{
    private StatusSegments? statusSegments;
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 200 };
    private int statusKeyOffset = -1, statusKeyLength = -1, statusKeyZoom = -1;
    private int countKeyLength = -1, charsAll, charsSolid;
    private string? countKeyText;

    private void InitStatusSegments()
    {
        statusSegments = new StatusSegments { Dock = DockStyle.Right, Visible = false, Separators = !embedded };
        statusLabel.Controls.Add(statusSegments);
        statusLabel.AutoEllipsis = true; // 안내 글이 길어도 오른쪽 칸 밑으로 들어가지 않게 말줄임
        statusTimer.Tick += (_, _) => RefreshStatusSegments();
        statusTimer.Start();
        FormClosed += (_, _) => statusTimer.Dispose();
    }

    /// <summary>
    /// 캐럿 위치·글자 크기가 바뀌었을 때만 다시 센다(TextBox에는 캐럿 이동 이벤트가 없어서 짧은 주기로 살핀다).
    /// 줄·열은 줄바꿈으로 접힌 화면 줄이 아니라 글 안의 실제 줄 기준이다. 페이지 방식이면 전체 글(fullText) 기준 위치로 센다.
    /// </summary>
    private void RefreshStatusSegments()
    {
        if (statusSegments == null || IsDisposed) return;
        SyncReadBarLabels();
        bool hideBar = embedded && !IsReferenceFile(currentPath); // 에피소드 · 위키 문서에는 맨 아래 상태 줄을 두지 않는다
        if (statusLabel.Visible == hideBar) statusLabel.Visible = !hideBar;
        bool show = shownKind == ContentKind.Txt && editorHost.Visible && Visible;
        if (show != statusSegments.Visible)
        {
            statusSegments.Visible = show;
            statusKeyOffset = -1;
            SyncStatusPadding();
        }
        if (!show) return;

        int sel = mainEditor.SelectionStart;
        int offset = pageMode ? currentPageStart + sel : sel;
        int zoom = ViewZoom;
        int length = pageMode ? fullText.Length : mainEditor.TextLength;
        if (offset == statusKeyOffset && length == statusKeyLength && zoom == statusKeyZoom) return;
        statusKeyOffset = offset;
        statusKeyLength = length;
        statusKeyZoom = zoom;

        string text = pageMode ? fullText : mainEditor.Text;
        offset = Math.Min(offset, text.Length);
        var before = text.AsSpan(0, offset);
        int line = before.Count('\n') + 1;
        int lastBreak = before.LastIndexOf('\n');
        int col = offset - (lastBreak + 1) + 1;

        if (length != countKeyLength || !string.Equals(currentPath, countKeyText))
        {
            // 글자 수(Muvel처럼 공백 포함 · 제외). 줄바꿈(CR LF)은 한 글자로 센다.
            int all = 0, solid = 0;
            foreach (char ch in text) { if (ch == '\r') continue; all++; if (!char.IsWhiteSpace(ch)) solid++; }
            charsAll = all; charsSolid = solid;
            UpdateGoalWidget(all, solid);
            countKeyLength = length; countKeyText = currentPath;
        }
        statusSegments.SetValues(line, col, zoom, charsAll, charsSolid);
        SyncStatusPadding();
    }

    private void SyncStatusPadding() =>
        statusLabel.Padding = new Padding(6, 0, statusSegments is { Visible: true } s ? s.Width : 0, 0);

    /// <summary>메모장의 상태 표시줄처럼, 칸 사이를 세로선으로 나눈 직접 그린 칸들.</summary>
    private sealed class StatusSegments : Control
    {
        private int line = 1, col = 1, zoom = 100, chars, solid;
        private Color back = SystemColors.Control, fore = SystemColors.ControlText;
        public bool Separators = true;

        // 줄바꿈 방식은 읽을 때 항상 \r\n으로 맞추고 저장도 그대로 하며, 글자 방식은 UTF-8로 저장하므로 그 값을 보여준다.
        private const string LineEnding = "Windows (CRLF)";
        private const string Encoding = "UTF-8";

        public StatusSegments()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public void ApplyColors(Color backColor, Color foreColor)
        {
            back = backColor;
            fore = foreColor;
            Invalidate();
        }

        public void SetValues(int newLine, int newCol, int newZoom, int newChars, int newSolid)
        {
            if (newLine == line && newCol == col && newZoom == zoom && newChars == chars && newSolid == solid) return;
            line = newLine; col = newCol; zoom = newZoom; chars = newChars; solid = newSolid;
            Width = Cells().Sum(c => c.Width);
            Invalidate();
        }

        private int Pad => (int)Math.Round(12 * DeviceDpi / 96.0);

        public int FirstCellWidth => Cells()[0].Width;

        /// <summary>각 칸의 글자와 폭. 숫자가 자라도 칸이 들썩이지 않게 줄·열 칸은 최소 폭을 둔다.</summary>
        private List<(string Text, int Width)> Cells()
        {
            int Measure(string s, int min) => Math.Max((int)Math.Round(min * DeviceDpi / 96.0), TextRenderer.MeasureText(s, Font).Width + Pad * 2);
            string pos = $"줄 {line:N0}, 열 {col:N0}";
            string count = $"{chars:N0}자 (공백 제외 {solid:N0}자)";
            return new()
            {
                (count, Measure(count, 170)),
                (pos, Measure(pos, 130)),
                ($"{zoom}%", Measure("100%", 64)),
                (LineEnding, Measure(LineEnding, 120)),
                (Encoding, Measure(Encoding, 80)),
            };
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Width = Cells().Sum(c => c.Width);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Width = Cells().Sum(c => c.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(back);
            using var sep = new Pen(Color.FromArgb(90, fore));
            int x = 0;
            var cells = Cells();
            for (int i = 0; i < cells.Count; i++)
            {
                var (text, w) = cells[i];
                if (i > 0 && Separators) g.DrawLine(sep, x, 3, x, Height - 4); // 칸 사이 세로선
                TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, w, Height), fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                x += w;
            }
        }
    }
}
