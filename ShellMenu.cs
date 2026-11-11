using Microsoft.Win32;

namespace CoffeeViewer;

/// <summary>
/// 탐색기에서 소설 · 만화 · 문서 파일을 오른쪽 클릭하면 나오는 "Caffeum으로 보기"를 현재 사용자 레지스트리(HKCU, 관리자 권한 없이)에 등록한다.
/// 누르면 이 프로그램이 그 파일을 PIP 창으로 연다(AppShared.Start). 실행할 때마다 지금 exe 경로로 맞춰 둔다.
/// </summary>
internal static class ShellMenu
{
    private const string Verb = "Caffeum";
    private const string Label = "Caffeum으로 보기";

    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !File.Exists(exe)) return;
            foreach (var ext in NovelStore.ReferenceExtensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}");
                if (key == null) continue;
                key.SetValue("", Label);
                key.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = key.CreateSubKey("command");
                cmd?.SetValue("", $"\"{exe}\" \"%1\"");
            }
        }
        catch { /* 등록하지 못해도 프로그램은 그대로 쓴다 */ }
    }
}
