using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.WinForms;

namespace CoffeeViewer;

/// <summary>독서용 색 조합. Back/Fore는 본문, Chrome/ChromeFore는 메뉴·상태줄·서재 같은 주변 화면에 쓴다.</summary>
public sealed class ReaderTheme
{
    public required string Name { get; init; }
    public required Color Back { get; init; }
    public required Color Fore { get; init; }
    public required Color Chrome { get; init; }
    public required Color ChromeFore { get; init; }
    public required Color Link { get; init; }
    public bool Dark { get; init; }
}

public static class Themes
{
    public static readonly ReaderTheme[] All =
    {
        // Caffeum 기본: Muvel처럼 여백 많고 깨끗한 바탕에, 커피 · 캐러멜 강조색.
        new() { Name = "카페 라이트", Back = Color.FromArgb(0xFD, 0xFC, 0xFA), Fore = Color.FromArgb(0x2B, 0x26, 0x22), Chrome = Color.FromArgb(0xF4, 0xF1, 0xEC), ChromeFore = Color.FromArgb(0x3A, 0x33, 0x2D), Link = Color.FromArgb(0x9C, 0x63, 0x3A) },
        new() { Name = "카페 다크", Back = Color.FromArgb(0x1C, 0x1A, 0x18), Fore = Color.FromArgb(0xE4, 0xDF, 0xD9), Chrome = Color.FromArgb(0x26, 0x23, 0x20), ChromeFore = Color.FromArgb(0xE4, 0xDF, 0xD9), Link = Color.FromArgb(0xD8, 0xA0, 0x6E), Dark = true },
        new() { Name = "라이트", Back = Color.White, Fore = Color.Black, Chrome = SystemColors.Control, ChromeFore = SystemColors.ControlText, Link = Color.FromArgb(0x06, 0x45, 0xad) },
        new() { Name = "세피아", Back = Color.FromArgb(0xF4, 0xEC, 0xD8), Fore = Color.FromArgb(0x5B, 0x46, 0x36), Chrome = Color.FromArgb(0xE6, 0xDB, 0xBE), ChromeFore = Color.FromArgb(0x4A, 0x39, 0x2B), Link = Color.FromArgb(0x8A, 0x4B, 0x1D) },
        new() { Name = "눈 편한 초록", Back = Color.FromArgb(0xCB, 0xE8, 0xCF), Fore = Color.FromArgb(0x1F, 0x3A, 0x24), Chrome = Color.FromArgb(0xB4, 0xD5, 0xB8), ChromeFore = Color.FromArgb(0x1A, 0x30, 0x1E), Link = Color.FromArgb(0x0B, 0x5D, 0x2A) },
        new() { Name = "다크", Back = Color.FromArgb(0x1E, 0x1E, 0x1E), Fore = Color.FromArgb(0xD4, 0xD4, 0xD4), Chrome = Color.FromArgb(0x2B, 0x2B, 0x2B), ChromeFore = Color.FromArgb(0xE0, 0xE0, 0xE0), Link = Color.FromArgb(0x6C, 0xB6, 0xFF), Dark = true },
        new() { Name = "블랙", Back = Color.Black, Fore = Color.FromArgb(0xB0, 0xB0, 0xB0), Chrome = Color.FromArgb(0x12, 0x12, 0x12), ChromeFore = Color.FromArgb(0xC8, 0xC8, 0xC8), Link = Color.FromArgb(0x6C, 0xB6, 0xFF), Dark = true },
    };

    public static ReaderTheme Find(string? name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];

