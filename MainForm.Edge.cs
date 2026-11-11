namespace CoffeeViewer;

/// <summary>
/// 책의 끝(마지막 페이지에서 더 넘김)이나 처음(첫 페이지에서 더 되돌림)에 닿았을 때의 처리.
/// - 같은 폴더에 다음(이전) 파일이 있으면: 보기 > "마지막 · 첫 페이지에서" 메뉴대로(멈추기 / 물어보기 / 자동으로 열기).
/// - 폴더의 마지막(첫) 파일이면: 설정의 "처음 · 마지막 파일에서"대로(아무것도 안 함 / 물어보기 / 다시 처음으로 / 다음·이전 폴더로).
/// 만화 zip과 소설 페이지 방식(txt · epub)이 같이 쓴다. 스크롤 방식·pdf는 끝을 알아채는 방식이 달라 여기로 오지 않는다.
/// "옆 파일"은 같은 폴더에서 확장자가 같은 파일을 이름의 자연 순서(2화 → 10화)로 세어 정한다 — txt와 epub이 섞여 있어도
/// 같은 소설이 두 번 나오지 않게 하려는 것.
/// </summary>
public partial class MainForm
{
    private static readonly string[] EdgeExtensions = { ".zip", ".txt", ".epub", ".hwp", ".hwpx" };

    /// <summary>forward가 true면 마지막 페이지에서 더 넘기려 한 것, false면 첫 페이지에서 더 되돌리려 한 것.</summary>
    private void HandleBookEdge(bool forward)
    {
        string edge = forward ? "마지막" : "첫";
        var files = SiblingBookFiles(out int index);
        if (files == null) // 낱장 이미지·zip에서 꺼낸 임시 파일 등 "옆 파일" 개념이 없는 열기 방식
        {
            StopSlideshow();
            statusLabel.Text = $"{edge} 페이지입니다.";
            return;
        }

        int adjacent = index + (forward ? 1 : -1);
        if (adjacent >= 0 && adjacent < files.Count)
        {
            string target = files[adjacent];
            switch (endOfBookAction)
            {
                case EndOfBookAction.Stop:
                    StopSlideshow();
                    statusLabel.Text = $"{edge} 페이지입니다.";
                    break;

                case EndOfBookAction.NextFile:
                    OpenPath(target);
                    break;

                case EndOfBookAction.Ask:
                    // 대화상자로 흐름을 끊지 않고 화면 안에 안내 상자만 띄운다. 줄을 누르거나 ] / [ 키로 옆 파일로 넘어간다(GoAdjacentFile).
                    StopSlideshow();
                    ShowEdgePrompt(forward);
                    break;
            }
            return;
        }

        HandleEdgeFile(forward, files);
    }

    /// <summary>폴더의 마지막(첫) 파일 끝에서 더 넘기려 했을 때 — 설정의 "처음 · 마지막 파일에서".</summary>
    private void HandleEdgeFile(bool forward, List<string> files)
    {
        switch (settings.EdgeFileAction)
        {
            case EdgeFileAction.Ask:
                // 대화상자 대신 화면 안 안내 상자 — 다음(이전) 줄에는 옆 폴더의 파일이 나오고, 누르거나 ] / [ 로 넘어간다(GoAdjacentFile).
                StopSlideshow();
                ShowEdgePrompt(forward);
                break;

            case EdgeFileAction.WrapAround:
                WrapToOtherEnd(forward, files);
                break;

            case EdgeFileAction.AdjacentFolder:
                if (!TryOpenAdjacentFolder(forward))
                {
                    StopSlideshow();
                    statusLabel.Text = forward ? "마지막 폴더입니다. (다음 폴더에 넘어갈 파일이 없습니다)" : "첫 폴더입니다. (이전 폴더에 넘어갈 파일이 없습니다)";
                }
                break;

            default: // Nothing
                StopSlideshow();
                statusLabel.Text = forward ? "마지막 파일의 마지막 페이지입니다." : "첫 파일의 첫 페이지입니다.";
                break;
        }
    }

    /// <summary>폴더의 반대쪽 끝 파일로 돌아간다(마지막 → 첫 파일, 첫 → 마지막 파일). 파일이 하나뿐이면 같은 책의 반대쪽 끝으로.</summary>
    private void WrapToOtherEnd(bool forward, List<string> files)
    {
        if (files.Count > 1) OpenPath(forward ? files[0] : files[^1]);
        else JumpToBookEnd(toStart: forward);
    }

