# ThermalTuner 集成测试脚本
#
# 用法（Windows PowerShell 5.1 或 PowerShell 7 均可）：
#   powershell -ExecutionPolicy Bypass -File build/test.ps1                     # 轻量：构建 + 功能测试 + Runner + Monitor + App 冒烟
#   powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware           # 追加 MSR/RAPL 通道自检（需管理员）
#   powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware -Full     # 追加三轮满载压测（约 5 分钟）
#   powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware -Elevate  # 非管理员时自动请求提权
#
# 产物：artifacts/test-runs/integration-<时间戳>.txt，任一步失败脚本返回非零。
# 说明：统一用 .NET Process 启动子进程，避免 PowerShell 5.1 的 Start-Process 在
#       环境变量大小写重复（HTTPS_PROXY / https_proxy）时抛 "已添加项" 的问题。

param(
    [switch]$Hardware,
    [switch]$Full,
    [switch]$Elevate,
    [switch]$SkipBuild,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $root "artifacts/test-runs" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ---------- 提权转交 ----------
if ($Hardware -and -not $isAdmin) {
    if (-not $Elevate) {
        Write-Host "[FAIL] -Hardware 需要管理员权限。请以管理员重开终端，或追加 -Elevate 自动提权。" -ForegroundColor Red
        exit 2
    }

    Write-Host "==> 请求管理员权限并转交集成测试" -ForegroundColor Cyan
    $argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Hardware -OutDir "' + $OutDir + '"'
    if ($Full) { $argLine += " -Full" }
    if ($SkipBuild) { $argLine += " -SkipBuild" }

    $startTime = Get-Date
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $true
    $psi.Verb = "runas"
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    } catch {
        Write-Host ("[FAIL] 提权失败：" + $_.Exception.Message) -ForegroundColor Red
        exit 4
    }

    # 提权进程的句柄不一定可取，改为轮询报告文件：报告只在全部步骤结束后写盘。
    $latest = $null
    $deadline = $startTime.AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $candidate = Get-ChildItem $OutDir -Filter "integration-*.txt" -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $candidate -and $candidate.LastWriteTime -gt $startTime) { $latest = $candidate; break }
        $exited = $false
        try { $exited = $proc.HasExited } catch { }
        if ($exited) {
            $candidate = Get-ChildItem $OutDir -Filter "integration-*.txt" -ErrorAction SilentlyContinue |
                         Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -ne $candidate -and $candidate.LastWriteTime -gt $startTime) { $latest = $candidate }
            break
        }
    }

    if ($null -eq $latest) {
        Write-Host "[FAIL] 提权测试未生成报告（可能被 UAC 拒绝或提前退出）。" -ForegroundColor Red
        exit 1
    }

    Write-Host "==> 提权测试报告：$($latest.FullName)" -ForegroundColor Cyan
    $reportText = Get-Content $latest.FullName -Raw -Encoding UTF8
    ($reportText -split "`r?`n" | Select-Object -Last 30) | ForEach-Object { Write-Host $_ }
    # 只看汇总段的最后一行，避免匹配到明细里出现的 "RESULT: PASS"
    $lastResult = ($reportText -split "`r?`n" | Where-Object { $_ -match "^RESULT:" } | Select-Object -Last 1)
    if ($lastResult -match "RESULT: PASS") { exit 0 }
    exit 1
}

# ---------- 报告 ----------
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $OutDir ("integration-" + $stamp + ".txt")
$script:lines = New-Object System.Collections.Generic.List[string]
$script:failures = New-Object System.Collections.Generic.List[string]
$totalTimer = [Diagnostics.Stopwatch]::StartNew()

function Log([string]$text = "") {
    $script:lines.Add($text)
    Write-Host $text
}

function Section([string]$name) {
    Log ""
    Log ("==== " + $name + " ====")
}

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $tag = "FAIL"
    if ($ok) { $tag = "PASS" }
    Log ("[{0}] {1} :: {2}" -f $tag, $name, $detail)
    if (-not $ok) { $script:failures.Add($name) }
}

