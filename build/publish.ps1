# ThermalPilot 构建脚本（便携版 + 安装包）
#
# 用法：
#   pwsh -File build/publish.ps1                 # 框架依赖单文件（约 12MB，需目标机装 .NET 8 桌面运行时）
#   pwsh -File build/publish.ps1 -SelfContained  # 自包含单文件（约 150MB，目标机免装运行时）
#   pwsh -File build/publish.ps1 -Installer      # 额外调用 Inno Setup 生成安装包（需 iscc.exe）

param(
    [switch]$SelfContained,
    [switch]$Installer,
    [string]$Configuration = "Release",
    [string]$Version = "0.4.3"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "artifacts/app-win-x64"
$proj = Join-Path $root "src/ThermalPilot.App/ThermalPilot.App.csproj"
# 本机只装了用户级 SDK（无系统级 dotnet SDK），优先用 DOTNET_ROOT 指向的 dotnet.exe
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else { "dotnet" }

Write-Host "==> 清理输出目录" -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$publishArgs = @(
    $proj, "-c", $Configuration, "-r", "win-x64",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:Version=$Version",
    "-p:FileVersion=$Version.0",
    "-p:AssemblyVersion=$Version.0",
    "-o", $outDir, "--nologo"
)
if ($SelfContained) { $publishArgs += @("--self-contained", "true") }
else { $publishArgs += @("--self-contained", "false") }

Write-Host "==> 发布（SelfContained=$SelfContained）" -ForegroundColor Cyan
& $dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

$exe = Join-Path $outDir "ThermalPilot.App.exe"
if (-not (Test-Path $exe)) { throw "未生成 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 便携版：$exe ($size MB)" -ForegroundColor Green

Write-Host "==> 冒烟自检" -ForegroundColor Cyan
$smoke = Join-Path $outDir "smoke.txt"
& $exe --smoke --out $smoke | Out-Null
$result = Select-String -Path $smoke -Pattern "RESULT:" | Select-Object -First 1
Write-Host "    $($result.Line)" -ForegroundColor Green
if ($result.Line -notmatch "PASS") { throw "冒烟自检未通过，见 $smoke" }

if ($Installer) {
    $iscc = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "未找到 Inno Setup 6（ISCC.exe）。请安装后重试：https://jrsoftware.org/isdl.php"
        Write-Warning "安装包脚本已就绪：build/installer/ThermalPilot.App.iss"
        exit 0
    }

    Write-Host "==> 生成安装包" -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$Version" "/DSourceDir=$outDir" (Join-Path $root "build/installer/ThermalPilot.App.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC 失败" }
    Get-ChildItem (Join-Path $root "artifacts") -Filter "ThermalPilot-Setup-*.exe" |
        ForEach-Object { Write-Host "==> 安装包：$($_.FullName) ($([math]::Round($_.Length/1MB,1)) MB)" -ForegroundColor Green }
}
