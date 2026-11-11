using System.Drawing.Imaging;
using SkiaSharp;

namespace CoffeeViewer;

/// <summary>보기 > 보기 모드에서 고르는 네 가지 맞춤 방식.</summary>
public enum ImageViewMode
{
    ActualSize,      // 원본 크기로 보기 — 항상 100%
    FitWindow,       // 꽉차게 보기 — 창에 맞추되, 원본보다 확대하진 않는다
    FitWidth,        // 꽉차게 보기(폭 맞춤) — 가로만 창 폭에 맞춘다(세로는 넘칠 수 있음, 끌어서 보기)
    FitWindowUpscale, // 작은 그림도 꽉차게 보기 — 창에 맞추고, 원본보다 작으면 확대도 한다
    Custom           // 사용자 지정 — 원본 대비 직접 넣은 %로 고정(ImageCanvas.CustomPercent)
}

/// <summary>
/// 만화/이미지 리더의 본문 화면. 마우스 휠로 확대·축소, 눌러서 끌면 이동, 더블클릭하면 지금 보기 모드 기준으로 리셋.
/// 좌우 화살표 키는 다음/이전 장(이미지)으로 — PageRequested 이벤트로 MainForm에 알려준다.
/// </summary>
public class ImageCanvas : Panel
{
    /// <summary>-1 = 이전 장, +1 = 다음 장.</summary>
    public event Action<int>? PageRequested;

    /// <summary>보기 > 화면 터치로 페이지 넘기기 체크에 연동 — 꺼져 있으면 화면 반쪽 탭은 무시하고
    /// (휠 확대·드래그 이동·더블클릭 리셋·화살표 키는 이거랑 상관없이 항상 그대로 동작한다).
    /// 디자이너용 속성이 아니라 MainForm이 코드로만 설정하는 값이라 직렬화 대상에서 뺀다.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TapToTurnEnabled { get; set; } = true;

    private string? pageInfoText;

    /// <summary>반디뷰처럼 그림 위 모서리에 작게 띄우는 "[현재/전체]" 표시. null이면 안 그린다.
    /// 디자이너용 속성이 아니라 MainForm이 코드로만 설정하는 값이라 직렬화 대상에서 뺀다.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? PageInfoText
    {
        get => pageInfoText;
        set
        {
            if (pageInfoText == value) return;
            pageInfoText = value;
            Invalidate();
        }
    }

    private Image? image;
    private float zoom = 1f;
    private float fitZoom = 1f;
    private PointF pan = PointF.Empty;
    private Point dragStart;
    private PointF panStart;
    private bool dragging;

    private ImageViewMode viewMode = ImageViewMode.FitWindowUpscale; // 지금까지의 기본 동작과 동일
    /// <summary>보기 모드가 사용자 지정일 때의 배율(%, 100 = 원본 크기).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CustomPercent { get; set; } = 100;
    private int marginL, marginR, marginT, marginB; // 만화 여백(px) — 맞춤 기준 영역은 창에서 이 여백을 뺀 안쪽
    private bool zoomFollowsFit = true; // true면 창 크기가 바뀔 때마다 보기 모드 기준으로 다시 맞춘다(마우스 휠로 직접 확대하면 꺼진다)

    // 반쪽 탭으로 페이지 넘기기: 클릭하자마자 바로 넘기면, 더블클릭(보기 리셋)의 첫 번째 클릭 때문에
    // 페이지가 한 번 더 넘어가 버린다 — 그래서 시스템 더블클릭 시간만큼 기다렸다가, 그사이 두 번째
    // 클릭이 없었을 때만(=진짜 한 번 탭한 것으로 판명 났을 때) 넘긴다.
    private readonly System.Windows.Forms.Timer tapTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private int pendingTapDelta;

    // zoom이 바뀔 때만 SkiaSharp로 한 번 스케일해두는 캐시. GDI+로 매 OnPaint마다(패닝·리사이즈 포함)
    // 다시 스케일하면 큰 이미지에서 수십~백여 ms씩 걸려서(실측: Bilinear 48ms, HighQualityBicubic 145ms),
    // 캐시 없이는 페이지를 넘길 때마다, 심지어 마우스로 끌 때마다 매번 그 비용을 다시 치른다.
    // SkiaSharp로 한 번만 스케일해두면(같은 크기 기준 15~20ms) 그 다음부터는 그냥 그대로 찍기만 하면 된다.
    private Bitmap? renderCache;

