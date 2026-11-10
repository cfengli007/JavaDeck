; JavaDeck 安装包 —— 流程参照 JdkManager
; 关键：使用 signing\InnoSetup6-patched\ISCC.exe 编译
;      该副本的 SetupLdr.e32 已被 patch 为 requireAdministrator，
;      loader 启动即弹 UAC（先申请权限），之后才创建临时目录，
;      避免联想电脑管家内置火绒引擎拦截非提权进程写 %TEMP%。

#ifndef AppVersion
  #define AppVersion "2.4.9"
#endif

[Setup]
AppId={{8F3C2A41-4D6E-4B9A-9C11-7E2D5A0B1C34}
AppName=JavaDeck
AppVersion={#AppVersion}
AppPublisher=JavaDeck
DefaultDirName={autopf}\JavaDeck
DefaultGroupName=JavaDeck
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=dist
OutputBaseFilename=JavaDeck-Setup-{#AppVersion}-win-x64
SetupIconFile=..\JavaDeck.ico
UninstallDisplayIcon={app}\JavaDeck.exe
UninstallDisplayName=JavaDeck
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
DisablePrecompiledFileVerifications=setupldr
ShowLanguageDialog=no

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JavaDeck"; Filename: "{app}\JavaDeck.exe"
Name: "{group}\卸载 JavaDeck"; Filename: "{uninstallexe}"
Name: "{autodesktop}\JavaDeck"; Filename: "{app}\JavaDeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\JavaDeck.exe"; Description: "启动 JavaDeck"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\JavaDeck.exe"; Parameters: "--stop-for-uninstall"; RunOnceId: "StopJavaDeck"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
// 检查 .NET 8 桌面运行时（目录 + 两个注册表视图）
function HasDesktopRuntime(): Boolean;
var
  Base: String;
  I: Integer;
begin
  Result := False;
  Base := ExpandConstant('{commonpf}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';

  if DirExists(Base) then
  begin
    for I := 0 to 200 do
      if DirExists(Base + '\8.0.' + IntToStr(I)) then begin Result := True; Exit; end;
  end;

  if RegKeyExists(HKEY_LOCAL_MACHINE,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') then
    Result := True
  else if RegKeyExists(HKEY_LOCAL_MACHINE,
      'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') then
    Result := True;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime();
  if not Result then
  begin
    if MsgBox('未检测到 .NET 8 桌面运行时。' + #13#10 + #13#10 +
              'JavaDeck 需要它才能运行。是否打开下载页面？' + #13#10 +
              '（装好后重新运行本安装程序即可）',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime',
                '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;