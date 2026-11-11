using System.Windows.Forms;

namespace CoffeeViewer;

/// <summary>
/// 단축키가 "이 탭"에서만 먹는 메뉴 항목. 볼트 창에는 탭마다 MainForm(과 그 메뉴)이 하나씩 들어 있는데,
/// WinForms는 같은 최상위 창 안의 모든 메뉴에서 단축키를 찾아 처음 걸리는 것을 실행한다 — 그래서 Ctrl+S 같은 키가
/// 지금 보는 탭이 아니라 먼저 열린(숨은) 탭의 메뉴로 가버렸다. gate가 false이면 단축키를 그냥 흘려보낸다.
/// </summary>
internal sealed class ScopedMenuItem : ToolStripMenuItem
{
    private readonly Func<bool> gate;

    public ScopedMenuItem(string text, EventHandler onClick, Func<bool> gate) : base(text, null, onClick) => this.gate = gate;

    protected override bool ProcessCmdKey(ref Message m, Keys keyData) =>
        gate() && base.ProcessCmdKey(ref m, keyData);
}
