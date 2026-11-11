using System.Runtime.InteropServices;

namespace CoffeeViewer;

/// <summary>
/// 팝업 메뉴가 열려 있는 동안 메뉴 밖을 한 번 누르면 닫는다. WinForms 기본 동작은 웹 화면(WebView) 위를 누를 때처럼
/// 다른 창이 클릭을 받으면 메뉴를 닫지 못하는 경우가 있어서, 마우스 단추가 새로 눌리는 순간을 직접 지켜본다.
/// </summary>
internal static class MenuGuard
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static bool AnyButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    private static bool Inside(ToolStripDropDown dd, Point p)
    {
        if (!dd.Visible) return false;
        if (dd.Bounds.Contains(p)) return true;
        foreach (ToolStripItem i in dd.Items)
            if (i is ToolStripDropDownItem d && d.DropDown.Visible && Inside(d.DropDown, p)) return true;
        return false;
    }

    public static void CloseOnOutsideClick(ToolStripDropDown menu)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        bool wasDown = AnyButtonDown(); // 메뉴를 연 그 클릭이 아직 눌려 있어도 닫지 않게
        void Stop() { if (timer.Enabled) timer.Stop(); }
        timer.Tick += (_, _) =>
        {
            if (!menu.Visible) { Stop(); timer.Dispose(); return; }
            bool down = AnyButtonDown();
            if (down && !wasDown && !Inside(menu, Cursor.Position)) menu.Close(ToolStripDropDownCloseReason.AppClicked);
            wasDown = down;
        };
        menu.Closed += (_, _) => Stop();
        timer.Start();
    }
}
