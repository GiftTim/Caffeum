using System.Drawing.Drawing2D;
using System.Text;

namespace CoffeeViewer;

/// <summary>
/// 윈도우 11 메모장의 찾기 바를 흉내낸 오버레이.
/// 1행: ⌄/˄ 펼치기·접기, 찾기 입력칸(지우기 ×, 돋보기), ↓ 다음, ↑ 이전, 옵션(대/소문자 구분·줄바꿈), ✕ 닫기
/// 2행(펼쳤을 때만): 바꿀 내용 입력칸, 바꾸기, 모두 바꾸기
/// 본문 위에 떠 있는 오버레이라 mainEditor의 폭/내용은 전혀 건드리지 않는다.
/// 모양은 전부 코드로 그린다(윈도우 버전·글꼴·시스템 테마와 무관). 색은 읽기 화면 테마가 어두운지 밝은지에 따라 바뀐다(ApplyTheme).
/// </summary>
public class FindBar : Panel
{
    private const string FindPlaceholder = "찾기";
    private const string ReplacePlaceholder = "바꾸기";
    private const int CornerRadius = 8;
    private const int CollapsedHeight = 44;
    private const int ExpandedHeight = 88;

    private const int FieldLeft = 42;
    private const int FindFieldWidth = 338;
    private const int ReplaceFieldWidth = 304;
    private const int FieldHeight = 32;
    private const int FieldTop = 6;

    /// <summary>찾기 바에 쓰는 색 한 벌.</summary>
    private sealed record Palette(Color Bar, Color BarBorder, Color Field, Color FieldBorder, Color FieldLine, Color Accent,
        Color Text, Color Hint, Color Glyph, Color Hover, Color Pressed);

    private static readonly Palette LightPalette = new(
        Bar: Color.FromArgb(251, 251, 251), BarBorder: Color.Black,
        Field: Color.White, FieldBorder: Color.FromArgb(226, 226, 226), FieldLine: Color.FromArgb(138, 138, 138), Accent: Color.FromArgb(0, 95, 184),
        Text: Color.FromArgb(27, 27, 27), Hint: Color.FromArgb(120, 120, 120), Glyph: Color.FromArgb(50, 50, 50),
        Hover: Color.FromArgb(238, 238, 238), Pressed: Color.FromArgb(228, 228, 228));

    private static readonly Palette DarkPalette = new(
        Bar: Color.FromArgb(40, 40, 40), BarBorder: Color.White,
        Field: Color.FromArgb(52, 52, 52), FieldBorder: Color.FromArgb(68, 68, 68), FieldLine: Color.FromArgb(160, 160, 160), Accent: Color.FromArgb(96, 205, 255),
        Text: Color.FromArgb(240, 240, 240), Hint: Color.FromArgb(165, 165, 165), Glyph: Color.FromArgb(225, 225, 225),
        Hover: Color.FromArgb(62, 62, 62), Pressed: Color.FromArgb(74, 74, 74));

    private Palette pal = LightPalette;

    private readonly TextBoxBase target;
    private readonly Func<string> getText;
    private readonly Func<int> getSelectionStart;
    private readonly Func<int> getSelectionEnd;
    private readonly Action<int, int> selectRange;
    private readonly Action focusBody; // 찾기 바를 닫을 때 포커스를 돌려줄 본문(txt 웹 화면이면 target이 아니라 웹뷰)
    private readonly TextBox queryBox;
    private readonly TextBox replaceBox;
    private readonly FieldPanel queryField;
    private readonly FieldPanel replaceField;
    private readonly GlyphButton collapseBtn, downBtn, upBtn, optionsBtn, closeBtn;
    private readonly RoundButton replaceBtn, replaceAllBtn;
    private readonly Panel replaceRow;
    private readonly ContextMenuStrip optionsMenu;
    private readonly ToolStripMenuItem matchCaseItem;
    private readonly ToolStripMenuItem wrapAroundItem;

    private bool expanded = false;

