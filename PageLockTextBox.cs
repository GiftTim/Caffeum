using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// 본문 상자(서식 지원): 글자마다 진하게 · 기울임 · 밑줄 · 취소선 · 글자색을 줄 수 있는 RichTextBox를 TextBox처럼 쓰도록 맞춘 것.
/// 페이지 방식에서는 스크롤을 완전히 막는다(터치 패닝은 스크롤 메시지로 오기도 해서 메시지 단계에서 거른다).
/// 휠은 스크롤 대신 WheelTurned로 알려 준다(MainForm이 페이지를 넘긴다).
/// 줄바꿈은 상자 안에서 \n 한 글자라, 파일을 읽고 쓸 때만 \r\n과 바꾼다(MainForm).
/// </summary>
internal sealed class PageLockTextBox : RichTextBox
{
    private const int WM_VSCROLL = 0x0115, WM_HSCROLL = 0x0114, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_PASTE = 0x0302;
    private const int EM_SETRECT = 0x00B3;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public PageLockTextBox()
    {
        DetectUrls = false;       // 주소를 저절로 링크로 바꾸지 않는다
        AutoWordSelection = false;
        HideSelection = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ScrollLocked { get; set; }

    /// <summary>집필 화면에서 이 상자를 머리와 함께 스크롤시키는 패널(없으면 상자가 스스로 스크롤한다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal CoffeeViewer.TallScroller? TallHost { get; set; }

    public new void ScrollToCaret()
    {
        if (TallHost != null) TallHost.EnsureCaretVisible(); else base.ScrollToCaret();
    }

    /// <summary>TextBox의 ScrollBars처럼 쓰게 해 주는 껍데기(RichTextBox는 종류가 달라서).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new System.Windows.Forms.ScrollBars ScrollBars
    {
        get => base.ScrollBars switch
        {
            RichTextBoxScrollBars.None => System.Windows.Forms.ScrollBars.None,
            RichTextBoxScrollBars.Horizontal or RichTextBoxScrollBars.ForcedHorizontal => System.Windows.Forms.ScrollBars.Horizontal,
            RichTextBoxScrollBars.Both or RichTextBoxScrollBars.ForcedBoth => System.Windows.Forms.ScrollBars.Both,
            _ => System.Windows.Forms.ScrollBars.Vertical,
        };
        set => base.ScrollBars = value switch
        {
            System.Windows.Forms.ScrollBars.None => RichTextBoxScrollBars.None,
            System.Windows.Forms.ScrollBars.Horizontal => RichTextBoxScrollBars.Horizontal,
            System.Windows.Forms.ScrollBars.Both => RichTextBoxScrollBars.Both,
            _ => RichTextBoxScrollBars.Vertical,
        };
    }

    private HorizontalAlignment align = HorizontalAlignment.Left;

    /// <summary>글 전체의 정렬(TextBox.TextAlign처럼). 선택한 자리는 그대로 둔다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public HorizontalAlignment TextAlign
    {
        get => align;
        set
        {
            bool same = align == value;
            align = value;
            if (!IsHandleCreated) return;
            if (same && value == HorizontalAlignment.Left) return; // 기본(왼쪽)이면 건드릴 필요가 없다
            int s = SelectionStart, l = SelectionLength;
            SelectAll();
            SelectionAlignment = value;
            Select(s, l);
        }
    }

    // ───────── 문단 서식(정렬 · 여백 · 첫 줄 들여쓰기/내어쓰기 · 줄/문단 간격) ─────────
    private const int EM_SETPARAFORMAT = 0x0447, EM_SETSEL = 0x00B1;
    private const uint PFM_STARTINDENT = 0x1, PFM_RIGHTINDENT = 0x2, PFM_OFFSET = 0x4, PFM_ALIGNMENT = 0x8, PFM_SPACEBEFORE = 0x40, PFM_SPACEAFTER = 0x80, PFM_LINESPACING = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct PARAFORMAT2
    {
        public uint cbSize, dwMask;
        public ushort wNumbering, wEffects;
        public int dxStartIndent, dxRightIndent, dxOffset;
        public ushort wAlignment;
        public short cTabCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public int[] rgxTabs;
        public int dySpaceBefore, dySpaceAfter, dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule, bOutlineLevel;
        public ushort wShadingWeight, wShadingStyle, wNumberingStart, wNumberingStyle, wNumberingTab, wBorderSpace, wBorderWidth, wBorders;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref PARAFORMAT2 f);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private string? lastParaKey;

    /// <summary>문단 서식을 모든 문단에 적용한다(새로 치는 문단은 앞 문단의 서식을 이어받는다). 글을 새로 넣은 뒤에는 force로 다시 건다.</summary>
    public void ApplyParaFormat(CoffeeViewer.ParaFormat p, bool force = false)
    {
        if (!IsHandleCreated) return;
        string key = string.Join("|", Font.SizeInPoints, Font.Name, p.Align, p.Left, p.Right, p.FirstMode, p.FirstPt, p.LineMode, p.LinePercent, p.LinePt, p.Before, p.After);
        if (!force && key == lastParaKey) return;
        lastParaKey = key;
        double first = p.FirstMode == 1 ? p.FirstPt : p.FirstMode == 2 ? -p.FirstPt : 0;
        var f = new PARAFORMAT2
        {
            cbSize = (uint)Marshal.SizeOf<PARAFORMAT2>(),
            dwMask = PFM_STARTINDENT | PFM_RIGHTINDENT | PFM_OFFSET | PFM_ALIGNMENT | PFM_SPACEBEFORE | PFM_SPACEAFTER | PFM_LINESPACING,
            // 들여쓰기(first > 0): 첫 줄만 안으로(시작 = 왼쪽 + X, 나머지 줄은 -X) · 내어쓰기(first < 0): 첫 줄은 그대로, 나머지 줄을 안으로(+X)
            dxStartIndent = (int)((p.Left + (first > 0 ? first : 0)) * 20),
            dxOffset = (int)(-first * 20),
            dxRightIndent = (int)(p.Right * 20),
            wAlignment = (ushort)(p.Align switch { "center" => 3, "right" => 2, "justify" or "distribute" or "divide" => 4, _ => 1 }),
            dySpaceBefore = (int)(p.Before * 20),
            dySpaceAfter = (int)(p.After * 20),
            rgxTabs = new int[32],
        };
        if (p.LineMode == 1) { f.bLineSpacingRule = 4; f.dyLineSpacing = (int)(p.LinePt * 20); }      // 고정 값(pt)
        else
        {
            // 한글처럼 "글자에 따라 160%" = 줄 높이가 글자 크기의 160%. RichEdit의 "n줄"은 글꼴의 한 줄 높이(글자 크기보다 큼)를 기준으로 하니,
            // 글자 크기에 퍼센트를 곱한 값을 "정확히(twips)"로 지정해서 같은 줄 간격이 되게 한다.
            f.bLineSpacingRule = 4;
            f.dyLineSpacing = Math.Max(20, (int)Math.Round(p.LinePercent / 100.0 * Font.SizeInPoints * 20));
        }
        int s = SelectionStart, l = SelectionLength;
        SendMessage(Handle, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
        SendMessage(Handle, EM_SETPARAFORMAT, IntPtr.Zero, ref f);
        SendMessage(Handle, EM_SETSEL, new IntPtr(s), new IntPtr(s + l));
    }

    // ───────── 좌우 여백 ─────────
    private int sideMargin;

    /// <summary>글이 놓이는 칸의 좌우 안쪽 여백(px). 상자 크기가 바뀌어도 다시 맞춘다.</summary>
    public void SetSideMargins(int m)
    {
        sideMargin = Math.Max(0, m);
        ApplySideMargins();
    }

    private void ApplySideMargins()
    {
        if (!IsHandleCreated) return;
        var r = new RECT { Left = sideMargin, Top = 0, Right = Math.Max(sideMargin + 10, ClientSize.Width - sideMargin), Bottom = ClientSize.Height };
        SendMessage(Handle, EM_SETRECT, IntPtr.Zero, ref r);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplySideMargins(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); ApplySideMargins(); }

    // ───────── 글자 서식(선택한 글자 · 커서 이후) ─────────

    /// <summary>진하게 · 기울임 · 밑줄 · 취소선을 켜거나 끈다. 글자를 골랐으면 그 글자들에, 안 골랐으면 커서 이후에 칠 글자에 적용한다.
    /// 골라 놓은 글자가 전부 이미 그 서식이면 해제하고, 아니면 전부 적용한다.</summary>
    public void ToggleStyle(FontStyle style)
    {
        bool on = !HasStyle(style);
        ApplyStyle(style, on);
    }

    /// <summary>지금 선택(또는 커서)의 글자가 그 서식인가(섞여 있으면 false).</summary>
    public bool HasStyle(FontStyle style)
    {
        var f = SelectionFont;
        return f != null && (f.Style & style) == style;
    }

    private void ApplyStyle(FontStyle style, bool on)
    {
        if (SelectionLength == 0)
        {
            var f = SelectionFont ?? Font;
            SelectionFont = new Font(f, on ? f.Style | style : f.Style & ~style);
            return;
        }
        // 서식이 섞인 선택은 글자 조각(같은 서식이 이어지는 구간)마다 따로 바꾼다(조각마다 글꼴 크기가 달라도 유지)
        int start = SelectionStart, len = SelectionLength;
        var prev = IsHandleCreated;
        SuspendLayout();
        try
        {
            int pos = start, end = start + len;
            while (pos < end)
            {
                Select(pos, 1);
                var f = SelectionFont ?? Font;
                int runEnd = pos + 1;
                while (runEnd < end)
                {
                    Select(runEnd, 1);
                    var g = SelectionFont;
                    if (g == null || g.Style != f.Style || g.FontFamily.Name != f.FontFamily.Name || Math.Abs(g.Size - f.Size) > 0.01f) break;
                    runEnd++;
                }
                Select(pos, runEnd - pos);
                SelectionFont = new Font(f, on ? f.Style | style : f.Style & ~style);
                pos = runEnd;
            }
        }
        finally { ResumeLayout(); }
        Select(start, len);
    }

    /// <summary>글자색을 고른 글자(없으면 커서 이후)에 적용한다.</summary>
    public void ApplyColor(Color c) => SelectionColor = c;

    // ───────── 스크롤 · 붙여넣기 ─────────

    /// <summary>집필 화면에서 이 상자를 머리와 함께 스크롤시키는 패널에서 휠을 건네받는다.</summary>
    public event Action<int>? WheelTurned;

    protected override void WndProc(ref Message m)
    {
        if (TallHost != null && m.Msg == WM_MOUSEWHEEL && (ModifierKeys & Keys.Control) == 0)
        {
            TallHost.WheelScroll((short)((long)m.WParam >> 16));
            return;
        }
        if (m.Msg == WM_PASTE && !ReadOnly)
        {
            // 붙여넣기는 글자만(다른 곳의 글꼴 · 색 · 그림은 가져오지 않는다)
            try { if (Clipboard.ContainsText()) SelectedText = Clipboard.GetText().Replace("\r\n", "\n"); } catch { }
            return;
        }
        if (ScrollLocked)
        {
            switch (m.Msg)
            {
                case WM_MOUSEWHEEL:
                    if ((ModifierKeys & Keys.Control) != 0) break; // Ctrl+휠은 글자 크기 조절(MouseWheel 이벤트)
                    WheelTurned?.Invoke((short)((long)m.WParam >> 16));
                    return;
                case WM_MOUSEHWHEEL:
                case WM_VSCROLL:
                case WM_HSCROLL:
                    return;
            }
        }
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && Placeholder != null && TextLength == 0) DrawPlaceholder();
    }

    // ───────── 빈 글 안내 ─────────

    private const int WM_PAINT = 0x000F;
    [DllImport("user32.dll")] private static extern bool HideCaret(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowCaret(IntPtr h);

    /// <summary>글이 비어 있을 때 흐리게 그리는 안내 글(null이면 안 그린다). 따로 얹은 Label은 깜빡이는 커서를 가려 조각이 남아서, 상자가 직접 그린다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Placeholder { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color PlaceholderColor { get; set; } = SystemColors.GrayText;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Point PlaceholderAt { get; set; }

    private void DrawPlaceholder()
    {
        bool hid = Focused && HideCaret(Handle); // 그리는 동안 커서를 잠깐 숨겨야 커서 자국이 안 남는다
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            TextRenderer.DrawText(g, Placeholder, Font, PlaceholderAt, PlaceholderColor, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        finally { if (hid) ShowCaret(Handle); }
    }
}
