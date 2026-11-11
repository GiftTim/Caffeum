using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>
/// Windows에 설치된 음성(SAPI)으로 문장을 하나씩 읽어주는 엔진. 문장 하나 읽기가 끝나면 SentenceFinished가
/// UI 스레드에서 발생하고, 다음 문장을 무엇으로 할지·화면에서 어떻게 표시할지는 MainForm이 정한다.
/// 온라인 음성 서비스를 쓰지 않아서 인터넷 없이도 되고 읽은 글이 밖으로 나가지 않는다.
/// </summary>
public sealed class TtsEngine : IDisposable
{
    private SpeechSynthesizer? synth;
    private Prompt? current;
    private string? appliedVoice;
    private SynchronizationContext? ui; // 필드 초기화 시점엔 아직 윈폼 컨텍스트가 없을 수 있어서, 처음 말할 때(UI 스레드) 잡는다

    public event Action? SentenceFinished;
    public string? VoiceName { get; set; }
    /// <summary>-10 ~ 10.</summary>
    public int Rate { get; set; }

    /// <summary>
    /// 고를 수 있는 음성 이름들. 한국어를 읽을 수 있는 음성만 준다 — 영어 전용 음성(Zira, David 등)은 한글 글자를 읽지
    /// 못해서 문장 부호("." 등)만 소리 내고 나머지는 건너뛰기 때문이다. 한국어 음성이 하나도 없으면 그때만 전부 보여준다.
    /// </summary>
    public static List<string> InstalledVoices()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            var all = s.GetInstalledVoices().Where(v => v.Enabled).ToList();
            var korean = all.Where(v => v.VoiceInfo.Culture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)).ToList();
            return (korean.Count > 0 ? korean : all).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return new List<string>(); }
    }

    private SpeechSynthesizer Ensure()
    {
        if (synth != null) return synth;
        ui = SynchronizationContext.Current;
        synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
        synth.SpeakCompleted += (_, e) =>
        {
            // 취소된 것(정지·새 문장으로 교체)이나 이미 지난 문장의 완료 신호는 무시한다.
            if (e.Cancelled || e.Prompt != current) return;
            var done = e.Prompt;
            ui?.Post(_ =>
            {
                if (done != current) return;
                current = null;
                SentenceFinished?.Invoke();
            }, null);
        };
        return synth;
    }

    public void Speak(string text)
    {
        var s = Ensure();
        s.SpeakAsyncCancelAll();
        try
        {
            // 저장된 음성이 목록에 없으면(예전에 영어 음성을 골라둔 경우, 삭제된 음성) 한국어 음성으로 대신 읽는다.
            var allowed = InstalledVoices();
            string? voice = !string.IsNullOrEmpty(VoiceName) && allowed.Contains(VoiceName) ? VoiceName : allowed.FirstOrDefault();
            if (voice != null && voice != appliedVoice) { s.SelectVoice(voice); appliedVoice = voice; }
        }
        catch { /* 없어진 음성이면 기본 음성으로 그냥 읽는다 */ }
        s.Rate = Math.Clamp(Rate, -10, 10);
        current = s.SpeakAsync(text);
    }

    public void Stop()
    {
        current = null;
        synth?.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        current = null;
        synth?.Dispose();
        synth = null;
    }
}

/// <summary>낭독할 글을 문장으로 나누고, 읽지 말아야 할 기호를 걸러낸다.</summary>
public static class TtsText
{
    private const int MaxSentence = 300; // 마침표 없이 길게 이어지는 글도 이 길이쯤에서는 끊어 읽는다

    // 장식용 기호(★ ◆ ※ 등)는 그대로 읽으면 "별표 별표"처럼 들려서 공백으로 바꾼다.
    private static readonly Regex Symbols = new(
        @"[←-⇿■-◿☀-➿⬀-⯿※＊*#~`^|\\<>{}\[\]_=]",
        RegexOptions.Compiled);
    private static readonly Regex Ellipsis = new(@"\.{2,}|…+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string s)
    {
        s = Symbols.Replace(s, " ");
        s = Ellipsis.Replace(s, ", ");
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>글자나 숫자가 하나도 없으면(구분선, "* * *" 등) 읽을 게 없다.</summary>
    public static bool HasSpeakable(string cleaned) => cleaned.Any(char.IsLetterOrDigit);

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '…';
    private static bool IsTrailingQuote(char c) => c is '.' or '!' or '?' or '…' or '"' or '\'' or '”' or '’' or '」' or '』' or ')' or '）' or ']' or '】';

    /// <summary>
    /// text의 pos부터 다음 한 문장의 (시작, 길이)를 찾는다. limit(글 끝 또는 지금 페이지 끝)을 넘지 않는다.
    /// 문장 끝은 종결 부호 뒤 공백이나 줄바꿈, 또는 줄바꿈 자체다("3.14"의 점은 뒤에 공백이 없으니 끝이 아니다).
    /// 글 전체를 미리 쪼개지 않고 한 문장씩 그때그때 찾아서, 수십 MB짜리 txt도 바로 낭독을 시작할 수 있다.
    /// </summary>
    public static (int Start, int Length)? NextSentence(string text, int pos, int limit)
    {
        limit = Math.Min(limit, text.Length);
        while (pos < limit && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= limit) return null;

        int hardEnd = Math.Min(limit, pos + MaxSentence);
        int i = pos, end = -1;
        while (i < hardEnd)
        {
            char c = text[i];
            if (c == '\n' || c == '\r') { end = i; break; }
            if (IsTerminator(c))
            {
                int j = i + 1;
                while (j < limit && IsTrailingQuote(text[j])) j++;
                if (j >= limit || char.IsWhiteSpace(text[j])) { end = j; break; }
                i = j;
                continue;
            }
            i++;
        }

        if (end < 0)
        {
            end = hardEnd;
            if (hardEnd < limit)
            {
                // 길이 제한에 걸렸으면 단어 중간이 아니라 가까운 공백·쉼표에서 끊는다.
                int cut = text.LastIndexOfAny(new[] { ' ', ',', '，', '、' }, hardEnd - 1, hardEnd - pos);
                if (cut > pos + MaxSentence / 3) end = cut + 1;
            }
        }

        int len = end - pos;
        while (len > 0 && char.IsWhiteSpace(text[pos + len - 1])) len--;
        return len > 0 ? (pos, len) : null;
    }
}