    /// <summary>
    /// getText/getSelectionStart/getSelectionEnd/selectRange를 안 주면(기본값) target(mainEditor)의
    /// Text/Selection을 그대로 쓴다 — 지금까지의 동작과 같다.
    /// 페이지 모드에서는 mainEditor.Text가 지금 페이지 조각만 담고 있어서 그걸 그대로 검색하면
    /// 다른 페이지에 있는 내용을 못 찾으므로, MainForm이 이 델리게이트들을 통해 "전체 글 기준"
    /// 텍스트/위치를 넘겨주고, 찾은 위치가 지금 페이지 밖이면 그 페이지로 먼저 이동하게 한다.
    /// </summary>
    public FindBar(TextBoxBase target, Func<string>? getText = null, Func<int>? getSelectionStart = null,
        Func<int>? getSelectionEnd = null, Action<int, int>? selectRange = null, Action? focusBody = null)
    {
        this.target = target;
        this.focusBody = focusBody ?? (() => target.Focus());
        this.getText = getText ?? (() => target.Text);
        this.getSelectionStart = getSelectionStart ?? (() => target.SelectionStart);
        this.getSelectionEnd = getSelectionEnd ?? (() => target.SelectionStart + target.SelectionLength);
        this.selectRange = selectRange ?? ((start, length) =>
        {
            target.Focus();
            target.Select(start, length);
            target.ScrollToCaret();
        });

        Anchor = AnchorStyles.Top | AnchorStyles.Right;
        DoubleBuffered = true;
        // 높이가 바뀔 때(펼치기·접기) 테두리를 전체 다시 그려야 옛 높이의 아래 테두리가 남거나 새 아래 테두리가 빠지지 않는다.
        SetStyle(ControlStyles.ResizeRedraw, true);

        // ── 1행 ── (위치·크기는 화면 배율에 맞춰 LayoutAll이 정한다)
        collapseBtn = new GlyphButton { Kind = GlyphKind.ChevronDown, TabStop = false }; // ChevronDown(접힘: 눌러서 펼치기) / ChevronUp(펼침: 눌러서 접기)로 토글
        collapseBtn.Click += (_, _) => SetExpanded(!expanded);

        queryField = new FieldPanel(this, FindPlaceholder, withIcons: true);
        queryBox = queryField.Box;
        queryBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { FindNext(forward: true); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) Hide();
        };

        downBtn = new GlyphButton { Kind = GlyphKind.Down, TabStop = false };
        downBtn.Click += (_, _) => FindNext(forward: true);

        upBtn = new GlyphButton { Kind = GlyphKind.Up, TabStop = false };
        upBtn.Click += (_, _) => FindNext(forward: false);

        matchCaseItem = new ToolStripMenuItem("대/소문자 구분(C)") { CheckOnClick = true };
        // 켜면(기본값) 문서 끝/처음에 닿았을 때 반대쪽부터 다시 찾는다. 끄면 거기서 멈춘다.
        wrapAroundItem = new ToolStripMenuItem("줄바꿈 (문서 전체를 순환 검색)") { CheckOnClick = true, Checked = true };
        optionsMenu = new ContextMenuStrip();
        optionsMenu.Items.Add(matchCaseItem);
        optionsMenu.Items.Add(wrapAroundItem);

        optionsBtn = new GlyphButton { Kind = GlyphKind.Options, TabStop = false };
        optionsBtn.Click += (_, _) => optionsMenu.Show(optionsBtn, new Point(0, optionsBtn.Height));

        closeBtn = new GlyphButton { Kind = GlyphKind.Close, TabStop = false };
        closeBtn.Click += (_, _) => Hide();

        Controls.Add(collapseBtn);
        Controls.Add(queryField);
        Controls.Add(downBtn);
        Controls.Add(upBtn);
        Controls.Add(optionsBtn);
        Controls.Add(closeBtn);

        // ── 2행(바꾸기) — 펼쳤을 때만 보인다 ──
        replaceRow = new Panel { Visible = false };
        replaceField = new FieldPanel(this, ReplacePlaceholder, withIcons: false);
        replaceBox = replaceField.Box;
        replaceBtn = new RoundButton(this, "바꾸기");
        replaceAllBtn = new RoundButton(this, "모두 바꾸기");
        replaceBtn.Click += (_, _) => ReplaceCurrent();
        replaceAllBtn.Click += (_, _) => ReplaceAll();
        replaceRow.Controls.Add(replaceField);
        replaceRow.Controls.Add(replaceBtn);
        replaceRow.Controls.Add(replaceAllBtn);
        Controls.Add(replaceRow);

