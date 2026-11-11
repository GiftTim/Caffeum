using System.Drawing.Drawing2D;

namespace CoffeeViewer;

/// <summary>코드로 그리는 보라색 선 아이콘(에피소드 = 펼친 책, 에피소드 그룹 = 이어진 네 개의 점). 선 두께를 직접 정해서 얇게 그린다.</summary>
internal static class PurpleIcons
{
    public static readonly Color Purple = Color.FromArgb(0xA8, 0x7B, 0xFF);

    private static Bitmap Canvas(int size, Color color, out Graphics g, out Pen pen)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        pen = new Pen(color,Math.Max(1.2f, size / 13f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        return bmp;
    }

    /// <summary>펼친 책: 나란한 두 쪽(네모) + 가운데 책등 + 아래 작은 받침.</summary>
    public static Bitmap Book(int size, Color? color = null)
    {
        var bmp = Canvas(size, color ?? Purple, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size;
            g.DrawRectangle(pen, 0.12f * s, 0.22f * s, 0.38f * s, 0.54f * s);
            g.DrawRectangle(pen, 0.50f * s, 0.22f * s, 0.38f * s, 0.54f * s);
            g.DrawLine(pen, 0.50f * s, 0.76f * s, 0.50f * s, 0.82f * s);
        }
        return bmp;
    }

    /// <summary>이어진 네 점: 왼쪽 위 네모 · 오른쪽 위 동그라미 · 왼쪽 아래 동그라미 · 오른쪽 아래 네모, 가로 두 줄과 오른쪽 세로 한 줄로 연결.</summary>
    public static Bitmap Nodes(int size, Color? color = null)
    {
        var bmp = Canvas(size, color ?? Purple, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size, r = 0.09f * s, side = 0.18f * s;
            float tl = 0.18f * s, tr = 0.80f * s, top = 0.30f * s, bot = 0.72f * s;
            g.DrawLine(pen, tl + side / 2, top, tr - r, top);          // 위 가로선
            g.DrawLine(pen, tl + r, bot, tr - side / 2, bot);          // 아래 가로선
            g.DrawLine(pen, tr, top + r, tr, bot - side / 2);          // 오른쪽 세로선
            g.DrawRectangle(pen, tl - side / 2, top - side / 2, side, side);   // 왼쪽 위 네모
            g.DrawEllipse(pen, tr - r, top - r, r * 2, r * 2);                 // 오른쪽 위 동그라미
            g.DrawEllipse(pen, tl - r, bot - r, r * 2, r * 2);                 // 왼쪽 아래 동그라미
            g.DrawRectangle(pen, tr - side / 2, bot - side / 2, side, side);   // 오른쪽 아래 네모
        }
        return bmp;
    }

    /// <summary>캔버스: 왼쪽 위 · 오른쪽 아래 두 카드를 굽은 선으로 이은 모양(플롯 캔버스).</summary>
    public static Bitmap Board(int size, Color color)
    {
        var bmp = Canvas(size, color, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size;
            g.DrawRectangle(pen, 0.10f * s, 0.14f * s, 0.36f * s, 0.26f * s);   // 왼쪽 위 카드
            g.DrawRectangle(pen, 0.54f * s, 0.60f * s, 0.36f * s, 0.26f * s);   // 오른쪽 아래 카드
            g.DrawBezier(pen, 0.46f * s, 0.27f * s, 0.72f * s, 0.27f * s, 0.72f * s, 0.40f * s, 0.72f * s, 0.60f * s); // 잇는 선
        }
        return bmp;
    }

    /// <summary>문서: 접힌 모서리가 있는 종이 + 글 두 줄.</summary>
    public static Bitmap Document(int size, Color? color = null)
    {
        var bmp = Canvas(size, color ?? Purple, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size;
            var pts = new PointF[] { new(0.24f * s, 0.12f * s), new(0.60f * s, 0.12f * s), new(0.80f * s, 0.32f * s), new(0.80f * s, 0.88f * s), new(0.24f * s, 0.88f * s) };
            g.DrawPolygon(pen, pts);
            g.DrawLines(pen, new[] { new PointF(0.60f * s, 0.12f * s), new PointF(0.60f * s, 0.32f * s), new PointF(0.80f * s, 0.32f * s) });
            g.DrawLine(pen, 0.36f * s, 0.54f * s, 0.68f * s, 0.54f * s);
            g.DrawLine(pen, 0.36f * s, 0.72f * s, 0.62f * s, 0.72f * s);
        }
        return bmp;
    }

    /// <summary>폴더: 위에 작은 귀가 있는 둥근 폴더.</summary>
    public static Bitmap Folder(int size, Color? color = null)
    {
        var bmp = Canvas(size, color ?? Purple, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size;
            var pts = new PointF[] { new(0.12f * s, 0.26f * s), new(0.40f * s, 0.26f * s), new(0.48f * s, 0.36f * s), new(0.88f * s, 0.36f * s), new(0.88f * s, 0.80f * s), new(0.12f * s, 0.80f * s) };
            g.DrawPolygon(pen, pts);
        }
        return bmp;
    }

    /// <summary>복사: 겹친 두 개의 네모.</summary>
    public static Bitmap Copy(int size, Color? color = null)
    {
        var bmp = Canvas(size, color ?? Purple, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size;
            g.DrawRectangle(pen, 0.14f * s, 0.34f * s, 0.46f * s, 0.52f * s);
            g.DrawLines(pen, new[] { new PointF(0.34f * s, 0.34f * s), new PointF(0.34f * s, 0.16f * s), new PointF(0.86f * s, 0.16f * s), new PointF(0.86f * s, 0.68f * s), new PointF(0.60f * s, 0.68f * s) });
        }
        return bmp;
    }

    /// <summary>글자 수 세기: 둥근 종이 + 글 줄 셋 + 오른쪽 위 체크 + 아래 123.</summary>
    public static Bitmap CountDoc(int size, Color color)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float s = size;
        using var pen = new Pen(color, Math.Max(1.0f, s / 17f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var page = LibraryPalette.Rounded(new RectangleF(0.12f * s, 0.10f * s, 0.62f * s, 0.80f * s), 0.08f * s)) g.DrawPath(pen, page);
        g.DrawLine(pen, 0.24f * s, 0.30f * s, 0.46f * s, 0.30f * s);
        g.DrawLine(pen, 0.24f * s, 0.42f * s, 0.50f * s, 0.42f * s);
        g.DrawLine(pen, 0.24f * s, 0.54f * s, 0.40f * s, 0.54f * s);
        g.DrawLines(pen, new[] { new PointF(0.52f * s, 0.38f * s), new PointF(0.64f * s, 0.54f * s), new PointF(0.90f * s, 0.16f * s) });
        // 123은 보통 굵기 글자에 얇은 테두리를 더해서 보통과 굵게 사이로(조금만 두껍게)
        using var b = new SolidBrush(color);
        using var numPen = new Pen(color, Math.Max(0.6f, s / 55f)) { LineJoin = LineJoin.Round };
        using var numPath = new GraphicsPath();
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        numPath.AddString("123", new FontFamily("Segoe UI"), (int)FontStyle.Regular, 0.25f * s, new RectangleF(0.12f * s, 0.62f * s, 0.62f * s, 0.26f * s), sf);
        g.FillPath(b, numPath);
        g.DrawPath(numPen, numPath);
        return bmp;
    }

    /// <summary>줄 목록: 오른쪽 끝이 맞는 네 줄(첫 줄과 끝 줄 앞에 작은 점).</summary>
    public static Bitmap Lines(int size, Color color)
    {
        var bmp = Canvas(size, color, out var g, out var pen);
        using (g) using (pen)
        {
            float s = size, end = 0.90f * s;
            float[] ys = { 0.27f * s, 0.42f * s, 0.58f * s, 0.73f * s };
            float[] starts = { 0.31f * s, 0.49f * s, 0.68f * s, 0.31f * s };
            for (int i = 0; i < 4; i++) g.DrawLine(pen, starts[i], ys[i], end, ys[i]);
            foreach (int i in new[] { 0, 3 }) g.DrawLine(pen, 0.12f * s, ys[i], 0.17f * s, ys[i]); // 작은 점
        }
        return bmp;
    }
}
