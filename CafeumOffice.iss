; 카페움 오피스 설치 프로그램 스크립트 (Inno Setup)
; publish 폴더의 self-contained 단일 exe를 그대로 배포한다. 관리자 권한 없이(현재 사용자 전용) 설치된다.
; 커피뷰어(CoffeeViewer.iss)와는 별개 프로그램이라 AppId · 설치 폴더 · 바로가기가 모두 따로다.
; 탐색기 우클릭 "Caffeum으로 보기"(클래식 메뉴)는 등록한다. 윈도우 11 새 우클릭 메뉴 직접 노출은 아직 넣지 않았다.

#define MyAppName "CafeumOffice"
#define MyAppDisplayName "Caffeum"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "JuneSilver"
#define MyAppExeName "CafeumOffice.exe"

[Setup]
AppId={{B3A5E0C4-7D1F-4E8A-9A52-5C2D6F0B1E37}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppDisplayName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
PrivilegesRequired=lowest
OutputDir=Install
OutputBaseFilename=CafeumOffice-Setup-{#MyAppVersion}
SetupIconFile=Img\CaffeumTaskbar.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 아이콘 만들기"; GroupDescription: "추가 아이콘:"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppDisplayName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppDisplayName} 실행"; Flags: nowait postinstall

[Registry]
; 탐색기 오른쪽 클릭 메뉴 "Caffeum으로 보기"(현재 사용자 전용 · 관리자 권한 불필요 · Windows 10 / 11 공통). 앱도 실행할 때 같은 항목을 맞춰 둔다.
; 제거하면 이 키들도 함께 지워진다(uninsdeletekey).
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Caffeum"; ValueType: string; ValueData: "Caffeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Caffeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Caffeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