        Resize += (_, _) => ApplyRoundedRegion();
        Paint += (_, e) => DrawBorder(e.Graphics);
        LayoutAll();
        ApplyPalette();
    }

    /// <summary>화면 배율(DPI)에 맞춘 픽셀 값. 글자는 배율을 따라 커지므로 칸·버튼·아이콘도 같은 비율로 키운다.</summary>
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private float Scale => DeviceDpi / 96f;

    /// <summary>모든 부품의 위치·크기를 현재 배율로 다시 정한다. 오른쪽 끝은 그대로 두고 왼쪽으로 늘어난다.</summary>
    private void LayoutAll()
    {
        int right = Right;
        bool hadRight = IsHandleCreated || Parent != null;
        SuspendLayout();

        Size = new Size(S(520), expanded ? S(ExpandedHeight) : S(CollapsedHeight));
        if (hadRight) Left = right - Width;

        void Icon(GlyphButton b, int left) { b.SetBounds(S(left), S(7), S(30), S(30)); }
        Icon(collapseBtn, 6);
        Icon(downBtn, 387);
        Icon(upBtn, 418);
        Icon(optionsBtn, 449);
        Icon(closeBtn, 480);
        queryField.SetBounds(S(FieldLeft), S(FieldTop), S(FindFieldWidth), S(FieldHeight));

        // 바꾸기 줄은 바깥 테두리(좌우·아래)를 덮지 않도록 안쪽으로 조금 들여서 놓는다. 자식 좌표는 이 줄 기준이라 들인 만큼 뺀다.
        int inset = S(2);
        replaceRow.SetBounds(inset, S(CollapsedHeight), Width - inset * 2, S(ExpandedHeight - CollapsedHeight) - S(4));
        // 오른쪽 끝부터 [모두 바꾸기] ← 간격 ← [바꾸기] ← 간격 ← 입력칸 순으로 놓는다. 버튼 글자가 배율·글꼴에 따라 길어지면
        // 버튼이 넓어지는 만큼 입력칸이 줄어들어, 버튼끼리는 늘 일정한 틈(gap)을 둔다.
        int gap = S(10);
        int allW = Math.Max(S(78), TextRenderer.MeasureText(replaceAllBtn.Text, replaceAllBtn.Font).Width + S(24));
        int oneW = Math.Max(S(66), TextRenderer.MeasureText(replaceBtn.Text, replaceBtn.Font).Width + S(24));
        int allLeft = Width - S(16) - allW - inset;
        int oneLeft = allLeft - gap - oneW;
        int fieldLeft = S(FieldLeft) - inset;
        replaceAllBtn.SetBounds(allLeft, S(FieldTop), allW, S(FieldHeight));
        replaceBtn.SetBounds(oneLeft, S(FieldTop), oneW, S(FieldHeight));
        replaceField.SetBounds(fieldLeft, S(FieldTop), Math.Max(S(120), oneLeft - gap - fieldLeft), S(FieldHeight));

        ResumeLayout(true);
        ApplyRoundedRegion();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutAll();
        queryField.ApplyPalette();
        replaceField.ApplyPalette();
    }

    /// <summary>읽기 화면 테마가 어두우면 어둡게, 밝으면 밝게 칠한다.</summary>
    public void ApplyTheme(ReaderTheme t)
    {
        pal = t.Dark ? DarkPalette : LightPalette;
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        BackColor = pal.Bar;
        replaceRow.BackColor = pal.Bar;
        queryField.ApplyPalette();
        replaceField.ApplyPalette();
        InvalidateAll(this);
    }

    private static void InvalidateAll(Control c)
    {
        c.Invalidate();
        foreach (Control child in c.Controls) InvalidateAll(child);
    }

    // ───────────────────────── 직접 그리는 부품들 ─────────────────────────

    /// <summary>둥근 입력칸: 바탕 + 아래쪽 선(포커스가 오면 강조색 2px) + 안에 든 글상자. 글상자는 칸 높이의 가운데에 놓는다.</summary>
    private sealed class FieldPanel : Panel
    {
        private readonly FindBar bar;
        private readonly string placeholder;
        private readonly GlyphButton? clearBtn;
        private readonly GlyphButton? searchIcon;
        public TextBox Box { get; }

        public FieldPanel(FindBar bar, string placeholder, bool withIcons)
        {
            this.bar = bar;
            this.placeholder = placeholder;
            DoubleBuffered = true;

            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("맑은 고딕", 10f),
                Text = placeholder,
            };
            Controls.Add(Box);

            if (withIcons)
            {
                // 입력칸 안쪽 오른쪽: [×(글자가 있을 때만)] [돋보기]
                clearBtn = new GlyphButton(bar) { Kind = GlyphKind.Clear, TabStop = false, Visible = false };
                clearBtn.Click += (_, _) => { Box.Text = ""; Box.Focus(); };
                searchIcon = new GlyphButton(bar) { Kind = GlyphKind.Search, TabStop = false, Enabled = false };
                Controls.Add(clearBtn);
                Controls.Add(searchIcon);
            }

            Box.Enter += (_, _) =>
            {
                if (Box.Text == placeholder) Box.Text = "";
                Refresh2();
            };
            Box.Leave += (_, _) =>
            {
                if (string.IsNullOrEmpty(Box.Text)) Box.Text = placeholder;
                Refresh2();
            };
            Box.TextChanged += (_, _) => Refresh2();
            Resize += (_, _) => LayoutInner();
        }

        private bool ShowingPlaceholder => !Box.Focused && Box.Text == placeholder;

        private void LayoutInner()
        {
            int icon = bar.S(28);
            int right = Width - bar.S(4);
            if (searchIcon != null) { searchIcon.SetBounds(right - icon, (Height - icon) / 2, icon, icon); right -= icon; }
            if (clearBtn != null) { clearBtn.SetBounds(right - icon, (Height - icon) / 2, icon, icon); right -= icon; }
            Box.Left = bar.S(10);
            Box.Width = Math.Max(20, right - Box.Left - bar.S(4));
            // 맑은 고딕은 글자가 줄 높이 안에서 아래쪽에 앉는 편이라, 글상자를 가운데보다 1px 위로 올려야 눈에는 가운데로 보인다.
            Box.Top = (Height - Box.Height) / 2 - 1;
        }

        public void ApplyPalette()
        {
            Box.BackColor = bar.pal.Field;
            LayoutInner();
            Refresh2();
        }

        private void Refresh2()
        {
            Box.ForeColor = ShowingPlaceholder ? bar.pal.Hint : bar.pal.Text;
            if (clearBtn != null) clearBtn.Visible = Box.Focused && Box.Text.Length > 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = bar.pal;
            g.Clear(p.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = bar.Scale;
            using var path = RoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), bar.S(5));
            using var fill = new SolidBrush(p.Field);
            using var border = new Pen(p.FieldBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            // 아래쪽 선: 평소엔 가는 회색, 입력 중이면 강조색 2px(배율만큼 굵게)
            bool focused = Box.Focused;
            float w = (focused ? 2f : 1f) * s;
            using var line = new Pen(focused ? p.Accent : p.FieldLine, w);
            float y = Height - w / 2f - 0.2f;
            g.DrawLine(line, 4 * s, y, Width - 5 * s, y);
        }
    }

    /// <summary>바꾸기 / 모두 바꾸기 버튼: 둥근 사각형 + 가운데 글자.</summary>
    private sealed class RoundButton : Button
    {
        private readonly FindBar bar;
        private bool hover, down;

        public RoundButton(FindBar bar, string text)
        {
            this.bar = bar;
            Text = text;
            Font = new Font("맑은 고딕", 9f);
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = bar.pal;
            g.Clear(p.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), bar.S(5));
            using var fill = new SolidBrush(down ? p.Pressed : hover ? p.Hover : p.Field);
            using var border = new Pen(p.FieldBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 1), p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private enum GlyphKind { ChevronUp, ChevronDown, Down, Up, Options, Close, Search, Clear }

    /// <summary>
    /// 찾기 바의 아이콘 버튼. 글꼴(Segoe Fluent Icons는 윈도우 11에만 있다)에 기대지 않고 선으로 직접 그려서
    /// 어느 윈도우에서나 같게 보이고, 화면 배율이 달라도 선명하다.
    /// </summary>
    private sealed class GlyphButton : Button
    {
        private readonly FindBar? bar;
        private GlyphKind kind;
        private bool hover;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public GlyphKind Kind { get => kind; set { kind = value; Invalidate(); } }

        /// <summary>bar가 null이면 부모 FindBar를 찾아서 쓴다(바에 붙은 뒤에 그려지므로).</summary>
        public GlyphButton(FindBar? bar = null)
        {
            this.bar = bar;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        private FindBar? Owner()
        {
            if (bar != null) return bar;
            for (Control? c = Parent; c != null; c = c.Parent) if (c is FindBar fb) return fb;
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Owner()?.pal ?? LightPalette;

            // 입력칸 안의 그림(돋보기·×)은 칸 색 위에, 나머지는 바 색 위에 놓인다.
            bool inField = kind is GlyphKind.Search or GlyphKind.Clear;
            Color back = inField ? p.Field : p.Bar;
            bool decor = kind == GlyphKind.Search; // 그림일 뿐인 아이콘
            g.Clear(hover && !decor ? p.Hover : back);

            // 아이콘은 배율 1 기준 좌표로 그리고, 가운데로 옮긴 뒤 배율만큼 키운다(선 굵기도 같이 커진다).
            float scale = (Owner()?.Scale) ?? DeviceDpi / 96f;
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.ScaleTransform(scale, scale);
            float cx = 0, cy = 0;
            var color = decor ? p.Hint : kind == GlyphKind.Clear ? p.Hint : p.Glyph;
            using var pen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

            switch (kind)
            {
                case GlyphKind.ChevronUp:
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f, cy + 2.2f), new PointF(cx, cy - 2.2f), new PointF(cx + 4.5f, cy + 2.2f) });
                    break;
                case GlyphKind.ChevronDown:
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f, cy - 2.2f), new PointF(cx, cy + 2.2f), new PointF(cx + 4.5f, cy - 2.2f) });
                    break;
                case GlyphKind.Down:
                    g.DrawLine(pen, cx, cy - 6, cx, cy + 6);
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f, cy + 1.5f), new PointF(cx, cy + 6), new PointF(cx + 4.5f, cy + 1.5f) });
                    break;
                case GlyphKind.Up:
                    g.DrawLine(pen, cx, cy - 6, cx, cy + 6);
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f, cy - 1.5f), new PointF(cx, cy - 6), new PointF(cx + 4.5f, cy - 1.5f) });
                    break;
                case GlyphKind.Close:
                    g.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                    g.DrawLine(pen, cx - 4.5f, cy + 4.5f, cx + 4.5f, cy - 4.5f);
                    break;
                case GlyphKind.Clear:
                    g.DrawLine(pen, cx - 3, cy - 3, cx + 3, cy + 3);
                    g.DrawLine(pen, cx - 3, cy + 3, cx + 3, cy - 3);
                    break;
                case GlyphKind.Options: // 슬라이더 세 줄(손잡이 위치는 줄마다 다르게)
                    float[] knobs = { -3, 3, -1 };
                    using (var knobFill = new SolidBrush(hover ? p.Hover : p.Bar))
                        for (int i = 0; i < 3; i++)
                        {
                            float y = cy + (i - 1) * 5f;
                            g.DrawLine(pen, cx - 7, y, cx + 7, y);
                            g.FillEllipse(knobFill, cx + knobs[i] - 2.3f, y - 2.3f, 4.6f, 4.6f);
                            g.DrawEllipse(pen, cx + knobs[i] - 2.3f, y - 2.3f, 4.6f, 4.6f);
                        }
                    break;
                case GlyphKind.Search:
                    g.DrawEllipse(pen, cx - 4.5f, cy - 5, 8, 8);
                    g.DrawLine(pen, cx + 1.8f, cy + 2.8f, cx + 5, cy + 6);
                    break;
            }
        }
    }

    // ───────────────────────── 동작 ─────────────────────────

    /// <summary>Ctrl+F로 열 때: 보이게 하고, 입력창에 포커스+전체 선택한다.</summary>
    public new void Show()
    {
        Visible = true;
        BringToFront();
        queryBox.Focus();
        queryBox.SelectAll();
    }

    /// <summary>Ctrl+H로 열 때: 찾기 바를 보이게 하고 바꾸기 줄까지 펼친다.</summary>
    public void ShowReplace()
    {
        Show();
        if (!expanded) SetExpanded(true);
    }

    public new void Hide()
    {
        Visible = false;
        focusBody();
    }

    public void ToggleVisible()
    {
        if (Visible) Hide();
        else Show();
    }

    /// <summary>˄/⌄ 버튼: 바꾸기 행을 펼치거나 접는다. 본문(target)은 전혀 건드리지 않는다.</summary>
    private void SetExpanded(bool value)
    {
        expanded = value;
        collapseBtn.Kind = expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown;
        replaceRow.Visible = expanded;
        Height = expanded ? S(ExpandedHeight) : S(CollapsedHeight);
        Invalidate(true);
    }

    private void FindNext(bool forward)
    {
        string q = queryBox.Text;
        if (string.IsNullOrEmpty(q) || q == FindPlaceholder) return;

        var comparison = matchCaseItem.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = getText();
        int idx;

        if (forward)
        {
            int startAt = Math.Min(getSelectionEnd(), text.Length);
            idx = text.IndexOf(q, startAt, comparison);
            if (idx < 0 && wrapAroundItem.Checked) idx = text.IndexOf(q, 0, comparison); // 처음부터 다시
        }
        else
        {
            int startAt = getSelectionStart() - 1;
            idx = startAt >= 0 ? text.LastIndexOf(q, Math.Min(startAt, text.Length - 1), comparison) : -1;
            if (idx < 0 && wrapAroundItem.Checked && text.Length > 0)
                idx = text.LastIndexOf(q, text.Length - 1, comparison); // 끝부터 다시
        }

        if (idx < 0)
        {
            FlashNotFound(queryBox);
            return;
        }

        selectRange(idx, q.Length);
        // selectRange가 본문으로 포커스를 가져가므로, 입력칸으로 되돌려야 Enter를 계속 눌러 다음 단어로 넘어갈 수 있다.
        // 찾은 부분의 선택 표시는 본문이 포커스를 잃어도 그대로 남는다(HideSelection=false).
        queryBox.Focus();
    }

    /// <summary>선택돼 있는 부분이 검색어와 일치하면 그것부터 바꾸고, 이어서 다음 걸 찾는다.</summary>
    private void ReplaceCurrent()
    {
        if (!EnsureEditable()) return;
        string q = queryBox.Text;
        if (string.IsNullOrEmpty(q) || q == FindPlaceholder) return;
        var comparison = matchCaseItem.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (target.SelectionLength > 0 && string.Equals(target.SelectedText, q, comparison))
            target.SelectedText = ReplacementText();

        FindNext(forward: true);
    }

    /// <summary>본문 전체에서 검색어를 전부 바꾼다.</summary>
    private void ReplaceAll()
    {
        if (!EnsureEditable()) return;
        string q = queryBox.Text;
        if (string.IsNullOrEmpty(q) || q == FindPlaceholder) return;
        var comparison = matchCaseItem.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string replacement = ReplacementText();

        string text = target.Text;
        var sb = new StringBuilder();
        int pos = 0, count = 0;
        while (true)
        {
            int idx = text.IndexOf(q, pos, comparison);
            if (idx < 0)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }
            sb.Append(text, pos, idx - pos);
            sb.Append(replacement);
            pos = idx + q.Length;
            count++;
        }

        if (count == 0)
        {
            FlashNotFound(queryBox);
            return;
        }

        int caret = target.SelectionStart;
        target.Text = sb.ToString();
        target.SelectionStart = Math.Min(caret, target.Text.Length);
        target.SelectionLength = 0;
    }

    private string ReplacementText() => replaceBox.Text == ReplacePlaceholder ? "" : replaceBox.Text;

    /// <summary>본문이 읽기 전용이면 바꾸기를 실행할 수 없으니 안내만 하고 멈춘다.</summary>
    private bool EnsureEditable()
    {
        if (!target.ReadOnly) return true;
        MessageBox.Show(FindForm(), "지금은 읽기 전용이에요. 편집 메뉴의 \"수정하기\"를 먼저 눌러주세요.",
            "바꾸기", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private static void FlashNotFound(TextBox box)
    {
        var original = box.BackColor;
        box.BackColor = Color.MistyRose;
        box.ForeColor = Color.Black;
        var reset = new System.Windows.Forms.Timer { Interval = 400 };
        reset.Tick += (_, _) => { box.BackColor = original; reset.Stop(); reset.Dispose(); };
        reset.Start();
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRectPath(new Rectangle(0, 0, Width, Height), S(CornerRadius));
        Region = new Region(path);
    }

    private void DrawBorder(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), S(CornerRadius));
        using var pen = new Pen(pal.BarBorder);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectPath(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
