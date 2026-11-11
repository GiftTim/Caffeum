using System.Text.RegularExpressions;

namespace CoffeeViewer;

public partial class MainForm
{
    /// <summary>⋯ 메뉴에 [도구 > 빠른 교정 내용… / 글 한 번에 다듬기…]를 넣고, 글 치는 동안의 자동 교정을 건다(도움말 앞).</summary>
    private void AddToolsMenu()
    {
        if (toolbarMore == null) return;
        var tools = new ToolStripMenuItem("도구");
        var content = new ToolStripMenuItem("빠른 교정 내용…", null, (_, _) => { using var dlg = new QuickCorrectDialog(settings); dlg.ShowDialog(HostForm); });
        var quick = new ToolStripMenuItem("글 한 번에 다듬기…", null, (_, _) => RunQuickFix());
        tools.DropDownItems.Add(content);
        tools.DropDownItems.Add(quick);
        tools.DropDownOpening += (_, _) => quick.Enabled = shownKind == ContentKind.Txt && !mainEditor.ReadOnly && !txtWeb; // 고치는 중인 글에서만
        int at = toolbarMore.DropDownItems.Cast<ToolStripItem>().ToList().FindIndex(i => i.Text.StartsWith("도움말"));
        if (at < 0) toolbarMore.DropDownItems.Add(tools); else toolbarMore.DropDownItems.Insert(at, tools);
        mainEditor.KeyPress += OnQuickKeyPress;
    }

    private void RunQuickFix()
    {
        if (shownKind != ContentKind.Txt || mainEditor.ReadOnly) { ShowToast("글을 고칠 수 있을 때만 쓸 수 있어요"); return; }
        using var dlg = new QuickFixDialog(mainEditor.Text);
        if (dlg.ShowDialog(HostForm) != DialogResult.OK || dlg.Result == mainEditor.Text) return;
        int caret = mainEditor.SelectionStart;
        mainEditor.SelectAll();
        mainEditor.SelectedText = dlg.Result; // 한 번에 바꿔 넣어서 Ctrl+Z로 되돌릴 수 있다
        mainEditor.SelectionStart = Math.Min(caret, mainEditor.TextLength);
        mainEditor.SelectionLength = 0;
        ShowToast("빠른 교정을 적용했어요 (Ctrl+Z로 되돌릴 수 있어요)");
    }

    /// <summary>글을 치는 동안: 입력 자동 서식(…, —, 따옴표, 괄호) → 입력 자동 명령 → 빠른 교정(낱말이 끝났을 때) 순으로 적용한다.</summary>
    private void OnQuickKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (mainEditor.ReadOnly || shownKind != ContentKind.Txt || txtWeb) return;
        char c = e.KeyChar;
        if (c < ' ' && c != '\r') return; // 백스페이스 · 컨트롤 키
        string text = mainEditor.Text;
        int caret = mainEditor.SelectionStart;
        int selLen = mainEditor.SelectionLength;

        // 입력 자동 서식 — 친 글자 자리에서 바로 바꾼다
        if (selLen == 0)
        {
            if ((settings.AutoEllipsis || settings.AutoEllipsisDots) && c == '.' && caret >= 2 && text[caret - 1] == '.' && text[caret - 2] == '.')
            {
                mainEditor.Select(caret - 2, 2);
                mainEditor.SelectedText = settings.AutoEllipsisDots ? "······" : "…";
                e.Handled = true;
                return;
            }
            if (settings.AutoDash && c == '-' && caret >= 1 && text[caret - 1] == '-')
            {
                mainEditor.Select(caret - 1, 1);
                mainEditor.SelectedText = "—";
                e.Handled = true;
                return;
            }
        }
        if (settings.AutoBrackets && selLen == 0)
        {
            int open = "([{".IndexOf(c);
            if (open >= 0)
            {
                mainEditor.SelectedText = c.ToString() + ")]}"[open];
                mainEditor.SelectionStart = caret + 1;
                e.Handled = true;
                return;
            }
            if ((c == ')' || c == ']' || c == '}') && caret < text.Length && text[caret] == c) // 이미 닫는 괄호가 있으면 그 뒤로 넘어간다
            {
                mainEditor.SelectionStart = caret + 1;
                e.Handled = true;
                return;
            }
        }
        if (settings.AutoQuotes && c == '"')
        {
            char prev = caret > 0 ? text[caret - 1] : '\n';
            bool isOpen = caret == 0 || char.IsWhiteSpace(prev) || "([{<“‘—".IndexOf(prev) >= 0;
            mainEditor.SelectedText = isOpen ? "“" : "”";
            e.Handled = true;
            return;
        }

