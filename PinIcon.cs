using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CoffeeViewer;

/// <summary>
/// 항상 위(압정) 아이콘을 코드로 얇은 선으로 그려서 원하는 색 · 크기로 만든다.
/// 꺼짐 = 가로로 누운 압정, 켜짐 = 45도 기울어진 압정(둘 다 같은 굵기 · 같은 크기).
/// </summary>
internal static class PinIcon
{
    public static Bitmap Make(Color ink, int size, bool pinned = true)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(size / 2f, size / 2f);
        g.RotateTransform(pinned ? 45 : 90); // 켜짐: 비스듬히 · 꺼짐: 가로로 눕힌다(바늘이 왼쪽 아래 / 왼쪽)
        float s = size * (pinned ? 0.90f : 0.86f);
        float w = Math.Max(1.1f, size * 0.048f);
        using var pen = new Pen(ink, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        // 똑바로 선 압정(위 → 아래): 머리(속이 빈 네모) · 받침으로 넓어지는 옆선 · 받침 · 바늘
        g.DrawRectangle(pen, -0.13f * s, -0.46f * s, 0.26f * s, 0.34f * s);                 // 머리
        g.DrawLine(pen, -0.13f * s, -0.12f * s, -0.27f * s, 0.05f * s);                      // 왼쪽 옆선
        g.DrawLine(pen, 0.13f * s, -0.12f * s, 0.27f * s, 0.05f * s);                        // 오른쪽 옆선
        g.DrawLine(pen, -0.27f * s, 0.05f * s, 0.27f * s, 0.05f * s);                        // 받침
        g.DrawLine(pen, 0, 0.05f * s, 0, 0.46f * s);                                         // 바늘
        return bmp;
    }
}