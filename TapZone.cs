namespace CoffeeViewer;

/// <summary>
/// 페이지 터치(화면 탭으로 넘기기)의 영역 나누기. 화면을 세로로 셋으로 나눠 왼쪽 = 뒤로, 오른쪽 = 앞으로,
/// 가운데는 아무것도 안 한다 — 딱 반으로만 나누면 가운데 근처를 누를 때 어느 쪽으로 넘어갈지 애매해서다.
/// txt(MainForm), 만화(ImageCanvas), epub(EpubPaginateScript의 JS) 세 곳이 모두 이 기준을 쓴다.
/// </summary>
internal static class TapZone
{
    /// <summary>화면 너비 중 "눌러도 안 넘어가는" 가운데 칸이 차지하는 비율.</summary>
    public const double CenterRatio = 1.0 / 3;

    /// <summary>탭한 가로 위치 → -1(뒤로) / 0(가운데, 무시) / 1(앞으로).</summary>
    public static int Direction(int x, int width)
    {
        double side = width * (1 - CenterRatio) / 2;
        if (x < side) return -1;
        if (x >= width - side) return 1;
        return 0;
    }
}