    // 움직이는 그림(webp·gif) 재생기. 페이지가 바뀌면 멈추고 새로 만든다.
    private AnimationPlayer? animation;
    private byte[]? animationSource; // 설정이 바뀌었을 때 지금 그림을 처음부터 다시 재생하려고 들고 있는다
    private volatile bool shownOnScreen = true; // 재생기가 백그라운드 스레드에서 읽는 값이라 Visible 대신 따로 둔다

    public ImageCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        ResizeRedraw = true;
        TabStop = true;

        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        tapTimer.Tick += (_, _) => { tapTimer.Stop(); PageRequested?.Invoke(pendingTapDelta); };
        DoubleClick += (_, _) => { tapTimer.Stop(); ResetZoom(); }; // 더블클릭으로 확정되면 대기 중이던 탭 넘김은 취소
        Resize += (_, _) =>
        {
            RecalcFit();
            if (zoomFollowsFit) { zoom = fitZoom; RebuildRenderCache(); } // 보기 모드를 따라가는 중이면 창 크기 변화에 맞춰 다시 맞춘다
            ClampPan(); // 창이 작아지면 이전에 끌어둔 위치가 범위 밖으로 나갈 수 있으니 다시 눌러 담는다
            Invalidate();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Left) { PageRequested?.Invoke(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { PageRequested?.Invoke(1); e.Handled = true; }
        };
        VisibleChanged += (_, _) => shownOnScreen = Visible;
    }

    public void SetImage(Image? img)
    {
        animation?.Dispose();
        animation = null;
        animationSource = null;
        image?.Dispose();
        image = img;
        pan = PointF.Empty;
        zoomFollowsFit = true; // 새 페이지는 항상 지금 보기 모드 기준으로 다시 시작한다
        RecalcFit();
        zoom = fitZoom;
        RebuildRenderCache();
        Invalidate();

        if (img != null && ImageLoader.GetAnimationSource(img) is { } bytes)
        {
            animationSource = bytes;
            StartAnimation();
        }
    }

    /// <summary>설정 > 무한 반복을 바꿨을 때 — 한 번 재생하고 멈춰 있던 그림도 바뀐 설정대로 처음부터 다시 돌린다.</summary>
    public void RestartAnimation()
    {
        if (animationSource == null) return;
        animation?.Dispose();
        StartAnimation();
    }

    private void StartAnimation() =>
        animation = new AnimationPlayer(animationSource!, this, () => shownOnScreen,
            () => AppShared.Settings.LoopAnimations, ShowAnimationFrame);

    /// <summary>애니메이션의 다음 프레임으로 그림만 바꾼다 — 확대 배율·끌어둔 위치는 그대로 둔다
    /// (프레임 크기는 항상 같으니 맞춤 배율도 다시 계산할 필요가 없다).</summary>
    private void ShowAnimationFrame(Bitmap frame)
    {
        image?.Dispose();
        image = frame;
        RebuildRenderCache();
        Invalidate();
    }

    /// <summary>사용자가 확대 · 축소 · 맞춤을 바꿨을 때(휠 · 더블클릭 · 보기 모드) 원본 대비 배율(%, 100 = 원본 크기).</summary>
    public event Action<int>? ZoomPercentChanged;

    private void NotifyZoom() => ZoomPercentChanged?.Invoke((int)Math.Round(zoom * 100f));

    public void ResetZoom()
    {
        zoomFollowsFit = true;
        zoom = fitZoom;
        pan = PointF.Empty;
        RebuildRenderCache();
        Invalidate();
        NotifyZoom();
    }

    /// <summary>확대 표시 상자의 − / + 버튼: 휠 한 칸과 같은 단계로.</summary>
    public void ZoomStep(int direction)
    {
        if (image == null) return;
        zoomFollowsFit = false;
        zoom = Math.Clamp(zoom * (direction > 0 ? 1.15f : 1f / 1.15f), fitZoom * 0.3f, fitZoom * 8f);
        ClampPan();
        RebuildRenderCache();
        Invalidate();
        NotifyZoom();
    }

    /// <summary>보기 > 보기 모드에서 고른 방식을 지금 화면에 바로 적용한다.</summary>
    public void SetViewMode(ImageViewMode mode)
    {
        viewMode = mode;
        zoomFollowsFit = true;
        pan = PointF.Empty;
        RecalcFit();
        zoom = fitZoom;
        RebuildRenderCache();
        Invalidate();
        NotifyZoom();
    }

    /// <summary>설정 > 보기 > 만화 여백을 지금 화면에 바로 적용한다(상하 · 좌우 각각, px).</summary>
    public void SetMargins(int side, int vertical) => SetMargins(side, side, vertical, vertical);

