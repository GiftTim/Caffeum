using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CoffeeViewer;

/// <summary>
/// 업데이트 확인: GitHub 저장소의 최신 릴리스(Releases)를 보고, 지금보다 새 버전이면 설치 파일(Setup.exe)을 내려받아
/// 조용히 설치한다(설치가 끝나면 Caffeum이 다시 열린다).
/// 새 버전을 낼 때: 저장소에 v1.0.2 같은 태그로 릴리스를 만들고 Install\CafeumOffice-Setup-1.0.2.exe를 첨부한다.
/// </summary>
internal static class Updater
{
    /// <summary>"소유자/저장소" 형태(예: "계정/CafeumOffice"). 비어 있으면 업데이트 확인은 안내만 한다.</summary>
    public const string RepoSlug = "GiftTim/Caffeum";

    private static bool busy;

    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Caffeum-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private sealed record Release(Version Version, string Tag, string AssetUrl, string AssetName, long Size, string Notes);

    public static async Task CheckAsync(IWin32Window? owner)
    {
        if (busy) return;
        busy = true;
        try
        {
            if (RepoSlug.Length == 0)
            {
                MessageBox.Show(owner, "업데이트를 받아 올 주소(GitHub 저장소)가 아직 정해지지 않았어요.", "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Release? rel;
            try { rel = await FetchLatestAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "업데이트 정보를 가져오지 못했어요. 인터넷 연결을 확인해 주세요.\n\n" + ex.Message, "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (rel == null || rel.Version <= Current)
            {
                MessageBox.Show(owner, $"이미 최신 버전이에요. (v{Format(Current)})", "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string notes = rel.Notes.Length > 0 ? "\n\n" + (rel.Notes.Length > 400 ? rel.Notes[..400] + "…" : rel.Notes) : "";
            var ask = MessageBox.Show(owner, $"새 버전 v{Format(rel.Version)}이 있어요. (지금 v{Format(Current)})\n지금 내려받아 설치할까요?\n설치하는 동안 Caffeum이 잠깐 닫혔다가 다시 열려요.{notes}",
                "업데이트 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;
            await DownloadAndInstallAsync(owner, rel);
        }
        finally { busy = false; }
    }

    private static string Format(Version v) => v.Build >= 0 && v.Revision <= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    private static async Task<Release?> FetchLatestAsync()
    {
        using var resp = await http.GetAsync($"https://api.github.com/repos/{RepoSlug}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 아직 릴리스가 없다
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
        string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
            return new Release(ver, tag, a.GetProperty("browser_download_url").GetString() ?? "", name, a.GetProperty("size").GetInt64(), notes);
        }
        return null;
    }

    private static async Task DownloadAndInstallAsync(IWin32Window? owner, Release rel)
    {
        string path = Path.Combine(Path.GetTempPath(), rel.AssetName);
        using var dlg = new Form
        {
            Text = "업데이트 내려받는 중", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ControlBox = false, AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96), ClientSize = new Size(360, 84), Font = new Font(NovelStore.UiFontFamily, 9.5f),
        };
        var label = new Label { Left = 16, Top = 12, Width = 328, Height = 22, Text = $"v{Format(rel.Version)} 내려받는 중…" };
        var bar = new ProgressBar { Left = 16, Top = 42, Width = 328, Height = 20, Maximum = 1000 };
        dlg.Controls.Add(label);
        dlg.Controls.Add(bar);
        dlg.Show(owner as Form);
        try
        {
            using var resp = await http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? rel.Size;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { bar.Value = (int)Math.Min(1000, done * 1000 / total); label.Text = $"v{Format(rel.Version)} 내려받는 중… {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"; }
                }
            }
            if (new FileInfo(path).Length < 1_000_000) throw new IOException("내려받은 파일이 너무 작아요.");
        }
        catch (Exception ex)
        {
            dlg.Close();
            try { File.Delete(path); } catch { }
            MessageBox.Show(owner, "내려받지 못했어요.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        dlg.Close();
        try
        {
            // 설치 프로그램이 Caffeum을 닫고 새 버전으로 덮은 뒤 다시 실행한다. 우리는 저장을 마치고 먼저 끝난다.
            Process.Start(new ProcessStartInfo(path, "/SILENT /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "설치 프로그램을 실행하지 못했어요.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Application.Exit();
    }
}