    /// <summary>] (다음) · [ (이전) 키. 같은 폴더의 옆 파일로 넘어가고, 폴더의 끝 파일이면 설정의 "처음 · 마지막 파일에서"에 따라
    /// 반대쪽 끝 파일(다시 처음으로) 또는 옆 폴더로 넘어간다(아무것도 하지 않기면 안내만).</summary>
    private void GoAdjacentFile(bool forward)
    {
        toast.Visible = false; // 떠 있던 안내는 걷는다
        HideEdgePrompt();
        var files = SiblingBookFiles(out int index);
        if (files == null)
        {
            ShowToast("이 파일은 옆 파일로 넘어갈 수 없습니다");
            return;
        }

        int adjacent = index + (forward ? 1 : -1);
        if (adjacent >= 0 && adjacent < files.Count)
        {
            OpenPath(files[adjacent]);
            return;
        }

        switch (settings.EdgeFileAction)
        {
            case EdgeFileAction.Nothing:
                ShowToast(forward ? "마지막 파일입니다" : "첫 파일입니다");
                break;
            case EdgeFileAction.WrapAround:
                WrapToOtherEnd(forward, files);
                break;
            default: // 물어보기 · 폴더 넘어가기
                if (!TryOpenAdjacentFolder(forward)) ShowToast(forward ? "마지막 폴더입니다" : "첫 폴더입니다");
                break;
        }
    }

    /// <summary>] / [ 를 옆 파일 넘기기로 쓸 수 있는 상태인지. 글을 고치는 중이거나 찾기 칸 같은 입력 칸에 커서가 있으면 그냥 글자로 입력돼야 한다.</summary>
    private bool CanUseBracketKeys()
    {
        if (!(imageMode || epubMode || editorHost.Visible)) return false; // pdf 등
        if (epubEditable || (editorHost.Visible && !mainEditor.ReadOnly)) return false;
        Control focused = this;
        while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
        return focused is not TextBoxBase { ReadOnly: false };
    }

    // ───────────────────────── 끝 · 처음 안내 상자(반디뷰 스타일) ─────────────────────────

    private readonly EdgePrompt edgePrompt = new();

    private void InitEdgePrompt()
    {
        edgePrompt.RowClicked += forward => GoAdjacentFile(forward);
        Controls.Add(edgePrompt);
    }

    /// <summary>끝 · 처음에 닿았을 때 화면 아래쪽에 "마지막 이미지입니다 + 이전 · 다음 파일" 상자를 띄운다.
    /// 두 줄에는 ] / [ (= 줄 클릭)가 실제로 열 파일을 보여준다 — 폴더 끝이면 설정에 따라 옆 폴더의 파일이나 반대쪽 끝 파일.</summary>
    private void ShowEdgePrompt(bool forward)
    {
        string what = imageMode ? "이미지" : "페이지";
        string kind = string.Equals(Path.GetExtension(currentPath), ".zip", StringComparison.OrdinalIgnoreCase) ? "압축파일" : "파일";
        var (prevPath, prevOk) = EdgeTarget(forward: false);
        var (nextPath, nextOk) = EdgeTarget(forward: true);
        edgePrompt.SetContent(forward ? $"마지막 {what}입니다." : $"첫 {what}입니다.",
            $"이전 {kind} ( [ )", TargetName(prevPath, prevOk, toStart: false), prevOk,
            $"다음 {kind} ( ] )", TargetName(nextPath, nextOk, toStart: true), nextOk);
        PlaceEdgePrompt();
        edgePrompt.Visible = true;
        edgePrompt.BringToFront();
    }

    private void HideEdgePrompt() => edgePrompt.Visible = false;

    private void PlaceEdgePrompt()
    {
        edgePrompt.FitTo(contentHost.Width);
        // 본문 가운데 위쪽(반디뷰처럼) — 아래쪽의 탐색 막대 · 알림과 겹치지 않게.
        edgePrompt.Left = contentHost.Left + (contentHost.Width - edgePrompt.Width) / 2;
        edgePrompt.Top = contentHost.Top + 24;
    }

    /// <summary>] (forward) · [ 키가 지금 열 파일. Path가 null인데 CanGo면 같은 책의 반대쪽 끝으로 가는 것(폴더에 파일이 하나뿐일 때 "다시 처음으로").</summary>
    private (string? Path, bool CanGo) EdgeTarget(bool forward)
    {
        var files = SiblingBookFiles(out int index);
        if (files == null) return (null, false);
        int adjacent = index + (forward ? 1 : -1);
        if (adjacent >= 0 && adjacent < files.Count) return (files[adjacent], true);
        switch (settings.EdgeFileAction)
        {
            case EdgeFileAction.Nothing:
                return (null, false);
            case EdgeFileAction.WrapAround:
                return files.Count > 1 ? (forward ? files[0] : files[^1], true) : (null, true);
            default: // 물어보기 · 폴더 넘어가기 → 옆 폴더의 파일
                var p = FindAdjacentFolderBook(forward);
                return (p, p != null);
        }
    }

