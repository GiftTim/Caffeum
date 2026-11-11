using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>
/// 텍스트 파일 안에서 잘라낸 한 화(챕터). Start/End는 원본 전체 문자열에서
/// 이 챕터가 차지하는 [Start, End) 구간 — 나중에 편집 저장 기능을 붙일 때
/// 이 구간만 갈아끼워 원본 파일을 재구성하는 데 쓴다(옵시디언 플러그인과 같은 방식).
/// </summary>
public sealed record TxtChapter(string Name, string Text, int Start, int End);

/// <summary>txt를 챕터로 나누는 방식. 설정창의 라디오 버튼 순서와 같다.</summary>
public enum TxtSplitMode
{
    /// <summary>제목 줄로 나누고, 그래도 큰 구간은 글자 수로 다시 자른다(기본).</summary>
    Auto,
    /// <summary>고른 제목 형식에 맞는 줄에서만 자른다. 글자 수로는 다시 자르지 않는다.</summary>
    Heading,
    /// <summary>제목은 무시하고 정한 글자 수마다 문단 경계에서 자른다.</summary>
    Size,
    /// <summary>나누지 않고 파일 전체를 챕터 하나로 둔다.</summary>
    None,
}

/// <summary>챕터 나누기 설정값 한 벌. AppSettings에서 만들어 Split에 넘긴다(백그라운드 스레드에서도 안전하게 값만 복사).</summary>
public sealed class TxtSplitOptions
{
    public const int DefaultChunkSize = 12000;
    public const int MinChunkSize = 2000;
    public const int MaxChunkSize = 200000;

    /// <summary>제목 형식 프리셋. 키는 AppSettings.TxtHeadingPresets에 저장된다. 줄 하나(끝의 \r 제외)에 대해 검사한다.</summary>
    public static readonly (string Key, string Label, string Example, string Pattern, RegexOptions Options)[] Presets =
    {
        ("episode", "N화", "1화 · 제12화 · 37화. 제목",
            @"^\s*(?:제\s*)?\d{1,5}\s*화(?=\s|[.:\-)\]】]|$)", RegexOptions.None),
        ("jang", "N장", "1장 · 제3장 · 3장. 제목",
            @"^\s*(?:제\s*)?\d{1,5}\s*장(?=\s|[.:\-)\]】]|$)", RegexOptions.None),
        ("bu", "제N부", "제1부 · 2부",
            @"^\s*(?:제\s*)?\d{1,5}\s*부(?=\s|[.:\-)\]】]|$)", RegexOptions.None),
        ("english", "Chapter N", "Chapter 1 · Ch. 2 · Episode 3",
            @"^\s*(?:chapter|ch\.?|episode|ep\.?)\s*\d{1,5}\b", RegexOptions.IgnoreCase),
        ("special", "프롤로그 · 에필로그 · 외전", "프롤로그 · 에필로그 · 외전 · 번외 · 작가의 말",
            @"^\s*(?:프롤로그|에필로그|외전|번외|서장|종장|후기|작가의\s*말)(?=\s|[.:\-)\]】]|$)", RegexOptions.None),
        ("number", "숫자로 시작하는 줄", "001 · 1. 제목 · 2) 제목 · [3]",
            @"^\s*(?:\d{1,5}\s*(?:[.)]\s*\S.*)?|[\[\(]\d{1,5}[\]\)].*)$", RegexOptions.None),
    };

    public TxtSplitMode Mode { get; init; } = TxtSplitMode.Auto;
    public IReadOnlyList<string> PresetKeys { get; init; } = new[] { "episode" };
    public string CustomRegex { get; init; } = "";
    public int ChunkSize { get; init; } = DefaultChunkSize;

    public static TxtSplitOptions Default { get; } = new();

    public static TxtSplitOptions From(AppSettings s) => new()
    {
        Mode = s.TxtSplitMode,
        PresetKeys = s.TxtHeadingPresets.ToArray(),
        CustomRegex = s.TxtHeadingCustomRegex ?? "",
        ChunkSize = Math.Clamp(s.TxtChunkSize, MinChunkSize, MaxChunkSize),
    };