    /// <summary>좌 · 우 · 상 · 하 여백을 따로 정한다(px).</summary>
    public void SetMargins(int left, int right, int top, int bottom)
    {
        marginL = Math.Max(0, left); marginR = Math.Max(0, right);
        marginT = Math.Max(0, top); marginB = Math.Max(0, bottom);
        RecalcFit();
        if (zoomFollowsFit) { zoom = fitZoom; pan = PointF.Empty; }
        ClampPan();
        RebuildRenderCache();
        Invalidate();
        NotifyZoom();
    }

    // 여백을 뺀 안쪽 영역 크기 — 창이 여백보다 작아져도 최소 1px은 남긴다.
    private float InnerWidth => Math.Max(1, Width - marginL - marginR);
    private float InnerHeight => Math.Max(1, Height - marginT - marginB);
    private float OffX => (marginL - marginR) / 2f; // 여백 안쪽 영역의 가운데가 창 가운데에서 벗어난 만큼
    private float OffY => (marginT - marginB) / 2f;

    private void RecalcFit()
    {
        if (image == null || Width <= 0 || Height <= 0) { fitZoom = 1f; return; }
        float sx = InnerWidth / image.Width;
        float sy = InnerHeight / image.Height;
        fitZoom = viewMode switch
        {
            ImageViewMode.ActualSize => 1f,
            ImageViewMode.FitWindow => Math.Min(1f, Math.Min(sx, sy)), // 창보다 작은 이미지는 확대하지 않는다
            ImageViewMode.FitWidth => sx,
            ImageViewMode.FitWindowUpscale => Math.Min(sx, sy),
            ImageViewMode.Custom => CustomPercent / 100f,
            _ => Math.Min(sx, sy),
        };
        if (fitZoom <= 0) fitZoom = 1f;
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (image == null) return;
        zoomFollowsFit = false; // 사용자가 직접 확대·축소하면 창 크기 변화에 더는 자동으로 안 맞춘다
        float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        zoom = Math.Clamp(zoom * factor, fitZoom * 0.3f, fitZoom * 8f);
        ClampPan(); // 축소해서 그림이 창보다 작아지면, 확대돼 있을 때 끌어둔 위치가 그대로 남아있으면 안 되니까
        RebuildRenderCache();
        Invalidate();
        NotifyZoom();
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || image == null) return;
        dragging = true;
        dragStart = e.Location;
        panStart = pan;
        Focus();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!dragging) return;
        pan = new PointF(panStart.X + (e.X - dragStart.X), panStart.Y + (e.Y - dragStart.Y));
        ClampPan();
        Invalidate(); // 패닝은 zoom이 안 바뀌므로 renderCache는 그대로 두고 위치만 다시 그린다(재스케일 없음)
    }

    /// <summary>끌지 않고 화면 왼쪽/오른쪽 칸을 콕 찍었을 때 앞/뒤 장으로 넘긴다 — 옵시디언 플러그인/터치 기준
    /// (왼쪽 = 뒤로, 오른쪽 = 앞으로, 가운데 칸은 무시 — TapZone). 확대해서 실제로 끈(패닝한) 경우엔 그게 그림을 옮기려던 거니까
    /// 페이지를 넘기지 않는다 — dragStart에서 이만큼(TapMoveThresholdSq) 안 벗어났을 때만 "탭"으로 친다.</summary>
    private const int TapMoveThreshold = 6;

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        bool wasDragging = dragging;
        dragging = false;
        if (e.Button != MouseButtons.Left || image == null) return;
        if (!TapToTurnEnabled) return;
        if (e.Clicks > 1) { tapTimer.Stop(); return; } // 더블클릭의 두 번째 클릭 — DoubleClick 핸들러가 처리한다
        int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
        if (!wasDragging || dx * dx + dy * dy > TapMoveThreshold * TapMoveThreshold) return;
        // 바로 넘기지 않고 대기 — 이 클릭이 더블클릭의 첫 번째였다면 곧 DoubleClick이 취소해준다.
        pendingTapDelta = TapZone.Direction(e.X, Width);
        if (pendingTapDelta == 0) return; // 가운데 칸은 넘기지 않는다
        tapTimer.Stop();
        tapTimer.Start();
    }

    /// <summary>그림이 창보다 작은 방향(가로/세로 각각)으로는 끌어도 안 움직이게, 큰 방향으로는 그림
    /// 가장자리가 창 안쪽으로 더 들어오지 않을 만큼만(반대쪽에 검은 여백이 생기지 않을 만큼만) 움직이게
    /// pan을 범위 안으로 눌러 담는다.</summary>
    private void ClampPan()
    {
        if (image == null) return;
        float w = image.Width * zoom;
        float h = image.Height * zoom;
        float maxX = Math.Max(0, (w - InnerWidth) / 2f); // 여백 안쪽 가장자리까지만 끌 수 있다
        float maxY = Math.Max(0, (h - InnerHeight) / 2f);
        pan = new PointF(Math.Clamp(pan.X, -maxX, maxX), Math.Clamp(pan.Y, -maxY, maxY));
    }

    /// <summary>지금 zoom 기준 크기로 SkiaSharp를 이용해 한 번만 스케일해서 renderCache에 담아둔다.
    /// 실패하면 renderCache를 비워서 OnPaint가 예전 방식(GDI+ 직접 스케일)으로 안전하게 대신 그리게 한다.</summary>
    private void RebuildRenderCache()
    {
        renderCache?.Dispose();
        renderCache = null;
        if (image is not Bitmap source) return;

        int w = Math.Max(1, (int)Math.Round(image.Width * zoom));
        int h = Math.Max(1, (int)Math.Round(image.Height * zoom));
        try
        {
            renderCache = ScaleWithSkia(source, w, h);
        }
        catch
        {
            renderCache = null;
        }
    }

    /// <summary>GDI+ 비트맵을 SkiaSharp로 지정한 크기로 스케일해서 새 GDI+ 비트맵으로 돌려준다.
    /// 원본·결과 픽셀 버퍼를 모두 그대로 잠가서 넘기기 때문에(복사 없이) 재인코딩 왕복이 없다.</summary>
    private static Bitmap ScaleWithSkia(Bitmap source, int destW, int destH)
    {
        var srcRect = new Rectangle(0, 0, source.Width, source.Height);
        var srcLock = source.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var srcInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var skSource = new SKBitmap();
            skSource.InstallPixels(srcInfo, srcLock.Scan0, srcLock.Stride);

            var dest = new Bitmap(destW, destH, PixelFormat.Format32bppArgb);
            var destRect = new Rectangle(0, 0, destW, destH);
            var destLock = dest.LockBits(destRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var destInfo = new SKImageInfo(destW, destH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var surface = SKSurface.Create(destInfo, destLock.Scan0, destLock.Stride);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(skSource, new SKRect(0, 0, destW, destH), paint);
                canvas.Flush();
            }
            finally
            {
                dest.UnlockBits(destLock);
            }
            return dest;
        }
        finally
        {
            source.UnlockBits(srcLock);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (image == null) return;
        var g = e.Graphics;
        PaintImage(g);
        PaintPageInfo(g);
    }

    /// <summary>왼쪽 위 모서리에 반투명 검정 바탕 + 흰 글씨로 페이지 정보를 그린다(그림 위에 겹쳐서).</summary>
    private void PaintPageInfo(Graphics g)
    {
        if (string.IsNullOrEmpty(pageInfoText)) return;
        using var font = new Font("맑은 고딕", 10f, FontStyle.Bold);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var size = g.MeasureString(pageInfoText, font);
        var box = new RectangleF(8, 8, size.Width + 10, size.Height + 4);
        using (var back = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.FillRectangle(back, box);
        g.DrawString(pageInfoText, font, Brushes.White, box.X + 5, box.Y + 2);
    }

    private void PaintImage(Graphics g)
    {
        if (image == null) return;

        if (renderCache != null)
        {
            // 이미 zoom에 맞게 미리 스케일해둔 캐시를 그대로 찍기만 한다 — 다시 스케일하지 않는다.
            float cx = (Width - renderCache.Width) / 2f + OffX + pan.X;
            float cy = (Height - renderCache.Height) / 2f + OffY + pan.Y;
            g.DrawImageUnscaled(renderCache, (int)Math.Round(cx), (int)Math.Round(cy));
        }
        else
        {
            // 캐시를 못 만들었을 때의 안전망(예전 방식 그대로 GDI+가 직접 스케일해서 그린다).
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            float w = image.Width * zoom;
            float h = image.Height * zoom;
            float x = (Width - w) / 2f + OffX + pan.X;
            float y = (Height - h) / 2f + OffY + pan.Y;
            g.DrawImage(image, x, y, w, h);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation?.Dispose();
            image?.Dispose();
            renderCache?.Dispose();
            tapTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
