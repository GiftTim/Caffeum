using System.Net;
using System.Text;

namespace CoffeeViewer;

/// <summary>
/// txt를 epub처럼 WebView2로 보여주려고, 글을 작은 HTML 문서 여러 개로 나눠 임시 폴더에 써 둔 "가상 epub".
/// 메모장 같은 TextBox로는 줄 간격 · 문단 간격 · 다단 페이지를 못 주므로, epub과 같은 웹 화면(페이지 넘김 ·
/// 테마 · 글꼴 · 낭독 · 이미지 확대 스크립트)을 그대로 같이 쓰게 하려는 것이다.
/// 한 문서가 너무 크면 다단 배치(CSS column)가 느려지므로, 챕터(TxtChapterSplitter)를 기본 단위로 하되
/// 큰 챕터는 MaxChunk 글자 안쪽에서 줄 경계로 다시 자른다.
/// 각 줄은 &lt;p data-o="전체 글 기준 시작 위치"&gt;로 쓰고 white-space: pre-wrap으로 공백까지 그대로 살린다 —
/// 그래서 문단 안의 글자 위치가 원본 글자 위치와 1:1로 맞아, 읽던 자리 · 찾기를 원본 위치로 주고받을 수 있다.
/// </summary>
public sealed class TxtWebBook
{
    private const int MaxChunk = 40000;

    public EpubBook Book { get; }
    /// <summary>각 문서가 맡은 전체 글 기준 [Start, End) 구간. Book.Chapters와 같은 순서·개수.</summary>
    public IReadOnlyList<(int Start, int End)> Ranges { get; }
    public int TextLength { get; }

    private TxtWebBook(EpubBook book, List<(int, int)> ranges, int textLength)
    {
        Book = book;
        Ranges = ranges;
        TextLength = textLength;
    }