    /// <summary>직접 입력한 정규식이 쓸 수 있는 것인지. 비어 있으면 true(쓰지 않음).</summary>
    public static bool IsValidRegex(string pattern, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>고른 프리셋과 직접 입력한 정규식을 합쳐 "제목 줄인가"를 판별하는 검사기들을 만든다. 잘못된 정규식은 조용히 뺀다.</summary>
    internal List<Regex> BuildHeadingRegexes()
    {
        var list = new List<Regex>();
        foreach (var p in Presets)
            if (PresetKeys.Contains(p.Key))
                list.Add(new Regex(p.Pattern, p.Options,TimeSpan.FromSeconds(1)));
        if (!string.IsNullOrWhiteSpace(CustomRegex) && IsValidRegex(CustomRegex, out _))
            list.Add(new Regex(CustomRegex, RegexOptions.None, TimeSpan.FromSeconds(1)));
        return list;
    }
}

/// <summary>
/// 옵시디언 image-zip-viewer 플러그인의 splitTxtIntoChapters와 같은 규칙을 옮긴 것(기본값 기준).
/// 소설 여러 화가 파일 하나에 통째로 들어있으면(수만~수백만 자) 한 번에 다룰 본문이 너무 커져서
/// 느려지므로, "1화" 같은 회차 제목 줄을 찾아 그 경계로 잘라 화 하나 = 챕터 하나로 만든다.
/// 다만 실제 연재 파일은 중간에 회차 제목 형식이 바뀌거나 아예 없어지는 경우가 흔해서
/// (예: 초반 40화까지는 "1화 제목"이 있다가 그 뒤로는 없어짐), 자동 모드에서는 회차 제목으로 나눈 구간이라도
/// 여전히 너무 크면 그 구간 안에서 다시 문단 경계로 잘라 크기를 보장한다.
/// 어떤 방식으로 나눌지는 설정(TxtSplitOptions)이 정한다.
/// </summary>
public static class TxtChapterSplitter
{
    public static List<TxtChapter> Split(string text, string baseName, TxtSplitOptions? options = null)
    {
        options ??= TxtSplitOptions.Default;
        int chunk = options.ChunkSize;
        // 기본 조각 크기(12000)일 때 예전 상수(자르지 않는 한계 20000 / 최대 30000)와 같아지도록 비율로 잡는다.
        int singleLimit = chunk * 5 / 3;
        int maxSize = chunk * 5 / 2;

        if (options.Mode == TxtSplitMode.None || text.Length == 0)
            return new List<TxtChapter> { new(baseName, text, 0, text.Length) };

        if (options.Mode == TxtSplitMode.Size)
        {
            var onlySize = new List<TxtChapter>();
            AddBySize(onlySize, text, 0, text.Length, baseName, chunk);
            return onlySize;
        }

        bool auto = options.Mode == TxtSplitMode.Auto;
        if (auto && text.Length <= singleLimit)
            return new List<TxtChapter> { new(baseName, text, 0, text.Length) };

        // 1) 제목 줄로 대략의 경계를 찾는다
        var regexes = options.BuildHeadingRegexes();
        var headingStarts = new List<int>();
        if (regexes.Count > 0)
        {
            int offset = 0;
            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (IsHeading(regexes, line)) headingStarts.Add(offset);
                offset += rawLine.Length + 1; // 잘려나간 '\n' 만큼 보정
            }
        }

        // 자동 모드는 우연히 맞은 줄 하나로 나누지 않도록 2개 이상일 때만, 직접 고른 제목 모드는 1개부터 믿는다.
        int minHeadings = auto ? 2 : 1;
        var roughSpans = new List<(int Start, int End, string Name)>();
        if (headingStarts.Count >= minHeadings)
        {
            if (headingStarts[0] > 0)
                roughSpans.Add((0, headingStarts[0], baseName));

            for (int i = 0; i < headingStarts.Count; i++)
            {
                int start = headingStarts[i];
                int end = i + 1 < headingStarts.Count ? headingStarts[i + 1] : text.Length;
                string heading = text[start..end].Split('\n')[0].Trim();
                if (heading.Length > 40) heading = heading[..40];
                if (heading.Length == 0) heading = $"{i + 1}화";
                roughSpans.Add((start, end, heading));
            }
        }
        else
        {
            // 제목을 못 찾음 — 자동 모드면 통째로 한 구간으로 두고 아래 2)단계에서 글자 수로 자른다.
            // 제목 모드는 사용자가 정한 대로 나눌 곳이 없으니 챕터 하나로 둔다.
            roughSpans.Add((0, text.Length, baseName));
        }

        // 2) (자동 모드만) 구간이 여전히 너무 크면 그 안에서 다시 문단 경계로 잘라 "한 번에 다룰 본문 크기"를 보장한다.
        var result = new List<TxtChapter>();
        foreach (var span in roughSpans)
        {
            if (!auto || span.End - span.Start <= maxSize)
                result.Add(new TxtChapter(span.Name, text[span.Start..span.End], span.Start, span.End));
            else
                AddBySize(result, text, span.Start, span.End, span.Name, chunk);
        }
        return result;
    }

    private static bool IsHeading(List<Regex> regexes, string line)
    {
        foreach (var re in regexes)
        {
            try { if (re.IsMatch(line)) return true; }
            catch (RegexMatchTimeoutException) { /* 너무 오래 걸리는 정규식은 이 줄을 제목이 아닌 걸로 */ }
        }
        return false;
    }

    /// <summary>[start, end) 구간을 chunk 글자 안팎으로, 가까운 빈 줄(문단 경계)에서 끊어 result에 덧붙인다.</summary>
    private static void AddBySize(List<TxtChapter> result, string text, int start, int end, string name, int chunk)
    {
        if (end - start <= chunk)
        {
            result.Add(new TxtChapter(name, text[start..end], start, end));
            return;
        }

        var subs = new List<(int Start, int End)>();
        int pos = start;
        while (pos < end)
        {
            int chunkEnd = Math.Min(pos + chunk, end);
            if (chunkEnd < end)
            {
                int nextParagraph = text.IndexOf("\n\n", chunkEnd, StringComparison.Ordinal);
                if (nextParagraph != -1 && nextParagraph < end && nextParagraph - chunkEnd < chunk / 3)
                    chunkEnd = nextParagraph;
            }
            subs.Add((pos, chunkEnd));
            pos = chunkEnd;
        }
        for (int i = 0; i < subs.Count; i++)
        {
            string subName = subs.Count > 1 ? $"{name} ({i + 1}/{subs.Count})" : name;
            result.Add(new TxtChapter(subName, text[subs[i].Start..subs[i].End], subs[i].Start, subs[i].End));
        }
    }
}
