using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>서재에 등록된 책 한 권. 읽던 위치와 마지막으로 연 시각도 여기에 같이 든다.</summary>
public sealed class BookRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime? LastOpened { get; set; }
    /// <summary>0~1. 서재 목록의 "진행" 칸에 보여준다.</summary>
    public double Progress { get; set; }
    /// <summary>txt: 글자 위치 / epub: 챕터 번호 / 만화 zip: 페이지 번호.</summary>
    public int PosA { get; set; }
    /// <summary>epub: 챕터 안에서의 위치(‰).</summary>
    public int PosB { get; set; }
    /// <summary>서재 목록의 "즐겨찾기" 칸(★). 진행도 초기화를 해도 그대로 남는다.</summary>
    public bool Favorite { get; set; }
    // 파일별 서식(txt · epub). null이면 설정창의 기본값을 따른다.
    public string? LookFont { get; set; }
    /// <summary>영어(라틴) 글자에만 쓸 글꼴. null이면 LookFont를 따른다.</summary>
    public string? LookFontLatin { get; set; }
    public int? LookSize { get; set; }
    public int? LookLine { get; set; }
    public string? LookAlign { get; set; }
    /// <summary>글자 서식(이 파일 전체): 진하게 · 기울임 · 밑줄 · 취소선 · 글자색(#RRGGBB).</summary>
    public bool? LookBold { get; set; }
    public bool? LookItalic { get; set; }
    public bool? LookUnderline { get; set; }
    public bool? LookStrike { get; set; }
    public string? LookColor { get; set; }
    /// <summary>문단 서식(여백 · 첫 줄 · 문단 간격 · 줄 나눔 기준 …). null이면 기본.</summary>
    public ParaFormat? Para { get; set; }
    public bool? LookWrap { get; set; }
    public PaperSetup? Paper { get; set; }
    /// <summary>화면 배율(%). null이면 100.</summary>
    public int? LookZoom { get; set; }
    /// <summary>파일의 수정한 날짜(서재 목록의 "수정일" 칸). 스캔 · 추가할 때 채운다 — 예전 기록에는 없을 수 있다.</summary>
    public DateTime? Modified { get; set; }

    [JsonIgnore] public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    [JsonIgnore] public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>
/// 서재(등록한 폴더들 + 그 안의 책 목록 + 읽던 위치). %LOCALAPPDATA%\CoffeeViewer\library.json 한 파일에 담는다.
/// 소설 몇 천 권 규모면 JSON 하나로 충분해서 DB 엔진(SQLite)은 쓰지 않았다 — 네이티브 DLL이 하나 더 늘면
/// 단일 exe 배포(csproj 주석 참고)에서 또 "DLL was not found"를 겪을 수 있어서다.
/// UI 스레드에서만 만지는 걸 전제로 한다(스캔의 파일 열거만 백그라운드에서 돌고, 결과 병합은 UI 스레드).
/// </summary>
public sealed class LibraryStore
{
    public static readonly string[] BookExtensions = { ".txt", ".epub", ".pdf", ".zip", ".hwp", ".hwpx" };

    private sealed class Data
    {
        public List<string> Roots { get; set; } = new();
        public List<BookRecord> Books { get; set; } = new();
    }

    private readonly Dictionary<string, BookRecord> books = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    public List<string> Roots { get; } = new();
    public IEnumerable<BookRecord> Books => books.Values;
    public int Count => books.Count;

    private static string FilePath => System.IO.Path.Combine(AppSettings.DataDir, "library.json");