    /// <summary>text는 줄바꿈이 \r\n으로 정리된 전체 글(ShowLoadedText 참고), chapters는 그 글을 나눈 챕터 목록.</summary>
    public static TxtWebBook Build(string text, IReadOnlyList<TxtChapter> chapters, string title, string sourcePath)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CoffeeViewerTxt",
            Path.GetFileNameWithoutExtension(sourcePath) + "_" + Math.Abs(sourcePath.GetHashCode()));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 못 지운 옛 파일은 아래에서 덮어쓴다 */ }
        Directory.CreateDirectory(dir);

        var parts = new List<(string Name, int Start, int End)>();
        foreach (var ch in chapters)
        {
            int start = ch.Start, part = 1;
            while (start < ch.End)
            {
                int end = ch.End;
                if (end - start > MaxChunk)
                {
                    // 글자 수 한도 안쪽의 마지막 줄바꿈에서 자른다(줄 중간에서 끊기지 않게). 한 줄이 한도보다 길면 그냥 한도에서.
                    int nl = text.LastIndexOf('\n', start + MaxChunk - 1, MaxChunk);
                    end = nl > start ? nl + 1 : start + MaxChunk;
                }
                parts.Add((part == 1 ? ch.Name : $"{ch.Name} ({part})", start, end));
                start = end;
                part++;
            }
        }
        if (parts.Count == 0) parts.Add((title, 0, text.Length)); // 빈 파일도 문서 하나는 있어야 화면이 뜬다

        var epubChapters = new List<EpubChapter>();
        var ranges = new List<(int, int)>();
        for (int i = 0; i < parts.Count; i++)
        {
            var (name, start, end) = parts[i];
            string file = Path.Combine(dir, $"{i + 1:D4}.html");
            File.WriteAllText(file, BuildHtml(text, start, end, name), new UTF8Encoding(false));
            epubChapters.Add(new EpubChapter(name, file));
            ranges.Add((start, end));
        }
        return new TxtWebBook(new EpubBook { Title = title, Chapters = epubChapters }, ranges, text.Length);
    }

    /// <summary>offset(전체 글 기준)이 들어 있는 문서 번호.</summary>
    public int ChunkAt(int offset)
    {
        int lo = 0, hi = Ranges.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Ranges[mid].Start <= offset) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    private static string BuildHtml(string text, int start, int end, string name)
    {
        var sb = new StringBuilder((end - start) * 6 / 5 + 4096);
        sb.Append("<!DOCTYPE html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>")
          .Append(WebUtility.HtmlEncode(name))
          .Append("</title><style>")
          .Append("html,body{margin:0}")
          .Append("body.clr-txt p{margin:0;white-space:pre-wrap;word-break:keep-all;overflow-wrap:anywhere}")
          .Append("</style><script>").Append(HelperScript).Append("</script></head><body class=\"clr-txt\">\n");

        int pos = start;
        while (pos < end)
        {
            int nl = text.IndexOf('\n', pos, end - pos);
            int lineEnd = nl < 0 ? end : nl;
            int contentEnd = lineEnd > pos && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            sb.Append("<p data-o=\"").Append(pos).Append("\">");
            if (contentEnd > pos) sb.Append(WebUtility.HtmlEncode(text.Substring(pos, contentEnd - pos)));
            else sb.Append("<br>"); // 빈 줄도 한 줄 높이를 차지하게
            sb.Append("</p>\n");
            pos = nl < 0 ? end : nl + 1;
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 문서마다 심어 두는 도우미: 원본 글자 위치(data-o 기준)로 화면 위치를 찾고 옮긴다.
    /// - __clrTxtTop(): 지금 화면에 보이는 첫 줄의 원본 위치
    /// - __clrTxtGo(off): 그 위치가 있는 줄이 보이게(페이지 방식이면 그 페이지로, 스크롤이면 화면 맨 위로)
    /// - __clrTxtSel(start, len): 찾은 글자를 표시하고 보이게
    /// 페이지 방식의 __clrGoTo · __clrPageWidth는 epub용 다단 스크립트(EpubPaginateScript)가 나중에 심는다.
    /// </summary>
    private const string HelperScript = @"
(function(){
  function paras() { return document.querySelectorAll('p[data-o]'); }
  function paraAt(off) {
    var ps = paras(), lo = 0, hi = ps.length - 1, ans = ps.length ? 0 : -1;
    while (lo <= hi) { var mid = (lo + hi) >> 1; if (+ps[mid].dataset.o <= off) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
    return ans < 0 ? null : ps[ans];
  }
  function show(rect) {
    if (window.__clrGoTo && window.__clrTotalPages !== undefined) {
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      var page = Math.floor((rect.left - tx + 1) / window.__clrPageWidth);
      if (page !== window.__clrPageIndex) window.__clrGoTo(page);
    } else {
      window.scrollTo(0, window.scrollY + rect.top - Math.min(40, window.innerHeight * 0.1));
    }
  }
  window.__clrTxtTop = function() {
    var ps = paras(), vw = window.innerWidth, vh = window.innerHeight;
    for (var i = 0; i < ps.length; i++) {
      var b = ps[i].getBoundingClientRect();
      if (b.bottom > 0 && b.top < vh && b.right > 0 && b.left < vw) return +ps[i].dataset.o;
    }
    return ps.length ? +ps[0].dataset.o : 0;
  };
  window.__clrTxtGo = function(off) {
    var p = paraAt(off);
    if (p) show(p.getBoundingClientRect());
  };
  window.__clrTxtSel = function(start, len) {
    var p = paraAt(start);
    if (!p) return;
    var node = p.firstChild, r = document.createRange();
    if (node && node.nodeType === 3) {
      var a = Math.max(0, Math.min(node.nodeValue.length, start - (+p.dataset.o)));
      r.setStart(node, a);
      r.setEnd(node, Math.min(node.nodeValue.length, a + len));
    } else r.selectNodeContents(p);
    var st = document.getElementById('__clr_find_style');
    if (!st) { st = document.createElement('style'); st.id = '__clr_find_style'; st.textContent = '::highlight(clr-find) { background-color: #ff9632; color: #000000; }'; document.head.appendChild(st); }
    try { CSS.highlights.set('clr-find', new Highlight(r)); } catch (e) {}
    show(r.getBoundingClientRect());
  };
})();
";
}
