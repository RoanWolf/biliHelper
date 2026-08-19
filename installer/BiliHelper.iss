; BiliHelper Inno Setup 脚本
; 输入目录（CI 组装产物，位于仓库根 dist/）:
;   dist/app/                      dotnet publish 输出（BiliHelperWpf.exe 等）
;   dist/bilihelperCore/           Python 后端（源码 + .venv/ 组装好的 embed 运行时）
;   dist/alma.ico                  安装包图标
; 输出: dist/BiliHelper-Setup-v{version}.exe
;
; 关键约定（与 WPF 运行时契约一致，勿破坏）:
;   - 安装布局必须保持 {app}\bilihelperCore\.venv\Scripts\python.exe（WPF 4 个 Service spawn 此路径）
;   - 用户级安装（PrivilegesRequired=lowest）：history/_log 写在程序目录下，Program Files 无写权限会崩
;   - {app} 下直接放 BiliHelperWpf.exe —— FindProjectRoot 从 BaseDirectory 向上找 bilihelperCore 目录，同级命中

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppName "BiliHelper"
#define MyAppPublisher "RoanWolf"
#define MyAppExeName "BiliHelperWpf.exe"

[Setup]
AppId={{9c644a4a-3c0b-418e-a519-bdfd6e22d94d}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\BiliHelper
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=BiliHelper-Setup-v{#MyAppVersion}
SetupIconFile=..\dist\alma.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 用户级安装：免管理员，装到 %LocalAppData%（history/_log 需要写权限）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 桌面应用（dotnet publish 产物）
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Python 后端（源码 + 组装好的 embed 运行时）
Source: "..\dist\bilihelperCore\*"; DestDir: "{app}\bilihelperCore"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