    public static LibraryStore Load()
    {
        var store = new LibraryStore();
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), AppSettings.JsonOptions);
                if (data != null)
                {
                    store.Roots.AddRange(data.Roots);
                    foreach (var b in data.Books)
                        if (!string.IsNullOrEmpty(b.Path)) store.books[b.Path] = b;
                }
            }
        }
        catch { /* 깨진 서재 파일은 빈 서재로 시작 */ }
        return store;
    }

    public void SaveIfDirty()
    {
        if (!dirty) return;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var data = new Data { Roots = Roots.ToList(), Books = books.Values.ToList() };
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, AppSettings.JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            dirty = false;
        }
        catch { /* 다음 기회에 다시 시도 */ }
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    public void MarkDirty() => dirty = true;

    public BookRecord? Find(string path) => books.TryGetValue(Normalize(path), out var b) ? b : null;

    /// <summary>파일을 열 때: 서재에 없으면 새로 만들고, 마지막으로 연 시각을 갱신한다(읽던 위치는 그대로).</summary>
    public BookRecord Touch(string path)
    {
        path = Normalize(path);
        if (!books.TryGetValue(path, out var rec))
        {
            rec = new BookRecord { Path = path };
            books[path] = rec;
        }
        try { rec.Size = new FileInfo(path).Length; } catch { }
        rec.LastOpened = DateTime.Now;
        dirty = true;
        return rec;
    }

    public void SavePosition(string path, int posA, int posB, double progress)
    {
        var rec = Find(path);
        if (rec == null) return;
        progress = Math.Clamp(progress, 0, 1);
        if (rec.PosA == posA && rec.PosB == posB && Math.Abs(rec.Progress - progress) < 0.0005) return; // 안 바뀌었으면 디스크도 안 건드린다
        rec.PosA = posA;
        rec.PosB = posB;
        rec.Progress = progress;
        dirty = true;
    }

    /// <summary>읽던 위치·진행률·마지막으로 연 시각을 지워 "안 읽은 책"으로 되돌린다(서재 목록에는 그대로 남는다).</summary>
    public void ResetProgress(string path)
    {
        var rec = Find(path);
        if (rec == null) return;
        rec.Progress = 0;
        rec.PosA = 0;
        rec.PosB = 0;
        rec.LastOpened = null;
        dirty = true;
    }

    public void SetFavorite(string path, bool favorite)
    {
        var rec = Find(path);
        if (rec == null || rec.Favorite == favorite) return;
        rec.Favorite = favorite;
        dirty = true;
    }

    public IEnumerable<BookRecord> Recent(int count) =>
        books.Values.Where(b => b.LastOpened != null).OrderByDescending(b => b.LastOpened).Take(count);

    public void Remove(string path)
    {
        if (books.Remove(Normalize(path))) dirty = true;
    }

    /// <summary>서재를 통째로 비운다(책 목록·읽던 기록·등록 폴더 모두). 파일은 그대로다.</summary>
    public void Clear()
    {
        if (books.Count == 0 && Roots.Count == 0) return;
        books.Clear();
        Roots.Clear();
        dirty = true;
    }

    /// <summary>폴더 없이 책 파일 하나만 서재에 넣는다. 이미 있거나 지원하지 않는 형식이면 false.</summary>
    public bool AddBook(string path)
    {
        path = Normalize(path);
        if (!File.Exists(path) || !BookExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) return false;
        if (books.ContainsKey(path)) return false;
        long size = 0;
        DateTime? modified = null;
        try { var fi = new FileInfo(path); size = fi.Length; modified = fi.LastWriteTime; } catch { }
        books[path] = new BookRecord { Path = path, Size = size, Modified = modified };
        dirty = true;
        return true;
    }

    public bool AddRoot(string dir)
    {
        dir = Normalize(dir).TrimEnd('\\');
        if (Roots.Any(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase))) return false;
        Roots.Add(dir);
        dirty = true;
        return true;
    }

    /// <summary>폴더 등록을 해제한다. 그 폴더 밑에서 읽은 기록이 없는 책은 목록에서도 뺀다.</summary>
    public void RemoveRoot(string dir)
    {
        Roots.RemoveAll(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase));
        foreach (var b in books.Values.Where(b => IsUnder(b.Path, dir) && b.LastOpened == null && !b.Favorite).ToList())
            books.Remove(b.Path);
        dirty = true;
    }

    /// <summary>경로가 놓인 드라이브(또는 네트워크 공유)가 지금 연결돼 있는지.</summary>
    private static bool DriveAvailable(string path)
    {
        string? top = System.IO.Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(top) && Directory.Exists(top);
    }

    public static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>등록된 폴더들을 뒤져 새 책을 목록에 넣고, 사라진 파일은 뺀다. 새로 들어온 권수를 돌려준다.</summary>
    public async Task<int> ScanAsync()
    {
        var roots = Roots.ToList();
        var found = await Task.Run(() =>
        {
            var list = new List<(string Path, long Size, DateTime Modified)>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
                    if (BookExtensions.Contains(f.Extension.ToLowerInvariant()))
                        list.Add((f.FullName, f.Length, f.LastWriteTime));
            }
            return list;
        });

        int added = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, size, modified) in found)
        {
            seen.Add(path);
            if (books.TryGetValue(path, out var existing)) { existing.Size = size; existing.Modified = modified; continue; }
            books[path] = new BookRecord { Path = path, Size = size, Modified = modified };
            added++;
        }

        // 등록 폴더 밑에 있었는데 이제 없는 파일은 지운다.
        // 폴더 자체가 사라졌더라도 드라이브(공유)는 살아 있으면 폴더를 지운 것이니 책 목록과 등록을 함께 정리하고,
        // 드라이브가 빠졌거나 공유에 못 닿는 경우일 수 있으니 그때는 건드리지 않는다.
        foreach (var root in roots)
        {
            bool exists = Directory.Exists(root);
            if (!exists && !DriveAvailable(root)) continue;
            foreach (var gone in books.Values.Where(b => IsUnder(b.Path, root) && (!exists || !seen.Contains(b.Path))).ToList())
                books.Remove(gone.Path);
            if (!exists) Roots.RemoveAll(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        }

        // 등록 폴더 밖에서 직접 연 책(더블클릭·우클릭으로 연 것)은 위에서 안 걸리므로 파일이 있는지 하나씩 본다.
        // 드라이브가 빠진 경우는 위와 같은 이유로 건드리지 않는다. 느린 네트워크 경로가 있을 수 있어 백그라운드에서 확인한다.
        var outside = books.Keys.Where(p => !roots.Any(r => IsUnder(p, r))).ToList();
        var missing = await Task.Run(() => outside.Where(p => !File.Exists(p) && DriveAvailable(p)).ToList());
        foreach (var p in missing) books.Remove(p);

        dirty = true;
        SaveIfDirty();
        return added;
    }

    private static readonly Regex BracketsRe = new(@"[\[\(【（][^\]\)】）]*[\]\)】）]", RegexOptions.Compiled);
    private static readonly Regex AuthorTagRe = new(@"@.*$", RegexOptions.Compiled); // "@권경목"처럼 @ 뒤로 이어지는 올린 사람 표시
    // 회차 범위: "1-442", "001~203", "1-24권", "01권-15권" (뒤에 붙는 "5화" 같은 것은 아래 EpisodeRe가 걷어낸다)
    private static readonly Regex RangeRe = new(@"\d+\s*(?:화|권|부|회)?\s*[~\-]\s*\d+\s*(?:화|권|부|회)?", RegexOptions.Compiled);
    // 단독 회차·권 표시: "001화", "3권", "08.5권" (제목 속 숫자 "100층", "31번째"는 단위가 달라서 그대로 둔다)
    private static readonly Regex EpisodeRe = new(@"(?:제\s*)?\d+(?:\.\d+)?\s*(?:화|권|부|회)(?![\p{L}\p{N}])", RegexOptions.Compiled);
    // 단위 없이 "숫자 ~부제"로 이어지는 권 표시: "스즈미야 하루히의 01 ~우울" → 숫자와 부제를 함께 걷어낸다.
    private static readonly Regex VolumeSubtitleRe = new(@"(?<![\p{L}\p{N}.])\d+(?:\.\d+)?\s*[~～].*$", RegexOptions.Compiled);
    // 완결 여부·판본 표시. 앞뒤가 글자·숫자가 아닐 때만(제목 속 "완전"의 "완" 같은 걸 지우지 않게).
    private static readonly Regex StatusRe = new(
        @"(?<![\p{L}\p{N}])(?:미완결|완결|미완|未完|完|완|연중|스캔본|외전|본편|후기|합본)(?![\p{L}\p{N}])", RegexOptions.Compiled);

    /// <summary>
    /// 파일 이름에서 "책 제목"만 남긴다. 괄호 안 표시((完), (스캔본)), @올린이, 회차 범위(1-442), 완결·외전 같은
    /// 표시를 걷어낸 나머지다. 같은 소설을 txt·스캔본 zip·epub으로 따로 갖고 있어도 같은 이름이 되게 하려는 것.
    /// 서재의 시리즈 머리글에 그대로 보여준다.
    /// </summary>
    public static string SeriesName(string title)
    {
        string s = BracketsRe.Replace(title, " ");
        s = AuthorTagRe.Replace(s, "");
        s = RangeRe.Replace(s, " ");
        s = EpisodeRe.Replace(s, " ");
        s = VolumeSubtitleRe.Replace(s, " ");
        s = StatusRe.Replace(s, " ");
        s = Regex.Replace(s, @"[\s_]+", " ").Trim(' ', '-', '+', '.', ',', '~');
        return s;
    }

    /// <summary>
    /// 같은 책(시리즈)을 알아보는 열쇠. SeriesName에서 글자와 숫자만 남기고 소문자로 맞춘다 — 띄어쓰기가 달라도
    /// ("죽지않는 엑스트라" / "죽지 않는 엑스트라") 같은 책으로 본다. 남는 게 두 글자 미만이면 시리즈로 안 본다.
    /// </summary>
    public static string SeriesKey(string title)
    {
        string k = Regex.Replace(SeriesName(title), @"[^\p{L}\p{N}]", "").ToLowerInvariant();
        return k.Length >= 2 ? k : "";
    }
}