function KeyLines([string]$content, [int]$max = 2) {
    if ([string]::IsNullOrWhiteSpace($content)) { return "" }
    $picked = $content -split "`r?`n" |
              Where-Object { $_ -match "RESULT|通道状态|状态\s*:|Writable|可写|PL1 |同值|恢复" } |
              Select-Object -First $max
    return (($picked -join " / ").Trim())
}

# 启动原生程序（不重建环境字典，规避 PowerShell 5.1 的重复代理变量缺陷）
function Start-Native([string]$exe, [string[]]$cmdArgs, [bool]$capture) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($cmdArgs -join " ")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($capture) {
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $utf8
        $psi.StandardErrorEncoding = $utf8
    }
    return [System.Diagnostics.Process]::Start($psi)
}

function Invoke-Capture([string]$exe, [string[]]$cmdArgs) {
    $p = Start-Native $exe $cmdArgs $true
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Output = ($outTask.Result + $errTask.Result) }
}

# ---------- 环境 ----------
Section "环境"
Log ("时间       : " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
Log ("仓库       : " + $root)
Log ("管理员     : " + $isAdmin)
Log ("模式       : Hardware=" + $Hardware + " Full=" + $Full)
Log ("报告       : " + $reportPath)

$dotnet = ""
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    $dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} elseif (Test-Path (Join-Path $env:USERPROFILE ".dotnet-sdk\dotnet.exe")) {
    $dotnet = Join-Path $env:USERPROFILE ".dotnet-sdk\dotnet.exe"
} else {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnetCmd) { $dotnet = $dotnetCmd.Source }
}
if ([string]::IsNullOrWhiteSpace($dotnet) -or -not (Test-Path $dotnet)) {
    Write-Host "[FAIL] 找不到 dotnet。请安装 .NET 8 SDK，或设置 DOTNET_ROOT 指向 SDK 目录。" -ForegroundColor Red
    exit 3
}
Log ("dotnet     : " + $dotnet)

# ---------- 1. 构建 ----------
Section "1. 构建（Release）"
if ($SkipBuild) {
    Add-Result "build" $true "跳过（-SkipBuild）"
} else {
    $slnPath = Join-Path $root "ThermalTuner.sln"
    $buildRes = Invoke-Capture $dotnet @("build", ('"' + $slnPath + '"'), "-c", "Release", "--nologo")
    $warnLine = ($buildRes.Output -split "`r?`n" | Where-Object { $_ -match "个警告|个错误" } | Select-Object -Last 1)
    Add-Result "build" ($buildRes.Code -eq 0) ("exit=" + $buildRes.Code + "; " + $warnLine)
}

# ---------- 2. Core 功能测试 ----------
Section "2. Core 功能测试（MsrEncoding / TurboRatio / ThermalCalibration / ThermalResistance / DecisionTree / PlatformProfile）"
$testProj = Join-Path $root "tests/ThermalTuner.Core.Tests"
$testRes = Invoke-Capture $dotnet @("run", "--project", ('"' + $testProj + '"'), "-c", "Release", "--no-build")
$testSummary = ($testRes.Output -split "`r?`n" | Where-Object { $_ -match "^套件|^断言|^RESULT" }) -join " / "
Add-Result "core-tests" ($testRes.Code -eq 0 -and $testRes.Output -match "RESULT: PASS") ("exit=" + $testRes.Code + "; " + $testSummary)

# ---------- 3. Runner ----------
Section "3. Runner 环境自适应回路"
$runnerProj = Join-Path $root "src/ThermalTuner.Runner"
$runnerRes = Invoke-Capture $dotnet @("run", "--project", ('"' + $runnerProj + '"'), "-c", "Release", "--no-build")
$runnerKey = ($runnerRes.Output -split "`r?`n" | Where-Object { $_ -match "CPU 品牌|TjMax|PL1 范围|完成" } | Select-Object -First 3) -join " / "
Add-Result "runner" ($runnerRes.Code -eq 0 -and $runnerRes.Output -match "完成") ("exit=" + $runnerRes.Code + "; " + $runnerKey)

