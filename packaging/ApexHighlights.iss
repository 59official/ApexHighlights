; ApexHighlights 安装包脚本（Inno Setup 6）
;
; 为什么要有安装包：便携 zip 解压出 266 个文件，普通玩家看到会以为下错了东西；
; 而且「开始菜单里有没有」直接决定了他会不会第二次打开。安装包给的是双击安装、
; 开始菜单、卸载项这三件最朴素的东西。
;
; 装到哪儿：默认 {autopf}。配 PrivilegesRequired=lowest 时那就是
; %LOCALAPPDATA%\Programs\ApexHighlights —— 不需要管理员权限、目录可写，于是
; config.json 和 captures\ 就落在 exe 旁边（Paths 的规则：exe 同目录可写就用户目录优先），
; 用户想清干净直接删文件夹就行。选「为所有用户安装」时才进 Program Files，
; 那时 Paths 会自动退到 %LOCALAPPDATA%\ApexHighlights 存数据。
;
; 编译（版本号从命令行传，别在这里写死）：
;   ISCC.exe /DAppVersion=0.7.0 packaging\ApexHighlights.iss

#define AppName "ApexHighlights"
#define AppExe "ApexHighlights.exe"
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
; AppId 一旦发布就不能改：Windows 靠它认「这是同一个程序的另一次安装」
AppId={{8B2E4A61-7C3D-4F58-9E42-1D6C0A9B7E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
; 默认当前用户安装：不弹 UAC、目录可写（config.json 与 captures\ 就落在 exe 旁边）。
; 刻意不写 PrivilegesRequiredOverridesAllowed=dialog —— 那个「为所有用户 / 仅为我」的
; 小对话框是 Inno 在选语言之前弹的，永远是英文，中文用户第一眼就撞上英文界面。
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=ApexHighlights.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 更新时如果托盘还开着，让 Restart Manager 去关它（比让用户自己找托盘图标友好）
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "zhcn"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStartProgram,{#AppName}}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"; Flags: unchecked

[Files]
; dist 是 dotnet publish 的输出（self-contained，用户不需要装 .NET）
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Apex 击倒片段录制与回顾"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Apex 击倒片段录制与回顾"; Tasks: desktopicon

[Registry]
; 开机自启：只写当前用户的 Run 键，不需要管理员
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 应用自己写出来的东西：配置、日志、释放出来的游戏档案。清掉它们才算真的卸干净
Type: files; Name: "{app}\config.json"
Type: files; Name: "{app}\live.log"
Type: files; Name: "{app}\live.log.csv"
Type: filesandordirs; Name: "{app}\games"

[Code]
// 片段要不要一起删，得问一句 —— 那是用户真正在乎的东西，删错了没法恢复。
// 默认按钮是「否」：手一滑回车不该把几十 GB 录像带走。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
  HasFiles: Boolean;
  FindRec: TFindRec;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  Dir := ExpandConstant('{app}\captures');
  HasFiles := False;
  if DirExists(Dir) then
  begin
    if FindFirst(Dir + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
            HasFiles := True;
        until HasFiles or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  if HasFiles then
  begin
    if MsgBox('录下的片段还留在：' + #13#10 + Dir + #13#10#13#10 +
              '要一起删掉吗？删了没法恢复。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Dir, True, True, True);
  end;
end;

// 装之前友好的提醒：dist 没 publish 过的话这里会明确报错，而不是装出个空目录
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not FileExists(ExpandConstant('{src}\..\dist\{#AppExe}')) then
  begin
    MsgBox('没找到 ..\dist\{#AppExe}。' + #13#10 +
           '先跑一次 dotnet publish（或 packaging\pack-release.sh）再来编译安装包。',
           mbError, MB_OK);
    Result := False;
  end;
end;
