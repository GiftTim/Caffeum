using System.IO.Compression;
using System.Xml.Linq;

namespace CoffeeViewer;

public sealed record EpubChapter(string Name, string FilePath);

/// <summary>
/// epub(zip 안에 OPF+XHTML로 이루어진 전자책) 파일을 열어 챕터 목록을 뽑아낸다.
/// epub은 zip이므로 통째로 임시 폴더에 풀어두고, 그 안의 XHTML 파일들을 WebView2로 그대로 보여준다
/// (이미지·CSS 같은 상대 경로 참조가 실제 디스크 파일 경로로 자연스럽게 풀리게 하려는 것).
/// </summary>
public sealed class EpubBook
{
    public string Title { get; init; } = "";
    public List<EpubChapter> Chapters { get; init; } = new();

    /// <summary>progress: 압축 해제 중 바이트 기준 누적 진행률(0.0~1.0). 이미지가 많은 큰 epub을 열 때
    /// 화면에 게이지를 보여주려고 둔 것이라, 안 넘겨도 동작엔 지장 없다.
    /// cancellationToken: 취소되면 풀다 만 임시 폴더를 지우고 OperationCanceledException을 던진다.</summary>
    public static EpubBook Load(string epubPath, string extractDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        try
        {
            ExtractWithProgress(epubPath, extractDir, progress, cancellationToken);
        }
        catch
        {
            try { Directory.Delete(extractDir, true); } catch { /* 정리 실패는 무시 — 다음에 열 때 어차피 지우고 다시 만든다 */ }
            throw;
        }

        // 1) META-INF/container.xml에서 실제 OPF(패키지 문서) 경로를 찾는다.
        string containerPath = Path.Combine(extractDir, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("epub 형식이 아닌 것 같습니다(META-INF/container.xml 없음).");

        XNamespace cns = "urn:oasis:names:tc:opendocument:xmlns:container";
        var containerDoc = XDocument.Load(containerPath);
        string? opfRelPath = containerDoc.Descendants(cns + "rootfile").FirstOrDefault()?.Attribute("full-path")?.Value;
        if (string.IsNullOrEmpty(opfRelPath))
            throw new InvalidDataException("container.xml에서 OPF 경로를 찾을 수 없습니다.");

        string opfPath = Path.Combine(extractDir, opfRelPath.Replace('/', Path.DirectorySeparatorChar));
        string opfDir = Path.GetDirectoryName(opfPath) ?? extractDir;

        // 2) OPF: manifest(파일 id -> 실제 경로)와 spine(읽는 순서)을 읽는다.
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var opfDoc = XDocument.Load(opfPath);

        string title = opfDoc.Descendants(dc + "title").FirstOrDefault()?.Value?.Trim()
            ?? Path.GetFileNameWithoutExtension(epubPath);

        var manifest = new Dictionary<string, string>();
        foreach (var item in opfDoc.Descendants(opf + "item"))
        {
            string? id = item.Attribute("id")?.Value;
            string? href = item.Attribute("href")?.Value;
            if (id == null || href == null) continue;
            manifest[id] = Path.Combine(opfDir, Uri.UnescapeDataString(href).Replace('/', Path.DirectorySeparatorChar));
        }

        var chapters = new List<EpubChapter>();
        int idx = 1;
        foreach (var itemref in opfDoc.Descendants(opf + "itemref"))
        {
            string? idref = itemref.Attribute("idref")?.Value;
            if (idref == null || !manifest.TryGetValue(idref, out var path) || !File.Exists(path)) continue;
            chapters.Add(new EpubChapter(TryGetHtmlTitle(path) ?? $"{idx}장", path));
            idx++;
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("이 epub에서 챕터를 하나도 찾지 못했습니다.");

        return new EpubBook { Title = title, Chapters = chapters };
    }

    /// <summary>
    /// ZipFile.ExtractToDirectory와 동작은 같지만(경로 탈출 방지 포함), 항목을 하나씩 풀면서 바이트
    /// 기준 진행률을 보고할 수 있게 직접 구현했다. 이미지가 많이 든 큰 epub은 이 단계가 시간이 걸린다.
    /// </summary>
    private static void ExtractWithProgress(string zipPath, string destDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.ToList();
        long totalBytes = entries.Sum(e => e.Length);
        long doneBytes = 0;
        string destRoot = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("잘못된 경로가 포함된 epub입니다."); // zip slip 방지

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath); // 디렉터리 항목
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }

            doneBytes += entry.Length;
            if (totalBytes > 0) progress?.Report((double)doneBytes / totalBytes);
        }
    }

    /// <summary>챕터 XHTML의 &lt;title&gt;을 읽어 목록에 조금 더 알아보기 쉬운 이름을 쓴다(실패하면 그냥 null).</summary>
    private static string? TryGetHtmlTitle(string htmlPath)
    {
        try
        {
            var doc = XDocument.Load(htmlPath);
            var t = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch
        {
            return null;
        }
    }
}