    /// <summary>안내 상자 줄에 쓸 이름 — 같은 폴더면 파일 이름만, 다른 폴더면 "폴더 / 파일"(한글 글꼴은 \ 를 ₩ 로 그려서 / 를 쓴다).</summary>
    private string? TargetName(string? path, bool canGo, bool toStart)
    {
        if (path == null) return canGo ? (toStart ? "이 파일의 처음으로" : "이 파일의 끝으로") : null;
        string name = Path.GetFileNameWithoutExtension(path);
        string? dir = Path.GetDirectoryName(path);
        string? here = currentPath == null ? null : Path.GetDirectoryName(Path.GetFullPath(currentPath));
        return string.Equals(dir, here, StringComparison.OrdinalIgnoreCase) ? name : $"{Path.GetFileName(dir)} / {name}";
    }

    /// <summary>같은 책의 처음(toStart) 또는 맨 끝으로 간다. 폴더에 파일이 하나뿐일 때 "다시 처음으로"가 쓴다.</summary>
    private void JumpToBookEnd(bool toStart)
    {
        if (imageMode)
        {
            if (imagePages.Count > 0) ShowImagePage(toStart ? 0 : imagePages.Count - 1);
        }
        else if (epubMode)
        {
            if (epubBook != null && epubBook.Chapters.Count > 0)
                ShowEpubChapter(toStart ? 0 : epubBook.Chapters.Count - 1, startAtLastPage: !toStart);
        }
        else if (editorHost.Visible && pageMode)
        {
            if (toStart)
            {
                currentPageStart = 0;
                currentPageEnd = FindPageEnd(0);
            }
            else
            {
                currentPageEnd = fullText.Length;
                currentPageStart = FindPageStart(currentPageEnd);
            }
            ShowCurrentPage();
            mainEditor.SelectionStart = 0;
            mainEditor.ScrollToCaret();
        }
    }

    /// <summary>같은 부모 폴더 안의 다음(이전) 폴더로 넘어가 그 폴더의 첫(마지막) 파일을 연다. 파일이 없는 폴더는 건너뛴다.</summary>
    private bool TryOpenAdjacentFolder(bool forward)
    {
        var target = FindAdjacentFolderBook(forward);
        if (target == null) return false;
        OpenPath(target);
        return true;
    }

    /// <summary>다음(이전) 폴더의 첫(마지막) 파일 — 열지는 않고 찾기만 한다(안내 상자에 이름을 보여줄 때도 쓴다). 없으면 null.</summary>
    private string? FindAdjacentFolderBook(bool forward)
    {
        if (SiblingBookFiles(out _) == null) return null;
        try
        {
            string full = Path.GetFullPath(currentPath!); // 위에서 옆 파일이 있다고 확인했으니 비어 있지 않다
            string ext = Path.GetExtension(full).ToLowerInvariant();
            string? dir = Path.GetDirectoryName(full);
            string? parent = dir == null ? null : Path.GetDirectoryName(dir);
            if (dir == null || parent == null) return null;

            var folders = Directory.EnumerateDirectories(parent).OrderBy(d => d, NaturalStringComparer.Instance).ToList();
            int here = folders.FindIndex(d => string.Equals(d.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (here < 0) return null;

            int step = forward ? 1 : -1;
            for (int i = here + step; i >= 0 && i < folders.Count; i += step)
            {
                var books = BookFilesIn(folders[i], ext);
                if (books.Count == 0) continue;
                return forward ? books[0] : books[^1];
            }
        }
        catch { /* 폴더를 못 읽으면(권한 등) 넘어갈 곳이 없는 것으로 */ }
        return null;
    }

    private static List<string> BookFilesIn(string dir, string ext) =>
        Directory.EnumerateFiles(dir)
            .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, NaturalStringComparer.Instance)
            .ToList();

    /// <summary>지금 연 파일과 같은 폴더의 같은 확장자 파일들(자연 순서)과 그 안에서 지금 파일의 자리. "옆 파일" 개념이 없으면 null.</summary>
    private List<string>? SiblingBookFiles(out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(currentPath)) return null;
        try
        {
            string full = Path.GetFullPath(currentPath);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!EdgeExtensions.Contains(ext)) return null; // 낱장 이미지·pdf 등

            // zip 안의 epub을 꺼내 연 임시 파일은 옆 파일이 아니라 임시 폴더의 다른 zip 것들이다.
            string tempZipRoot = Path.Combine(Path.GetTempPath(), "CoffeeViewerZip");
            if (full.StartsWith(tempZipRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

            string? dir = Path.GetDirectoryName(full);
            if (dir == null) return null;
            var files = BookFilesIn(dir, ext);
            index = files.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : files;
        }
        catch { return null; }
    }
}
