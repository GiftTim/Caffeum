using System.Drawing.Imaging;

namespace CoffeeViewer;

/// <summary>항상 위가 켜졌을 때 쓰는 기울어진 압정 그림(Img\Pin on Top.png)을 원하는 색 · 크기로 만든다.</summary>
internal static class PinIcon
{
    private static Bitmap? sourceOn, sourceOff;

    /// <summary>그림 둘레의 빈 흰 칸을 잘라내서, 압정이 칸 가득 그려지게 한다(그러지 않으면 아이콘이 작아 보인다).</summary>
    private static Bitmap CropToContent(Bitmap src)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var p = src.GetPixel(x, y);
                int lum = (int)(p.R * 0.299 + p.G * 0.587 + p.B * 0.114);
                if (p.A > 40 && lum < 225) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            }
        if (maxX < minX) return src;
        int w = maxX - minX + 1, hgt = maxY - minY + 1, side = Math.Max(w, hgt);
        var sq = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sq))
        {
            g.Clear(Color.White);
            g.DrawImage(src, new Rectangle((side - w) / 2, (side - hgt) / 2, w, hgt), new Rectangle(minX, minY, w, hgt), GraphicsUnit.Pixel);
        }
        src.Dispose();
        return sq;
    }

    /// <summary>그림의 밝기를 투명도로 바꿔(검은 선만 남기고) 주어진 색으로 칠한 size × size 그림.</summary>
    public static Bitmap Make(Color ink, int size, bool pinned = true)
    {
        try
        {
            if (sourceOn == null)
            {
                using var s = typeof(PinIcon).Assembly.GetManifestResourceStream("PinOnTop.png");
                if (s != null) sourceOn = CropToContent(new Bitmap(s));
            }
            if (sourceOff == null)
            {
                using var s = typeof(PinIcon).Assembly.GetManifestResourceStream("UnpinFromTop.png");
                if (s != null) sourceOff = CropToContent(new Bitmap(s));
            }
        }
        catch { }
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var source = pinned ? sourceOn : sourceOff;
        if (source == null) return bmp;
        // 원본을 size에 맞게 줄인 뒤, 어두운 정도를 투명도로 바꿔 ink 색으로 칠한다
        using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = scaled.GetPixel(x, y);
                int lum = (int)(p.R * 0.299 + p.G * 0.587 + p.B * 0.114);
                int a = Math.Clamp(255 - lum, 0, 255);
                bmp.SetPixel(x, y, Color.FromArgb(a, ink));
            }
        return bmp;
    }
}
