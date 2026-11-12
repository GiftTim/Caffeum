using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>도구 > 빠른 교정: 글에 흔한 자잘한 흐트러짐(줄 끝 공백 · 겹친 띄어쓰기 · 빈 줄 · 말줄임표 …)을 한 번에 고친다.</summary>
internal sealed class QuickFixDialog : Form
{
    private sealed record Rule(string Name, string Hint, bool Default, Func<string, string> Apply, Func<string, int> Count);

    private static readonly Rule[] Rules =
    {
        Make("줄 끝 공백 지우기", "줄 맨 끝에 붙은 띄어쓰기 · 탭", true, @"(?m)[ \t\u3000]+(?=\r?$)", _ => ""),
        Make("겹친 띄어쓰기를 한 칸으로", "글 가운데 두 칸 이상 띄운 곳(줄 앞 들여쓰기는 그대로)", true, @"(?<=\S)[ ]{2,}(?=\S)", _ => " "),
        Make("빈 줄 여러 개를 한 줄로", "빈 줄이 두 줄 넘게 이어진 곳", true, @"(\r?\n){3,}", m => { string u = m.Value.StartsWith("\r\n") ? "\r\n" : "\n"; return u + u; }),
        Make("말줄임표를 …로 통일", "'..' · '...' · '......' → … (여섯 점 이상은 ……)", true, @"(?<![\.…])\.{2,}(?!\.)", m => m.Length >= 6 ? "……" : "…"),
        Make("-- 를 — 로", "붙어 있는 하이픈 두 개를 줄표로", false, @"(?<!-)--(?!-)", _ => "—"),
        Make("곧은 큰따옴표를 둥근 따옴표로", "\"…\" → “…” (짝이 맞는 것만)", false, "\"([^\"\r\n]*)\"", m => "“" + m.Groups[1].Value + "”"),
    };

    private static Rule Make(string name, string hint, bool def, string pattern, MatchEvaluator ev)
    {
        var rx = new Regex(pattern);
        return new Rule(name, hint, def, s => rx.Replace(s, ev), s => rx.Matches(s).Count);
    }

    private readonly CheckedListBox list;
    private readonly string source;
    public string Result { get; private set; }

    public QuickFixDialog(string text)
    {
        source = text;
        Result = text;
        Text = "빠른 교정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Shown += (_, _) => { Activate(); BringToFront(); }; // 앱 안에서만 앞으로 온다(다른 프로그램 위로 뜨지는 않는다)
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        var info = new Label { Text = "고칠 항목을 고르세요. 괄호 안은 지금 글에서 찾은 곳 수예요.", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        list = new CheckedListBox { Width = 440, Height = 190, IntegralHeight = false, CheckOnClick = true, Margin = new Padding(0, 0, 0, 6) };
        var hint = new Label { AutoSize = false, Width = 440, Height = 22, Margin = new Padding(0, 0, 0, 10) };
        foreach (var r in Rules)
        {
            int n = r.Count(text);
            list.Items.Add($"{r.Name}  ({n:N0}곳)", r.Default);
        }
        list.SelectedIndexChanged += (_, _) => hint.Text = list.SelectedIndex >= 0 ? Rules[list.SelectedIndex].Hint : "";
        list.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var apply = new Button { Text = "교정하기", AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "닫기", AutoSize = true, MinimumSize = new Size(84, 30), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);
        apply.Click += (_, _) =>
        {
            string s = source;
            for (int i = 0; i < Rules.Length; i++) if (list.GetItemChecked(i)) s = Rules[i].Apply(s);
            Result = s;
        };
        root.Controls.Add(info);
        root.Controls.Add(list);
        root.Controls.Add(hint);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = apply;
        CancelButton = cancel;
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}
