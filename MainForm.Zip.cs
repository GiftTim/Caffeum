using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CoffeeViewer;

/// <summary>
/// zip 열기. 안에 든 내용을 보고 소설이면 소설로, 그림이면 만화로 연다(나만의 소설뷰어와 같은 방식).
/// - 그림이 훨씬 많음 → 만화(기존 이미지 뷰어)
/// - txt가 있음 → 이름 순서(2화 → 10화)로 이어 붙여 한 권의 글로 본다. 챕터 목록에는 파일별로 나온다.
/// - epub / pdf가 있음 → 임시 폴더에 꺼내서 그 뷰어로 연다.
/// </summary>
public partial class MainForm
{
    /// <summary>지금 화면의 글이 zip 안에서 꺼낸 것인지. 그러면 저장이 zip을 덮어쓰지 않게 항상 "다른 이름으로"만 된다.</summary>
    private bool txtFromZip;

    private enum ZipKind { Comic, NovelTxt, NovelEpub, NovelPdf }

    private void LoadZip(string path)
    {
        ZipKind kind;
        try { kind = ClassifyZip(path); }
        catch (Exception ex)
        {
            ShowZipOpenError(path, ex);
            return;
        }

        switch (kind)
        {
            case ZipKind.NovelTxt: LoadZipNovel(path); break;
            case ZipKind.NovelEpub: OpenFromZip(path, ".epub"); break;
            case ZipKind.NovelPdf: OpenFromZip(path, ".pdf"); break;
            default: LoadZipImages(path); break; // 그림뿐이거나 화별 zip이 든 묶음 zip — 예전 그대로
        }
    }

    /// <summary>zip 열기 실패를 알린다. OneDrive "온라인 전용" 파일을 내려받지 못해서 실패한 경우엔
    /// zip 라이브러리가 엉뚱하게 "Central Directory corrupt"(zip 손상)라고 하므로, 진짜 이유를 따로 알려준다
    /// — 실제로 C: 여유 공간이 0.2GB라 OneDrive가 파일을 못 내려받았는데 zip이 깨진 줄 알았던 적이 있다.</summary>
    private void ShowZipOpenError(string path, Exception ex, string title = "zip을 여는 데 실패했습니다")
    {
        string message = CloudFileProblem(path) ?? $"{title}: {ex.Message}";
        MessageBox.Show(this, message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>클라우드(OneDrive 등) 자리표시 파일이고 실제로 읽을 수 없으면 사용자용 설명을, 아니면 null.</summary>
    private static string? CloudFileProblem(string path)
    {
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000; // 열면 클라우드에서 내려받는 "온라인 전용" 파일
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & (RecallOnDataAccess | FileAttributes.Offline)) == 0) return null;
            using var fs = File.OpenRead(path);
            fs.ReadByte(); // 읽히면 클라우드 문제가 아니다 — 원래 오류를 그대로 보여준다
            return null;
        }
        catch (IOException io)
        {
            string free = "";
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                free = $"\n{drive.Name.TrimEnd('\\')} 드라이브 여유 공간: {drive.AvailableFreeSpace / (1024.0 * 1024 * 1024):0.##} GB";
            }
            catch { }
            return "OneDrive에서 이 파일을 내려받지 못해서 열 수 없습니다.\n" +
                   $"(파일이 PC에 없고 클라우드에만 있는 상태입니다: {io.Message})\n" +
                   free + "\n\n" +
                   "여유 공간이 부족하면 다 읽은 폴더를 탐색기에서 오른쪽 클릭 → '공간 확보'로 비운 뒤 다시 열어 보세요.\n" +
                   "OneDrive가 실행 중인지, 인터넷에 연결돼 있는지도 확인해 주세요.";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsIgnoredZipEntry(ZipArchiveEntry e) =>
        string.IsNullOrEmpty(e.Name) || e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("._");

    /// <summary>zip의 파일 목록만 보고(내용은 안 풀고) 소설인지 만화인지 정한다.</summary>
    private static ZipKind ClassifyZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        int images = 0, txt = 0, epub = 0, pdf = 0;
        foreach (var e in zip.Entries)
        {
            if (IsIgnoredZipEntry(e)) continue;
            string ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (ZipImageSet.IsImageExtension(e.Name)) images++;
            else if (ext == ".txt") txt++;
            else if (ext == ".epub") epub++;
            else if (ext == ".pdf") pdf++;
        }

