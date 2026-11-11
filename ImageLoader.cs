using System.Drawing.Imaging;
using SkiaSharp;

namespace CoffeeViewer;

/// <summary>
/// 순수 CPU 디코딩만 한다(zip이나 파일 스트림 같은 공유 자원은 안 건드림) — 그래서 다른 페이지의
/// IImageSet.LoadRawBytes(zip 공유 스트림을 쓰는 부분, 락 필요)와 달리 여러 스레드에서 동시에 불러도 안전하다.
/// GDI+(System.Drawing.Image)는 webp 디코더가 내장돼 있지 않아서 Image.FromStream이 webp를 못 읽으므로,
/// webp와 움직이는 gif는 SkiaSharp로 디코딩하고 나머지(jpg/png/bmp/정지 gif)는 지금까지처럼 GDI+가 바로 처리한다.
/// </summary>
public static class ImageLoader
{
    public static Image Decode(byte[] bytes, string fileName)
    {
        // 확장자만 믿으면 안 된다 — 실제로 zip 안의 "001.png"가 전부 webp인 경우가 있었고,
        // 그걸 GDI+에 넘기면 "Parameter is not valid"로 터졌다. 그래서 파일 헤더로 판별한다.
        if (IsWebp(bytes) || Path.GetExtension(fileName).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return DecodeWithSkia(bytes);
        if (IsGif(bytes) && AnimationPlayer.IsAnimated(bytes))
            return DecodeWithSkia(bytes);

        using var ms = new MemoryStream(bytes);
        return Image.FromStream(ms);
    }

    // 움직이는 그림(webp·gif)은 첫 프레임만 Image로 돌려주고, 나머지 프레임을 재생할 수 있게 원본 바이트를 그 Image에
    // 붙여둔다(ImageCanvas가 보여줄 때 꺼내서 AnimationPlayer로 재생). 프레임을 미리 다 풀어두면 한 장에
    // 수십~백몇십 MB라(실측: 640x640 x 66프레임) 프리페치 캐시에 몇 장만 쌓여도 감당이 안 된다.
    // 약한 참조 테이블이라 Image가 캐시에서 버려지면 여기 붙은 바이트도 같이 사라진다 — 따로 정리할 게 없다.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, byte[]> animatedSources = new();

    public static byte[]? GetAnimationSource(Image img) =>
        animatedSources.TryGetValue(img, out var bytes) ? bytes : null;

    // webp 헤더: "RIFF" + 4바이트 크기 + "WEBP"
    private static bool IsWebp(byte[] b) =>
        b.Length >= 12
        && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
        && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    // gif 헤더: "GIF87a" 또는 "GIF89a"
    private static bool IsGif(byte[] b) =>
        b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8';

    /// <summary>
    /// SkiaSharp로 디코딩한 뒤, 픽셀을 직접 복사해 GDI+ Bitmap으로 만든다.
    /// 처음엔 PNG로 재인코딩해서 GDI+에 넘겼는데, PNG 인코딩 자체가 페이지 한 장에 최대 1.5초 넘게
    /// 걸려서(실측) 병목이었다. 픽셀을 그대로 복사하면 같은 페이지가 0.1초 안팎으로 끝난다(13~14배).
    /// </summary>
    private static Image DecodeWithSkia(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("이미지를 디코딩하지 못했습니다.");
        var result = ToGdiBitmap(decoded);

        if (AnimationPlayer.IsAnimated(bytes))
            animatedSources.AddOrUpdate(result, bytes);
        return result;
    }

    public static Bitmap ToGdiBitmap(SKBitmap decoded)
    {
        var result = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, decoded.Width, decoded.Height);
        var destLock = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var destSurface = SKSurface.Create(info, destLock.Scan0, destLock.Stride);
            destSurface.Canvas.DrawBitmap(decoded, 0, 0);
            destSurface.Canvas.Flush();
        }
        finally
        {
            result.UnlockBits(destLock);
        }
        return result;
    }
}
