; MailDog 安装包脚本（Inno Setup 6）
; 由 src/build/package.ps1 调用，版本 / 源目录 / 输出目录通过 /D 传入。
; 手工编译示例：
;   ISCC.exe /DMyAppVersion=0.1.0 /DMySourceDir=..\..\..\dist\win-x64\MailDog MailDog.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef MySourceDir
  #define MySourceDir "..\..\..\dist\win-x64\MailDog"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\..\..\dist"
#endif

#ifndef MyRuntime
  #define MyRuntime "win-x64"
#endif

[Setup]
AppId={{A3B7F1E0-5C4D-4F8A-9B62-2D7E1C9A4F10}
AppName=MailDog
AppVersion={#MyAppVersion}
AppVerName=MailDog {#MyAppVersion}
AppPublisher=refout
AppComments=MailDog mail client
DefaultDirName={autopf}\MailDog
DefaultGroupName=MailDog
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=MailDog-{#MyAppVersion}-{#MyRuntime}-setup
SetupIconFile=..\..\MailDog.UI\Assets\maildog.ico
UninstallDisplayIcon={app}\MailDog.UI.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MySourceDir}\MailDog.Engine.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\MailDog.UI.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MailDog"; Filename: "{app}\MailDog.UI.exe"
Name: "{autodesktop}\MailDog"; Filename: "{app}\MailDog.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MailDog.UI.exe"; Description: "{cm:LaunchProgram,MailDog}"; Flags: nowait postinstall skipifsilent

[Registry]
; 协议与应用标识是运行时写进 HKCU 的，卸载时一并清掉，避免留下指向已删除程序的条目。
Root: HKCU; Subkey: "Software\Classes\maildog"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\MailDog.MailDog.App"; Flags: uninsdeletekey

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM MailDog.UI.exe /F"; Flags: runhidden; RunOnceId: "KillUI"
Filename: "{cmd}"; Parameters: "/C taskkill /IM MailDog.Engine.exe /F"; Flags: runhidden; RunOnceId: "KillEngine"
