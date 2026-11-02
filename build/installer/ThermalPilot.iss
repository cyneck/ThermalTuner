; ThermalPilot 安装包脚本（Inno Setup 6 备选方案）
; 与 build\installer\ThermalPilot.wxs（MSI）二选一；Inno 向导更轻量，MSI 卸载更标准。
; 构建：ISCC.exe ThermalPilot.iss
; 前提：先运行 build\publish.ps1 生成 artifacts\publish\ThermalPilot.exe

#define MyAppName "ThermalPilot 温控与调频"
#define MyAppVersion "1.0.0"
#define MyAppExeName "ThermalPilot.exe"

[Setup]
AppId={{F3A71C92-6D4E-4B7A-9C18-2A5E0B7D6311}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=ThermalPilot
DefaultDirName={autopf}\ThermalPilot
DefaultGroupName=ThermalPilot
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\..\artifacts
OutputBaseFilename=ThermalPilot-Setup-1.0.0-Setup
SetupIconFile=..\..\src\ThermalPilot.App\Icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "..\..\artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前还原电源方案（风扇策略由程序退出钩子还原）
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore"; RunOnceId: "RestorePower"; Flags: runhidden

[UninstallDelete]
; 保留 %ProgramData%\ThermalPilot 的配置与日志（含原始电源值快照），如需彻底清理可删除下一行注释
; Type: filesandordirs; Name: "{commonappdata}\ThermalPilot"
