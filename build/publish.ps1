#Requires -Version 5.1
<#
.SYNOPSIS
  ThermalPilot 一键构建：单文件便携 exe + MSI 安装包（带卸载）。
.NOTES
  依赖：
    1) .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）
    2) WiX 5（dotnet tool install --global wix --version 5.0.2
       然后 wix extension add -g WixToolset.UI.wixext/5.0.2）
  用法：
    powershell -ExecutionPolicy Bypass -File build\publish.ps1            # 全量
    powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipMsi   # 仅便携 exe
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.2.0",
    [switch]$SkipMsi,
    [switch]$NoElevation   # 调试用：生成不提权的 exe（配合 --smoke 界面自检）
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$art = Join-Path $root "artifacts"
$appProj = Join-Path $root "src\ThermalPilot.App\ThermalPilot.App.csproj"
# 用完整版本号做目录：旧目录里的 exe 正在运行时（用户开着程序），
# 发布到同名目录会因文件占用而失败（MSB4018），换版本目录可避免"必须先关程序才能打包"。
$pubDir = Join-Path $art "publish-v$Version"

Write-Host "== [1/3] 编译并发布单文件 exe ==" -ForegroundColor Cyan
$pubArgs = @(
    "publish", $appProj,
    "-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:Version=$Version",
    "-o", $pubDir
)
if ($NoElevation) { $pubArgs += "-p:NoElevation=true" }
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
Get-ChildItem $pubDir

if ($SkipMsi) { Write-Host "== 跳过 MSI ==" ; return }

Write-Host "== [2/3] 检查 WiX ==" -ForegroundColor Cyan
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "未找到 wix 命令。请先执行：dotnet tool install --global wix --version 5.0.2，再执行 wix extension add -g WixToolset.UI.wixext/5.0.2"
}

Write-Host "== [3/3] 构建 MSI 安装包 ==" -ForegroundColor Cyan
$msi = Join-Path $art "ThermalPilot-Setup-$Version.msi"
& wix build -arch x64 -ext WixToolset.UI.wixext -culture zh-CN `
    -d "ProductVersion=$Version.0" `
    -d "PublishDir=$pubDir" `
    -d "IconFile=$(Join-Path $root 'src\ThermalPilot.App\Icon.ico')" `
    -d "LicenseFile=$(Join-Path $PSScriptRoot 'installer\License.rtf')" `
    (Join-Path $PSScriptRoot "installer\ThermalPilot.wxs") `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build 失败" }

Write-Host "完成：" -ForegroundColor Green
Get-ChildItem $art -File | Select-Object FullName, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) }}
