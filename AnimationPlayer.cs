using SkiaSharp;

namespace CoffeeViewer;

/// <summary>
/// 움직이는 그림(webp·gif)을 재생한다. 프레임을 미리 다 풀어두지 않고, 백그라운드 스레드에서 다음 프레임을 그때그때
/// 디코딩해서(실측 프레임당 10~25ms, 프레임 간격은 보통 60ms대) 시간이 되면 onFrame으로 UI 스레드에 넘긴다.
/// onFrame은 owner.Invoke로 UI 스레드에서 불리고, 넘겨받은 Bitmap의 소유권(Dispose 책임)도 같이 넘어간다.
/// Invoke(동기)로 넘기기 때문에 UI가 밀리면 디코딩도 같이 쉬어서, 프레임이 대기줄에 쌓이지 않는다.
/// </summary>
public sealed class AnimationPlayer : IDisposable
{
    private readonly CancellationTokenSource cts = new();

    public static bool IsAnimated(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        return codec != null && codec.FrameCount > 1;
    }

    /// <param name="isVisible">false인 동안은(다른 화면으로 넘어가 캔버스가 숨었을 때) 디코딩을 쉰다.</param>
    /// <param name="loopForever">마지막 프레임에 닿을 때마다 묻는다 — true면 처음부터 다시, false면 마지막 프레임에서 멈춘다.
    /// 파일에 적힌 반복 횟수 대신 이 설정을 따른다(설정 > 영상 처리 > 움직이는 그림 무한 반복).</param>
    public AnimationPlayer(byte[] bytes, Control owner, Func<bool> isVisible, Func<bool> loopForever, Action<Bitmap> onFrame)
    {
        var token = cts.Token;
        Task.Run(() => Run(bytes, owner, isVisible, loopForever, onFrame, token), token);
    }

    private static async Task Run(byte[] bytes, Control owner, Func<bool> isVisible, Func<bool> loopForever, Action<Bitmap> onFrame, CancellationToken token)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec == null) return;
            var frames = codec.FrameInfo;
            if (frames.Length < 2) return;

            // 한 버퍼에 프레임을 차례로 겹쳐 그린다 — 움직이는 그림은 이전 프레임 위에 덧그리는(부분 갱신) 경우가
            // 많아서, 직전 프레임이 담긴 버퍼를 priorFrame으로 알려주면 Skia가 처음부터 다시 합성하지 않는다.
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var buffer = new SKBitmap(info);
            int index = 0; // 첫 프레임은 ImageLoader가 이미 보여줬으니, 그 다음 프레임부터 시간 맞춰 넘긴다
            var due = DateTime.UtcNow;

            // 버퍼를 첫 프레임 상태로 채워둔다(다음 프레임이 이걸 바탕으로 덧그린다).
            codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(0));

            while (!token.IsCancellationRequested)
            {
                due += TimeSpan.FromMilliseconds(FrameDuration(frames[index]));
                int next = index + 1;
                if (next >= frames.Length)
                {
                    if (!loopForever()) return; // 한 번만 재생 — 마지막 프레임이 떠 있는 채로 멈춘다
                    next = 0;
                }

                // 지금 버퍼엔 index 프레임이 들어 있다. next가 그걸 바탕으로 그리는 프레임이면 priorFrame으로 알려준다.
                // 단 index가 "이전 상태로 되돌리기"(gif의 RestorePrevious) 프레임이면 버퍼를 바탕으로 쓸 수 없으니
                // Skia가 필요한 프레임부터 알아서 다시 합성하게 한다.
                bool canReuse = next != 0 && frames[next].RequiredFrame != -1
                    && frames[index].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious;
                var result = codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(next, canReuse ? index : -1));
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput && canReuse)
                    result = codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(next)); // 안전망
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) return;
                index = next;

                var frame = ImageLoader.ToGdiBitmap(buffer);
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                else if (wait < TimeSpan.FromMilliseconds(-500)) due = DateTime.UtcNow; // 한참 밀렸으면(창을 끌던 중 등) 따라잡으려 빨리 감지 않는다

                while (!isVisible())
                {
                    await Task.Delay(200, token);
                    due = DateTime.UtcNow;
                }

                bool delivered = false;
                try
                {
                    owner.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return; // 그사이 페이지가 넘어갔으면 버린다
                        onFrame(frame);
                        delivered = true;
                    });
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    // 창이 닫히는 중
                }
                if (!delivered) { frame.Dispose(); return; }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 재생 실패는 조용히 멈춘다 — 첫 프레임은 이미 떠 있으니 정지 그림으로 남는다.
        }
    }

    // 브라우저들처럼, 0ms·10ms 같은 비정상적으로 짧은 간격은 100ms로 본다(안 그러면 CPU만 태우며 폭주한다).
    // gif는 간격을 0으로 적어둔 파일이 흔하다.
    private static int FrameDuration(SKCodecFrameInfo f) => f.Duration <= 10 ? 100 : f.Duration;

    // cts 자체는 Dispose하지 않는다 — 백그라운드 루프가 아직 토큰을 보고 있을 수 있어서, 멈추라고 알리기만 한다.
    public void Dispose() => cts.Cancel();
}
