using System.Diagnostics;
using Microsoft.Win32;

namespace CoffeeViewer;

/// <summary>
/// 한글 문서(.hwp · .hwpx). 읽기는 본문 글을 뽑아(HwpDocument) txt와 같은 웹 화면으로 보여주고, "수정하기"는 설치된 한컴 한글에
/// 그 파일을 넘긴다 — 표 · 그림 · 글자 모양을 지킨 채 고치려면 한글에서 고쳐야 해서다. 한글에서 저장하면 파일이 바뀐 걸 알아채고
/// 읽던 자리 그대로 다시 읽어 온다.
/// </summary>
public partial class MainForm
{
    private bool hwpSource; // 지금 보는 글이 한글 문서에서 뽑은 것(저장은 txt로 "다른 이름으로"만, 수정은 한컴 한글로)
    private FileSystemWatcher? hwpWatcher;
    private string? hwpWatchedPath;
    private readonly System.Windows.Forms.Timer hwpReloadTimer = new() { Interval = 1500 };

    private async void LoadHwpFile(string path)
    {
        string text;
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("한글 문서를 여는 중…");
        try
        {
            text = await Task.Run(() => HwpDocument.ExtractText(path), cts.Token);
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostForm, $"한글 문서를 여는 데 실패했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }

        ShowLoadedText(path, text, prepared: null, fromZip: false);
        if (hwpWatchedPath != null && !string.Equals(hwpWatchedPath, path, StringComparison.OrdinalIgnoreCase)) StopHwpWatch();
    }

    /// <summary>수정하기: 한컴 한글로 이 파일을 연다. 저장하면 알아채서 다시 읽도록 파일을 지켜본다.</summary>
    private void EditHwpInHancom()
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        string? exe = FindHancomExe(Path.GetExtension(currentPath));
        if (exe == null)
        {
            MessageBox.Show(HostForm, "한컴오피스 한글이 설치되어 있지 않아 고칠 수 없습니다.\n(읽기는 한글 없이도 됩니다)", "한글에서 수정하기",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{currentPath}\"") { UseShellExecute = false });
            StartHwpWatch(currentPath);
            ShowToast("한글에서 고친 뒤 저장하면 여기에도 바로 반영됩니다", 3500);
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostForm, $"한컴 한글을 실행하지 못했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 이 확장자를 여는 한컴 한글 실행 파일. 확장자 → ProgID(예: Hwp.Document.110) → shell\open\command 순으로 찾고, 그 프로그램이
    /// 한글(Hwp.exe)일 때만 쓴다 — 커피뷰어가 .hwp의 기본 프로그램으로 잡혀 있으면 커피뷰어를 다시 여는 꼴이 되기 때문이다.
    /// </summary>
    private static string? FindHancomExe(string ext)
    {
        try
        {
            foreach (string e in new[] { ext, ".hwp", ".hwpx" })
            {
                using var extKey = Registry.ClassesRoot.OpenSubKey(e);
                string? progId = extKey?.GetValue(null) as string;
                foreach (string id in new[] { progId, "Hwp.Document.110", "Hwp.Document" })
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    using var cmd = Registry.ClassesRoot.OpenSubKey($@"{id}\shell\open\command");
                    if (cmd?.GetValue(null) is not string line) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(line, "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
                    string exe = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (File.Exists(exe) && Path.GetFileName(exe).StartsWith("Hwp", StringComparison.OrdinalIgnoreCase)) return exe;
                }
            }
        }
        catch { }
        return null;
    }

    private void StartHwpWatch(string path)
    {
        StopHwpWatch();
        string? dir = Path.GetDirectoryName(path);
        if (dir == null) return;
        hwpWatchedPath = path;
        hwpWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        // 한글은 임시 파일에 쓴 뒤 이름을 바꿔 저장하기도 해서, 바뀜 · 새로 생김 · 이름 바뀜을 모두 본다. 저장 한 번에 이벤트가
        // 여러 번 오므로 잠깐(1.5초) 조용해지면 한 번만 다시 읽는다.
        FileSystemEventHandler onChange = (_, _) => BeginInvoke(() => { hwpReloadTimer.Stop(); hwpReloadTimer.Start(); });
        hwpWatcher.Changed += onChange;
        hwpWatcher.Created += onChange;
        hwpWatcher.Renamed += (_, e) => { if (string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase)) onChange(null!, null!); };
    }

    private void StopHwpWatch()
    {
        hwpWatcher?.Dispose();
        hwpWatcher = null;
        hwpWatchedPath = null;
        hwpReloadTimer.Stop();
    }

    private void InitHwp()
    {
        hwpReloadTimer.Tick += (_, _) =>
        {
            hwpReloadTimer.Stop();
            string? path = hwpWatchedPath;
            if (path == null || !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
            CapturePosition();       // 다시 읽어도 지금 보던 자리에서 이어지게(ShowLoadedText가 서재 기록의 위치로 돌아간다)
            library.SaveIfDirty();
            ReloadHwpAfterSave(path, attempt: 1);
        };
        FormClosed += (_, _) => { StopHwpWatch(); hwpReloadTimer.Dispose(); };
    }

    /// <summary>한글이 아직 파일을 쓰는 중이라 못 읽으면 잠깐 뒤 한 번 더 해 본다.</summary>
    private async void ReloadHwpAfterSave(string path, int attempt)
    {
        try
        {
            string text = await Task.Run(() => HwpDocument.ExtractText(path));
            if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) return;
            ShowLoadedText(path, text, prepared: null, fromZip: false);
            ShowToast("한글에서 저장한 내용을 다시 읽었습니다");
        }
        catch when (attempt < 3)
        {
            await Task.Delay(1500);
            ReloadHwpAfterSave(path, attempt + 1);
        }
        catch (Exception ex)
        {
            ShowToast($"바뀐 한글 문서를 다시 읽지 못했습니다: {ex.Message}", 4000);
        }
    }
}
