; ThermalTuner 安装包脚本（Inno Setup 6）
; 生成：iscc /DMyAppVersion=0.5.1 /DSourceDir=..\..\artifacts\app-win-x64 ThermalTuner.App.iss
; 特性：安装向导、桌面/开始菜单快捷方式、卸载入口、可选开机自启、可选保留用户配置

#define MyAppName "ThermalTuner"
#define MyAppPublisher "ThermalTuner"
#define MyAppExeName "ThermalTuner.App.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.5.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\app-win-x64"
#endif

[Setup]
AppId={{8F3D2B51-6C4A-4E7B-9D1E-2A5C7B9E4F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 读取 CPU 封装温度与写 MSR 都需要管理员，安装阶段即声明，避免运行期反复提权
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\..\artifacts
OutputBaseFilename=ThermalTuner-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
; 如有 ico 资源，取消下一行注释并放到 build/installer/app.ico
; SetupIconFile=app.ico
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce
Name: "autostart"; Description: "开机自动启动（托盘常驻）"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 单文件发布产物（框架依赖或自包含均适用）
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 若发布目录还有其它文件（如 .pdb、依赖 dll），一并带上
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,smoke.txt"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启写在当前用户 HKCU（安装时提权，但 HKCU 仍指向登录用户）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "ThermalTuner"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 仅清理程序目录；用户配置（%LOCALAPPDATA%\ThermalTuner）默认保留，避免误删 MSR 备份
Type: filesandordirs; Name: "{app}"

[UninstallRun]
; 卸载前尝试结束正在运行的托盘进程（避免文件占用）
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// 卸载时询问是否删除用户配置与 MSR 备份（默认保留）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\ThermalTuner');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除本地配置与 MSR 备份？' + #13#10 +
                '（保留可让下次安装延续设置；删除会丢失原值备份）',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
