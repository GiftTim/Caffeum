using System.IO.Compression;

namespace CoffeeViewer;

/// <summary>zip 안이든 폴더 안이든, "이미지 여러 장을 순서대로 넘겨볼 수 있는 것"을 똑같이 다루기 위한 인터페이스.</summary>
public interface IImageSet : IDisposable
{
    int Count { get; }
    string Name(int index);

    /// <summary>파일 그대로의 바이트만 읽어온다(디코딩은 안 함). zip 구현체는 내부 공유 스트림을 건드리므로
    /// 호출하는 쪽에서 한 번에 하나씩만 부르도록 락으로 감싸야 한다. 디코딩(무거운 CPU 작업)은 이 메서드가
    /// 끝난 뒤 락 밖에서 해야 여러 페이지를 동시에(병렬로) 미리 불러올 수 있다.</summary>
    byte[] LoadRawBytes(int index);

    /// <summary>바이트 추출 + 디코딩까지 한 번에. 락으로 감쌀 필요 없이 간단히 한 장만 볼 때 쓴다.</summary>
    Image Load(int index) => ImageLoader.Decode(LoadRawBytes(index), Name(index));
}

/// <summary>zip 파일 안의 이미지들(만화/웹툰 zip). 항목을 이미지 확장자만 걸러서 자연순으로 정렬해둔다.</summary>
public sealed class ZipImageSet : IImageSet
{
    private readonly ZipArchive zip;
    private readonly List<ZipArchiveEntry> entries;

    public ZipImageSet(string zipPath)
    {
        zip = ZipFile.OpenRead(zipPath);
        entries = zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageExtension(e.Name))
            .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
            .ToList();
    }

    public int Count => entries.Count;
    public string Name(int index) => Path.GetFileName(entries[index].FullName);

    public byte[] LoadRawBytes(int index)
    {
        using var s = entries[index].Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => zip.Dispose();

    public static bool IsImageExtension(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp";
    }
}

/// <summary>
/// "화별로 zip이 하나씩 들어있는 zip"(예: 1화.zip, 2화.zip, ... 들이 들어있는 묶음 zip) 전용.
/// 안쪽 zip들을 전부 미리 열어서, 그 안 이미지 전부를 "화 이름 - 파일명" 하나의 이어진 목록으로 펼쳐 보여준다.
/// 이렇게 하면 화 경계를 따로 챙기지 않아도, 페이지 넘기듯 쭉 넘기다 보면 자연스럽게 다음 화로 넘어간다.
/// </summary>
public sealed class NestedZipImageSet : IImageSet
{
    private readonly List<(ZipArchive innerZip, ZipArchiveEntry entry, string displayName)> pages = new();

    /// <summary>
    /// progress: 화(내부 zip) 하나를 다 풀 때마다 0.0~1.0 누적 진행률을 보고한다(안쪽 zip들을 통째로
    /// 메모리에 복사하는 게 이 클래스에서 시간이 걸리는 유일한 부분이라, 그 바이트 기준으로 잰다).
    /// 큰 묶음 zip(수백 MB~)을 열 때 화면에 게이지를 보여주려고 둔 것이라, 안 넘겨도 동작엔 지장 없다.
    /// cancellationToken: 화 하나를 다 풀 때마다 확인한다 — 취소되면 그때까지 열어둔 안쪽 zip들을
    /// 정리(Dispose)하고 OperationCanceledException을 던진다.
    /// </summary>
    public NestedZipImageSet(string outerZipPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var outerZip = ZipFile.OpenRead(outerZipPath);
        var episodeEntries = outerZip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
            .ToList();

        long totalBytes = episodeEntries.Sum(e => e.Length); // 압축 해제 크기 기준 — 대략치로 충분하다
        long doneBytes = 0;

        try
        {
            foreach (var episodeEntry in episodeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string episodeName = Path.GetFileNameWithoutExtension(episodeEntry.Name);
                using var es = episodeEntry.Open();
                var ms = new MemoryStream(); // innerZip이 이 스트림을 계속 붙들고 있어야 해서 여기선 안 닫는다(Dispose에서 정리).
                es.CopyTo(ms);
                ms.Position = 0;
                var innerZip = new ZipArchive(ms, ZipArchiveMode.Read);
                var imageEntries = innerZip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) && ZipImageSet.IsImageExtension(e.Name))
                    .OrderBy(e => e.FullName, NaturalStringComparer.Instance);
                foreach (var img in imageEntries)
                    pages.Add((innerZip, img, $"{episodeName} - {Path.GetFileName(img.FullName)}"));

                doneBytes += episodeEntry.Length;
                if (totalBytes > 0) progress?.Report((double)doneBytes / totalBytes);
            }
        }
        catch
        {
            Dispose(); // 취소/실패 시 그때까지 열어둔 안쪽 zip들이 새도록 두지 않는다
            throw;
        }
    }

    public int Count => pages.Count;
    public string Name(int index) => pages[index].displayName;

    public byte[] LoadRawBytes(int index)
    {
        using var s = pages[index].entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        var closed = new HashSet<ZipArchive>();
        foreach (var (zip, _, _) in pages)
            if (closed.Add(zip)) zip.Dispose();
    }

    /// <summary>zip 안에 이미지가 직접 없고 .zip 항목만 있으면(화별로 나뉜 묶음 zip이면) true.</summary>
    public static bool LooksNested(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        bool hasDirectImages = zip.Entries.Any(e => !string.IsNullOrEmpty(e.Name) && ZipImageSet.IsImageExtension(e.Name));
        if (hasDirectImages) return false;
        return zip.Entries.Any(e => !string.IsNullOrEmpty(e.Name) && e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>zip이 아니라 이미지 파일 하나를 직접 열었을 때: 같은 폴더 안의 이미지들을 모아 한 묶음으로 넘겨본다.</summary>
public sealed class FolderImageSet : IImageSet
{
    private readonly List<string> files;

    private FolderImageSet(List<string> files) => this.files = files;

    public static FolderImageSet FromDirectory(string dir)
    {
        var files = Directory.EnumerateFiles(dir)
            .Where(ZipImageSet.IsImageExtension)
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();
        return new FolderImageSet(files);
    }

    public int IndexOfPath(string path) =>
        files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    public int Count => files.Count;
    public string Name(int index) => Path.GetFileName(files[index]);

    public byte[] LoadRawBytes(int index) => File.ReadAllBytes(files[index]);

    public void Dispose() { }
}