# ---------- 4. App 发布 ----------
Section "4. 发布 App（框架依赖，win-x64）"
$appDir = Join-Path $root "artifacts/integration-app"
$appExe = Join-Path $appDir "ThermalTuner.App.exe"
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
$appProj = Join-Path $root "src/ThermalTuner.App/ThermalTuner.App.csproj"
$pubRes = Invoke-Capture $dotnet @("publish", ('"' + $appProj + '"'), "-c", "Release", "-r", "win-x64",
    "--self-contained", "false", "-o", ('"' + $appDir + '"'), "--nologo")
$appOk = ($pubRes.Code -eq 0) -and (Test-Path $appExe)
Add-Result "publish-app" $appOk ("exit=" + $pubRes.Code + "; exe=" + (Test-Path $appExe))

$lhmPath = Join-Path $appDir "LibreHardwareMonitorLib.dll"
if (Test-Path $lhmPath) {
    $lhmVer = [Reflection.AssemblyName]::GetAssemblyName($lhmPath).Version.ToString()
    Add-Result "lhm-version" ($lhmVer -eq "0.9.4.0") ("实际 $lhmVer，要求 0.9.4.0（WinRing0 可写后端）")
} else {
    Add-Result "lhm-version" $false "发布目录缺少 LibreHardwareMonitorLib.dll"
}