        // 입력 자동 명령 · 빠른 교정 · 서수 · 날짜 · 목록 잇기는 글자가 들어간 뒤(IME 조합이 끝난 뒤)에 살핀다
        bool boundary = QuickCorrect.IsBoundary(c);
        bool listKey = c == '\r' && (settings.AutoBullet || settings.AutoNumber);
        bool wordKey = boundary && (settings.QuickCorrect.Count > 0 || settings.AutoOrdinal || settings.AutoDate);
        if (settings.AutoCommands.Count == 0 && !wordKey && !listKey) return;
        BeginInvoke(() => { ApplyQuickCorrect(boundary, c == '\r'); if (listKey) ContinueList(); });
    }

    private void ApplyQuickCorrect(bool boundary, bool enter)
    {
        if (mainEditor.ReadOnly || mainEditor.SelectionLength != 0) return;
        string text = mainEditor.Text;
        int caret = mainEditor.SelectionStart;
        if (caret <= 0 || caret > text.Length) return;

        // 1) 입력 자동 명령: 방금 친 글자까지 포함해 끝이 '입력할 말'이면 바로 바꾼다(가장 긴 것부터)
        foreach (var p in settings.AutoCommands.Where(p => p.From.Length > 0).OrderByDescending(p => p.From.Length))
        {
            if (caret >= p.From.Length && string.CompareOrdinal(text, caret - p.From.Length, p.From, 0, p.From.Length) == 0)
            {
                mainEditor.Select(caret - p.From.Length, p.From.Length);
                mainEditor.SelectedText = p.To;
                return;
            }
        }
        if (!boundary || (settings.QuickCorrect.Count == 0 && !settings.AutoOrdinal && !settings.AutoDate)) return;

        // 2) 방금 친 구분 글자(띄어쓰기 · 엔터 · 문장부호) 바로 앞 낱말을 살핀다
        int delimLen = enter && caret >= 2 && text[caret - 2] == '\r' ? 2 : 1;
        int end = caret - delimLen;
        if (end <= 0) return;
        int start = end;
        while (start > 0 && !QuickCorrect.IsBoundary(text[start - 1])) start--;
        string word = text.Substring(start, end - start);

        // 날짜(2024.5.3 + 띄어쓰기 → 2024.5.3.): '.'이 구분 글자라 낱말이 끊기니 숫자와 점을 이어서 다시 본다
        if (settings.AutoDate && text[end] == ' ')
        {
            int ds = end;
            while (ds > 0 && (char.IsDigit(text[ds - 1]) || text[ds - 1] == '.')) ds--;
            string date = text.Substring(ds, end - ds);
            if (Regex.IsMatch(date, @"^\d{4}\.\d{1,2}\.\d{1,2}$"))
            {
                ReplaceRange(ds, end - ds, date + ".", caret);
                return;
            }
        }
        // 서수(1st → 1ˢᵗ)
        if (settings.AutoOrdinal && start < end && Regex.IsMatch(word, @"^\d+(st|nd|rd|th)$"))
        {
            int n = 0;
            while (char.IsDigit(word[n])) n++;
            string sup = string.Concat(word.Substring(n).Select(ch => ch switch { 's' => 'ˢ', 't' => 'ᵗ', 'n' => 'ⁿ', 'd' => 'ᵈ', 'r' => 'ʳ', 'h' => 'ʰ', _ => ch }));
            ReplaceRange(start, end - start, word.Substring(0, n) + sup, caret);
            return;
        }

        // 빠른 교정: 구분 글자 바로 앞이 사전의 틀린 말로 끝나면 맞는 말로(가장 긴 것부터).
        // 맨 앞의 "-"는 한글처럼 어미 · 조사 표시: 낱말 앞부분과 상관없이 끝만 맞으면 바꾸고, "-"는 글자로 치지 않는다.
        foreach (var p in settings.QuickCorrect.Where(p => p.From.Length > 0).OrderByDescending(p => p.From.Length))
        {
            bool ending = p.From.Length > 1 && p.From[0] == '-';
            string from = ending ? p.From.Substring(1) : p.From;
            string to = ending && p.To.StartsWith("-") ? p.To.Substring(1) : p.To;
            int ms = end - from.Length;
            if (ms < 0 || string.CompareOrdinal(text, ms, from, 0, from.Length) != 0 || from == to) continue;
            // 보통 항목은 낱말 맨 앞에서 시작해야 한다(문장부호로 시작하는 항목은 예외)
            if (!ending && ms > 0 && char.IsLetterOrDigit(from[0]) && !QuickCorrect.IsBoundary(text[ms - 1])) continue;
            ReplaceRange(ms, from.Length, to, caret);
            return;
        }
    }

    /// <summary>[start, start+len) 를 text로 바꾸고, 커서는 원래 자리(oldCaret) 기준으로 글자 수 차이만큼 옮겨 둔다.</summary>
    private void ReplaceRange(int start, int len, string text, int oldCaret)
    {
        mainEditor.Select(start, len);
        mainEditor.SelectedText = text;
        mainEditor.SelectionStart = oldCaret + (text.Length - len);
        mainEditor.SelectionLength = 0;
    }

    /// <summary>엔터를 친 뒤: 앞 줄이 글머리 기호(- * • ·)나 번호(1.)로 시작했으면 새 줄에도 이어서 넣고, 앞 줄이 기호뿐이면 목록을 끝낸다.</summary>
    private void ContinueList()
    {
        if (mainEditor.ReadOnly || mainEditor.SelectionLength != 0) return;
        string text = mainEditor.Text;
        int caret = mainEditor.SelectionStart;
        if (caret < 2 || text[caret - 1] != '\n') return;
        int lineEnd = caret - (text[caret - 2] == '\r' ? 2 : 1);
        int lineStart = lineEnd > 0 ? text.LastIndexOf('\n', lineEnd - 1) + 1 : 0;
        string line = text.Substring(lineStart, lineEnd - lineStart);
        if (settings.AutoBullet)
        {
            var m = Regex.Match(line, @"^(\s*)([-*•·])\s+(.*)$");
            if (m.Success)
            {
                if (m.Groups[3].Value.Length == 0) { mainEditor.Select(lineStart, lineEnd - lineStart); mainEditor.SelectedText = ""; return; }
                mainEditor.SelectedText = m.Groups[1].Value + m.Groups[2].Value + " ";
                return;
            }
        }
        if (settings.AutoNumber)
        {
            var m = Regex.Match(line, @"^(\s*)(\d+)([.)])\s+(.*)$");
            if (m.Success)
            {
                if (m.Groups[4].Value.Length == 0) { mainEditor.Select(lineStart, lineEnd - lineStart); mainEditor.SelectedText = ""; return; }
                mainEditor.SelectedText = m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + 1) + m.Groups[3].Value + " ";
            }
        }
    }
}