        int docs = txt + epub + pdf;
        if (docs == 0) return ZipKind.Comic;
        // 그림이 글 파일 수보다 훨씬 많으면 만화(만화 zip에 안내문 txt 하나 든 경우). 소설 zip에 표지·삽화가 몇 장 든 경우는 소설로 본다.
        if (images > Math.Max(3, docs)) return ZipKind.Comic;
        return txt > 0 ? ZipKind.NovelTxt : epub > 0 ? ZipKind.NovelEpub : ZipKind.NovelPdf;
    }

    /// <summary>zip 안의 txt들을 (이름 순서대로) 이어 붙여 하나의 글로 연다.</summary>
    private async void LoadZipNovel(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("압축 파일을 여는 중…");
        string text;
        List<TxtChapter> prepared;
        try
        {
            var progress = new Progress<double>(loadingOverlay.Report);
            var splitOptions = TxtSplitOptions.From(settings); // 백그라운드로 넘어가기 전에 값만 복사해 둔다
            (text, prepared) = await Task.Run(() => ReadZipNovel(path, progress, cts.Token, splitOptions));
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
            return;
        }
        catch (Exception ex)
        {
            ShowZipOpenError(path, ex);
            return;
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }

        if (text.Length == 0)
        {
            MessageBox.Show(this, "이 zip 안에서 읽을 글을 찾지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        ShowLoadedText(path, text, prepared, fromZip: true);
    }

    private static (string Text, List<TxtChapter> Chapters) ReadZipNovel(string path, IProgress<double> progress, CancellationToken ct, TxtSplitOptions options)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries
            .Where(e => !IsIgnoredZipEntry(e) && string.Equals(Path.GetExtension(e.Name), ".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
            .ToList();

        var sb = new StringBuilder();
        var chapters = new List<TxtChapter>();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            using (var s = entries[i].Open()) s.CopyTo(ms);

            // 줄바꿈을 \r\n으로 통일한 뒤 챕터를 나눠야, 이어 붙인 전체 글 기준의 위치가 맞는다.
            string t = DecodeText(ms.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            if (t.Trim().Length == 0) continue;

            if (sb.Length > 0) sb.Append("\r\n\r\n");
            int offset = sb.Length;
            // 파일 하나가 짧으면 파일 이름이 챕터 이름이 되고, 소설 한 편이 통째로 든 큰 파일이면 "N화" 기준으로 다시 나뉜다.
            foreach (var c in TxtChapterSplitter.Split(t, Path.GetFileNameWithoutExtension(entries[i].Name), options))
                chapters.Add(new TxtChapter(c.Name, c.Text, c.Start + offset, c.End + offset));
            sb.Append(t);
            progress.Report((i + 1.0) / entries.Count);
        }
        return (sb.ToString(), chapters);
    }

    /// <summary>
    /// 글 파일의 인코딩을 알아맞혀 읽는다. BOM이 있으면 그대로, 없으면 UTF-8로 먼저 읽어보고 깨지면 한글 옛 방식(CP949)으로 읽는다.
    /// 오래된 한국 소설 txt는 CP949가 흔한데, UTF-8로만 읽으면 글자가 전부 깨져 보이기 때문이다.
    /// </summary>
    internal static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
        catch (DecoderFallbackException) { return CodePagesEncodingProvider.Instance.GetEncoding(949)!.GetString(b); }
    }

    /// <summary>zip 안의 epub/pdf 하나(이름 순서로 첫 번째)를 임시 폴더에 꺼내 그 뷰어로 연다.</summary>
    private void OpenFromZip(string zipPath, string ext)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries
                .Where(e => !IsIgnoredZipEntry(e) && string.Equals(Path.GetExtension(e.Name), ext, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
                .First();

            // 같은 zip이면 항상 같은 폴더를 써서(다시 열 때 읽던 자리가 이어지게), zip 경로로 폴더 이름을 정한다.
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(zipPath.ToLowerInvariant())))[..12];
            string dir = Path.Combine(Path.GetTempPath(), "CoffeeViewerZip", hash);
            Directory.CreateDirectory(dir);
            // 이름만 쓴다(Path.GetFileName) — zip 안 경로에 ../ 가 있어도 임시 폴더 밖으로 나갈 수 없게.
            string target = Path.Combine(dir, Path.GetFileName(entry.Name));
            entry.ExtractToFile(target, overwrite: true);
            OpenPath(target);
        }
        catch (Exception ex)
        {
            ShowZipOpenError(zipPath, ex, "zip 안의 파일을 여는 데 실패했습니다");
        }
    }
}