# ---------- 5. App 自检 ----------
function Invoke-AppCheck([string]$name, [string[]]$cmdArgs, [string]$reportFile, [int[]]$okCodes, [string]$expect = "RESULT: PASS") {
    if (Test-Path $reportFile) { Remove-Item $reportFile -Force -ErrorAction SilentlyContinue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Native $appExe $cmdArgs $false
    $p.WaitForExit()
    $sw.Stop()
    $code = $p.ExitCode
    $content = ""
    if (Test-Path $reportFile) { $content = Get-Content $reportFile -Raw -Encoding UTF8 }
    $ok = $okCodes -contains $code
    if ($ok -and -not [string]::IsNullOrWhiteSpace($expect) -and $content -notmatch [regex]::Escape($expect)) {
        $ok = $false
    }
    $secs = [int]$sw.Elapsed.TotalSeconds
    Add-Result $name $ok ("exit=" + $code + "; " + $secs + "s; " + (KeyLines $content))
}

Section "5. App 冒烟与只读自检"
$smokeReport = Join-Path $OutDir "app-smoke.txt"
Invoke-AppCheck "app-smoke" @("--smoke", "--out", ('"' + $smokeReport + '"')) $smokeReport @(0) "RESULT: PASS"

$msrReport = Join-Path $OutDir "app-msr-probe.txt"
$msrCodes = @(0, 2)
if ($isAdmin) { $msrCodes = @(0) }
Invoke-AppCheck "app-msr-probe" @("--msr-probe", "--out", ('"' + $msrReport + '"')) $msrReport $msrCodes ""

$raplReport = Join-Path $OutDir "app-rapl-probe.txt"
if ($isAdmin) {
    Invoke-AppCheck "app-rapl-probe" @("--rapl-probe", "--out", ('"' + $raplReport + '"')) $raplReport @(0) ""
} else {
    Add-Result "app-rapl-probe" $true "跳过（需管理员）"
}

# ---------- 6. MSR 写入通道 ----------
Section "6. MSR 写入通道"
if ($Hardware -and $isAdmin) {
    $writeReport = Join-Path $OutDir "app-write-test.txt"
    Invoke-AppCheck "app-write-test" @("--write-test", "--out", ('"' + $writeReport + '"')) $writeReport @(0) "RESULT: PASS"

    $turboReport = Join-Path $OutDir "app-turbo-probe.txt"
    Invoke-AppCheck "app-turbo-probe" @("--turbo-probe", "--out", ('"' + $turboReport + '"')) $turboReport @(0) "RESULT: PASS"
} else {
    Add-Result "app-write-test" $true "跳过（需 -Hardware 且管理员）"
    Add-Result "app-turbo-probe" $true "跳过（需 -Hardware 且管理员）"
}

# ---------- 7. Monitor ----------
Section "7. Monitor 冒烟（合成热模型）"
$monitorExe = Join-Path $root "src/ThermalTuner.Monitor/bin/Release/net8.0-windows/ThermalTuner.Monitor.exe"
if (-not (Test-Path $monitorExe)) {
    Add-Result "monitor" $false "未找到 $monitorExe（先执行构建）"
} else {
    $monitorReport = Join-Path $OutDir "monitor-smoke.txt"
    if (Test-Path $monitorReport) { Remove-Item $monitorReport -Force }
    $mp = Start-Native $monitorExe @("--simulate", "--seconds", "5", "--interval", "500", "--out", ('"' + $monitorReport + '"')) $false
    $mp.WaitForExit()
    $monCode = $mp.ExitCode
    $monContent = ""
    if (Test-Path $monitorReport) { $monContent = Get-Content $monitorReport -Raw -Encoding UTF8 }
    $monKey = ($monContent -split "`r?`n" | Where-Object { $_ -match "汇总|命中分布" } | Select-Object -First 2) -join " / "
    Add-Result "monitor" ($monCode -eq 0 -and $monContent -match "汇总") ("exit=" + $monCode + "; " + $monKey)
}

# ---------- 8. 满载压测 ----------
Section "8. 满载压测（真实写入 0x610 / 0x1AD，结束自动恢复）"
if ($Full -and $Hardware -and $isAdmin) {
    $stressReport = Join-Path $OutDir "stress-test.txt"
    $stressCsv = Join-Path $OutDir "stress-test.csv"
    Invoke-AppCheck "stress-test" `
        @("--stress-test", "--out", ('"' + $stressReport + '"'), "--csv", ('"' + $stressCsv + '"'), "--seconds", "20", "--low-pl1", "15") `
        $stressReport @(0) "RESULT: 压测完成"

    $sweepReport = Join-Path $OutDir "calibrate-sweep.txt"
    $sweepCsv = Join-Path $OutDir "calibrate-sweep.csv"
    Invoke-AppCheck "calibrate-sweep" `
        @("--calibrate-sweep", "--out", ('"' + $sweepReport + '"'), "--csv", ('"' + $sweepCsv + '"'), "--seconds", "15", "--ceiling", "85") `
        $sweepReport @(0) "RESULT: 阶梯标定完成"

    $turboStressReport = Join-Path $OutDir "turbo-stress.txt"
    $turboStressCsv = Join-Path $OutDir "turbo-stress.csv"
    Invoke-AppCheck "turbo-stress" `
        @("--turbo-stress", "--out", ('"' + $turboStressReport + '"'), "--csv", ('"' + $turboStressCsv + '"'), "--seconds", "20") `
        $turboStressReport @(0) "RESULT: PASS"

    $finalReport = Join-Path $OutDir "app-final-msr.txt"
    Invoke-AppCheck "final-msr-probe" @("--msr-probe", "--out", ('"' + $finalReport + '"')) $finalReport @(0) ""
} else {
    Add-Result "stress-test" $true "跳过（需 -Hardware -Full 且管理员）"
    Add-Result "calibrate-sweep" $true "跳过（需 -Hardware -Full 且管理员）"
    Add-Result "turbo-stress" $true "跳过（需 -Hardware -Full 且管理员）"
    Add-Result "final-msr-probe" $true "跳过（需 -Hardware -Full 且管理员）"
}

# ---------- 汇总 ----------
$totalTimer.Stop()
$passCount = $script:lines | Where-Object { $_ -match "^\[PASS\]" } | Measure-Object | Select-Object -ExpandProperty Count
$failCount = $script:failures.Count
$elapsedSecs = [int]$totalTimer.Elapsed.TotalSeconds

Section "汇总"
Log ("通过 : " + $passCount)
Log ("失败 : " + $failCount)
Log ("耗时 : " + $elapsedSecs + " s")
if ($failCount -gt 0) {
    Log "失败项："
    foreach ($f in $script:failures) { Log ("  - " + $f) }
    Log "RESULT: FAIL"
} else {
    Log "RESULT: PASS"
}
Log ("报告 : " + $reportPath)

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllLines($reportPath, $script:lines.ToArray(), $utf8Bom)

if ($failCount -gt 0) { exit 1 }
exit 0