    public static string ToCss(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>라이트 테마는 원래 윈도우 모양 그대로 두고, 나머지 테마만 색을 입힌 메뉴 렌더러를 쓴다.</summary>
    public static ToolStripRenderer CreateRenderer(ReaderTheme t) =>
        t.Name == "라이트" ? new ToolStripProfessionalRenderer() : new ThemeRenderer(t);

    // ───────────────────────── 창/컨트롤에 테마 입히기 ─────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>주변 화면(서재·설정·도움말 창)용. 본문 컨트롤(mainEditor, WebView2)은 MainForm이 따로 다룬다.</summary>
    public static void ApplyChrome(Control root, ReaderTheme t)
    {
        if (root is Form form) SetDarkTitleBar(form, t.Dark);
        Recolor(root, t);
    }

    private static void Recolor(Control c, ReaderTheme t)
    {
        // 자체적으로 그리는 컨트롤이나 웹뷰는 건드리지 않는다.
        if (c is WebView2 or ImageCanvas or FindBar or LoadingOverlay or ArrowOverlayButton or PageScrubberBar or ToastLabel) return;

        c.BackColor = t.Chrome;
        c.ForeColor = t.ChromeFore;

        switch (c)
        {
            case TextBox or ListBox or ListView or TreeView or ComboBox or NumericUpDown:
                // 입력칸·목록은 주변 바탕보다 살짝 다르게(라이트는 흰색, 다크는 더 어둡게, 나머지는 더 밝게).
                c.BackColor = t.Name == "라이트" ? Color.White
                    : t.Dark ? Blend(t.Chrome, Color.Black, 0.25f)
                    : Blend(t.Chrome, Color.White, 0.5f);
                if (c.IsHandleCreated) SetScrollbarTheme(c, t.Dark); else c.HandleCreated += (_, _) => SetScrollbarTheme(c, t.Dark);
                // 콤보박스는 기본(3D) 모양이면 배경색을 무시해서 다크에서 혼자 흰색으로 남는다.
                if (c is ComboBox combo) combo.FlatStyle = t.Name == "라이트" ? FlatStyle.Standard : FlatStyle.Flat;
                break;
            case Panel { AutoScroll: true }:
                // 스크롤되는 영역(설정창)도 스크롤바가 다크에서 혼자 밝게 남지 않게.
                if (c.IsHandleCreated) SetScrollbarTheme(c, t.Dark); else c.HandleCreated += (_, _) => SetScrollbarTheme(c, t.Dark);
                break;
            case Button b:
                b.FlatStyle = t.Name == "라이트" ? FlatStyle.Standard : FlatStyle.Flat;
                b.UseVisualStyleBackColor = t.Name == "라이트";
                if (t.Name != "라이트")
                {
                    b.FlatAppearance.BorderColor = Blend(t.Chrome, t.ChromeFore, 0.35f);
                    b.BackColor = Blend(t.Chrome, t.ChromeFore, 0.1f);
                }
                break;
        }

        foreach (Control child in c.Controls) Recolor(child, t);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>목록·트리의 스크롤바(와 열 머리글)를 어두운 윈도우 테마로 바꾼다. 켜둔 채 라이트로 돌아올 때를 위해 되돌리기도 한다.</summary>
    private static void SetScrollbarTheme(Control c, bool dark)
    {
        if (c is not (ListView or TreeView or ListBox or Panel)) return;
        try
        {
            SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            if (c is ListView)
            {
                IntPtr header = SendMessage(c.Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
        }
        catch { }
    }

    public static void SetDarkTitleBar(Form f, bool dark)
    {
        void Apply()
        {
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(f.Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref v, sizeof(int)); } catch { }
        }
        if (f.IsHandleCreated) Apply();
        else f.HandleCreated += (_, _) => Apply();
    }
}

/// <summary>다크·세피아 같은 테마에서 메뉴 막대와 드롭다운이 흰 바탕으로 튀지 않게 색을 바꿔주는 표.</summary>
internal sealed class ThemeColorTable : ProfessionalColorTable
{
    private readonly ReaderTheme t;
    private readonly Color hover, border;

    public ThemeColorTable(ReaderTheme t)
    {
        this.t = t;
        UseSystemColors = false;
        hover = Themes.Blend(t.Chrome, t.ChromeFore, 0.16f);
        border = Themes.Blend(t.Chrome, t.ChromeFore, 0.3f);
    }

    public override Color MenuStripGradientBegin => t.Chrome;
    public override Color MenuStripGradientEnd => t.Chrome;
    public override Color ToolStripDropDownBackground => t.Chrome;
    public override Color ImageMarginGradientBegin => t.Chrome;
    public override Color ImageMarginGradientMiddle => t.Chrome;
    public override Color ImageMarginGradientEnd => t.Chrome;
    public override Color MenuBorder => border;
    public override Color MenuItemBorder => hover;
    public override Color MenuItemSelected => hover;
    public override Color MenuItemSelectedGradientBegin => hover;
    public override Color MenuItemSelectedGradientEnd => hover;
    public override Color MenuItemPressedGradientBegin => hover;
    public override Color MenuItemPressedGradientMiddle => hover;
    public override Color MenuItemPressedGradientEnd => hover;
    public override Color SeparatorDark => border;
    public override Color SeparatorLight => border;
    public override Color CheckBackground => hover;
    public override Color CheckSelectedBackground => hover;
    public override Color CheckPressedBackground => hover;
}

internal sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    private readonly ReaderTheme t;

    public ThemeRenderer(ReaderTheme t) : base(new ThemeColorTable(t))
    {
        this.t = t;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.ChromeFore : Themes.Blend(t.Chrome, t.ChromeFore, 0.4f);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.ChromeFore;
        base.OnRenderArrow(e);
    }

    // 기본 체크 표시는 검은색이라 어두운 바탕에서 안 보인다 — 글자색으로 직접 그린다.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using (var bg = new SolidBrush(Themes.Blend(t.Chrome, t.ChromeFore, 0.16f)))
            e.Graphics.FillRectangle(bg, r);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(t.ChromeFore, 1.8f);
        float x = r.Left + r.Width * 0.25f, y = r.Top + r.Height * 0.5f;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(x, y),
            new PointF(r.Left + r.Width * 0.44f, r.Top + r.Height * 0.68f),
            new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.32f),
        });
    }
}
