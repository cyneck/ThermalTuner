#Requires -Version 5.1
<#
  run-plan.ps1 -- generic elevated runner for a lab plan + NBFC duty side-channel.
  ASCII ONLY: no BOM needed, so PS 5.1 reads it correctly on a cp936 console.

  Usage (double-click shims pass parameters; double-clicking a .ps1 cannot):
      powershell -ExecutionPolicy Bypass -File lab\run-plan.ps1 -Plan lab\plan-fanprobe-v1.json -Tag fan1
#>
param(
    [string]$Plan = "lab\plan-fanprobe-v1.json",
    [string]$Tag  = "fan1"
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$Root = Split-Path -Parent $Here
Set-Location $Root

function Test-Admin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}
if (-not (Test-Admin)) {
    Write-Host "Requesting administrator rights -- please click YES in the UAC dialog ..." -ForegroundColor Yellow
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
                 "-Plan", "`"$Plan`"", "-Tag", "`"$Tag`"")
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

# ---- dotnet
$dotnet = $null
foreach ($c in @((Join-Path $Root "..\.tools\dotnet\dotnet.exe"),
                 (Join-Path $Root ".tools\dotnet\dotnet.exe"))) {
    if (Test-Path $c) { $dotnet = (Resolve-Path $c).Path; break }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) { Write-Host "[X] dotnet not found" -ForegroundColor Red; exit 1 }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet

$planPath = if (Test-Path $Plan) { (Resolve-Path $Plan).Path } else { Join-Path $Root $Plan }
if (-not (Test-Path $planPath)) { Write-Host "[X] plan not found: $planPath" -ForegroundColor Red; exit 1 }

$cli = Join-Path $Root "src\ThermalPilot.Cli\bin\Release\net8.0-windows\tplab.dll"
if (-not (Test-Path $cli)) {
    Write-Host "[i] building tplab ..." -ForegroundColor Cyan
    & $dotnet build (Join-Path $Root "src\ThermalPilot.Cli\ThermalPilot.Cli.csproj") -c Release --nologo -v q
}
if (-not (Test-Path $cli)) { Write-Host "[X] tplab build failed" -ForegroundColor Red; exit 1 }

$dataDir = Join-Path $Root "lab-data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
$log = Join-Path $dataDir ("run-" + $Tag + ".log")

# ---- make sure NBFC is actually controlling, otherwise every "fan" stage is a no-op
$nbfc = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
if (-not (Test-Path $nbfc)) {
    $nc = Get-Command nbfc.exe -ErrorAction SilentlyContinue
    if ($nc) { $nbfc = $nc.Source } else { $nbfc = $null }
}
if ($nbfc) {
    $st = (& $nbfc status -s 2>&1 | Out-String)
    $enabled = $st -match 'Service enabled\s*:\s*True'
    $cfg = ""
    if ($st -match 'Selected config name\s*:\s*(.+)') { $cfg = $Matches[1].Trim() }
    Write-Host ("NBFC: enabled=$enabled config='$cfg'") -ForegroundColor DarkGray
    if (-not $enabled) {
        Write-Host "[i] starting NBFC service ..." -ForegroundColor Cyan
        & $nbfc start 2>&1 | Out-String | Write-Host
        Start-Sleep -Seconds 3
        $st = (& $nbfc status -s 2>&1 | Out-String)
        if (-not ($st -match 'Service enabled\s*:\s*True')) {
            Write-Host "[!] NBFC service still disabled -- fan stages will not take effect." -ForegroundColor Yellow
            Write-Host "    Fix: run  nbfc config -a `"HP OMEN Notebook PC 15`"  then  nbfc start" -ForegroundColor Yellow
        }
    } elseif ($cfg -eq "") {
        Write-Host "[i] selecting config 'HP OMEN Notebook PC 15' ..." -ForegroundColor Cyan
        & $nbfc config -a "HP OMEN Notebook PC 15" 2>&1 | Out-String | Write-Host
    }
    & $nbfc set -a 2>&1 | Out-Null    # 从自动控制开始，别继承上次的手动档位
}

# ---- 计划里写了风扇档位时，NBFC 必须真的在线；否则 9.5 分钟全是白跑
$needFan = $false
try { $needFan = ([IO.File]::ReadAllText($planPath, [Text.Encoding]::UTF8) -match '"fan"\s*:') } catch { }
if ($needFan) {
    if (-not $nbfc) {
        Write-Host "[X] plan sets fan speeds but nbfc.exe was not found." -ForegroundColor Red
        Read-Host "Press Enter to exit"; exit 1
    }
    $chk = (& $nbfc status -s 2>&1 | Out-String)
    if (-not ($chk -match 'Service enabled\s*:\s*True')) {
        Write-Host "[X] plan sets fan speeds but the NBFC service is NOT enabled." -ForegroundColor Red
        Write-Host "    Enable it once as admin, then re-run:" -ForegroundColor Yellow
        Write-Host '      nbfc config -a "HP OMEN Notebook PC 15"' -ForegroundColor Yellow
        Write-Host "      nbfc start" -ForegroundColor Yellow
        Read-Host "Press Enter to exit"; exit 1
    }
    Write-Host "NBFC control verified -- fan stages will really be enforced." -ForegroundColor Green
}

# ---- fan duty side-channel (5 s cadence, appended so a crash keeps the prefix)
$secs = 600
try {
    $json = [IO.File]::ReadAllText($planPath, [Text.Encoding]::UTF8)
    $p = $json | ConvertFrom-Json
    $sum = 0; foreach ($s in $p.stages) { $sum += [int]$s.sec }
    if ($sum -gt 0) { $secs = $sum + 60 }
} catch { }

$fanCsv = Join-Path $dataDir ("fan-" + $Tag + ".csv")
"timestamp,elapsed_s,fan_duty,fan_target,auto_control,steps,ec_temp_c" | Set-Content -Path $fanCsv -Encoding UTF8

$fanJob = $null
if ($nbfc) {
    try {
        $fanJob = Start-Job -ScriptBlock {
            param($nbfcExe, $outPath, $total)
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt $total) {
                $f = ""; $s = ""
                try { $f = (& $nbfcExe status -f 2>$null | Out-String) } catch { }
                try { $s = (& $nbfcExe status -s 2>$null | Out-String) } catch { }
                $duty = ""; $tgt = ""; $auto = ""; $steps = ""; $et = ""
                if ($f -match 'Current fan speed\s*:\s*([\d\.]+)') { $duty = $Matches[1] }
                if ($f -match 'Target fan speed\s*:\s*([\d\.]+)')  { $tgt  = $Matches[1] }
                if ($f -match 'Auto control enabled\s*:\s*(\w+)')  { $auto = $Matches[1] }
                if ($f -match 'Fan speed steps\s*:\s*(\d+)')       { $steps = $Matches[1] }
                if ($s -match '(?m)^Temperature\s*:\s*(\d+)')      { $et   = $Matches[1] }
                $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
                $line = "$ts,$([math]::Round($sw.Elapsed.TotalSeconds,1)),$duty,$tgt,$auto,$steps,$et"
                Add-Content -Path $outPath -Value $line -Encoding UTF8
                Start-Sleep -Seconds 5
            }
        } -ArgumentList $nbfc, $fanCsv, $secs
        Write-Host "fan sampler  : $fanCsv  (every 5 s)" -ForegroundColor DarkGray
    } catch {
        Write-Host "fan sampler  : failed ($($_.Exception.Message))" -ForegroundColor DarkYellow
        $fanJob = $null
    }
}

Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host " plan : $planPath"
Write-Host " tag  : $Tag"
Write-Host " log  : $log"
Write-Host " Keep the machine idle. Ctrl+C aborts and restores power settings."
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host ""

$rc = 9
try { Start-Transcript -Path $log -Force | Out-Null } catch { }
try {
    & $dotnet $cli lab --plan $planPath --out $dataDir --tag $Tag
    $rc = $LASTEXITCODE
} catch {
    Write-Host "[X] $_" -ForegroundColor Red
} finally {
    if ($fanJob) { Stop-Job $fanJob -ErrorAction SilentlyContinue; Remove-Job $fanJob -Force -ErrorAction SilentlyContinue }
    try { Stop-Transcript | Out-Null } catch { }
}

Write-Host ""
Write-Host "=== finished, exit code $rc ===" -ForegroundColor Green
Write-Host "This window can be closed."
Start-Sleep -Seconds 3
