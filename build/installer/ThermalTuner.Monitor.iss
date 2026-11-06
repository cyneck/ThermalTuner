; ThermalTuner Monitor 安装包脚本（Inno Setup 6）
; 用法：先发布单文件 exe 到 artifacts\monitor-win-x64，再用 Inno Setup Compiler 打开本脚本编译。
;   iscc build\installer\ThermalTuner.Monitor.iss
;
; 产物：artifacts\ThermalTuner-Monitor-Setup-<版本>.exe
; 包含：安装向导、开始菜单快捷方式、桌面快捷方式（可选）、卸载入口

#define AppName "ThermalTuner Monitor"
#define AppExe "ThermalTuner.Monitor.exe"
#define AppVersion "1.0.0"
#define SourceDir "..\..\artifacts\monitor-win-x64"

[Setup]
AppId={{8B2F0B7A-4C1D-4D0E-9E7E-THERMALPILOT01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ThermalTuner
DefaultDirName={autopf}\ThermalTuner\Monitor
DefaultGroupName=ThermalTuner
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\..\artifacts
OutputBaseFilename=ThermalTuner-Monitor-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 需要管理员：读取 CPU 温度/功耗必须提权
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "runonstartup"; Description: "以管理员身份开机自启（任务计划）"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; 如需随包附带示例配置/文档，在此追加 Source 行

[Icons]
Name: "{group}\{#AppName} (管理员)"; Filename: "{app}\{#AppExe}"; Parameters: "--simulate"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动监控（管理员）"; Flags: runascurrentuser nowait postinstall skipifsilent

[Registry]
; 记录安装路径，便于排查
Root: HKLM; Subkey: "Software\ThermalTuner\Monitor"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
