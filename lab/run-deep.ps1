#Requires -Version 5.1
<#
  run-deep.ps1 -- 19.5 min deep sampling run (real steady states).
  ASCII ONLY on purpose: no BOM needed, so PowerShell 5.1 reads it correctly
  on a cp936 console. All Chinese text lives in plan-deep-v1.json (UTF-8).

  Double-click lab\run-deep.cmd instead of calling this directly: this script
  self-elevates, and the prompt only reaches the desktop when the caller is a
  real interactive shell (Explorer), not a sandboxed agent shell.
#>
$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$Root = Split-Path -Parent $Here
Set-Location $Root

# ---- self-elevate (reading CPU temp / writing powercfg both need admin)
function Test-Admin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}
if (-not (Test-Admin)) {
    Write-Host "Requesting administrator rights -- please click YES in the UAC dialog ..." -ForegroundColor Yellow
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

# ---- locate dotnet
$dotnet = $null
foreach ($c in @((Join-Path $Root "..\.tools\dotnet\dotnet.exe"),
                 (Join-Path $Root ".tools\dotnet\dotnet.exe"))) {
    if (Test-Path $c) { $dotnet = (Resolve-Path $c).Path; break }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Host "[X] dotnet not found. Install .NET 8 SDK or put a portable one in .tools\dotnet\" -ForegroundColor Red
    exit 1
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnet

# ---- locate tplab, build if missing
$cli = Join-Path $Root "src\ThermalPilot.Cli\bin\Release\net8.0-windows\tplab.dll"
if (-not (Test-Path $cli)) {
    Write-Host "[i] building tplab ..." -ForegroundColor Cyan
    & $dotnet build (Join-Path $Root "src\ThermalPilot.Cli\ThermalPilot.Cli.csproj") -c Release --nologo -v q
}
if (-not (Test-Path $cli)) { Write-Host "[X] tplab build failed" -ForegroundColor Red; exit 1 }

$dataDir = Join-Path $Root "lab-data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
$log  = Join-Path $dataDir "run-deep1.log"
$plan = Join-Path $Root "lab\plan-deep-v1.json"

# ---- NBFC fan duty side-channel
# The CSV written by tplab only carries the fan percentage the plan asked for.
# When a stage does not set a fan, firmware / NBFC decides, and we lose the
# trajectory -- but R_th depends on it, so the temperature plateau cannot be
# explained without it. Sample it independently, join later on timestamp.
$nbfc = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
if (-not (Test-Path $nbfc)) {
    $nc = Get-Command nbfc.exe -ErrorAction SilentlyContinue
    if ($nc) { $nbfc = $nc.Source } else { $nbfc = $null }
}

$secs = 0
try {
    # Read as UTF-8 explicitly: PS 5.1 Get-Content would fall back to cp936 and
    # mangle the Chinese stage labels inside the plan file.
    $json = [IO.File]::ReadAllText($plan, [Text.Encoding]::UTF8)
    $p = $json | ConvertFrom-Json
    foreach ($s in $p.stages) { $secs += [int]$s.sec }
} catch { $secs = 1400 }
if ($secs -le 0) { $secs = 1400 }
$secs += 120

$fanJob = $null
if ($nbfc) {
  try {
    $fanCsv = Join-Path $dataDir "fan-deep1.csv"
    $fanJob = Start-Job -ScriptBlock {
        param($nbfcExe, $outPath, $total)
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("timestamp,elapsed_s,fan_duty,fan_target,auto_control,ec_temp_c")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $total) {
            $f = (& $nbfcExe status -f 2>$null | Out-String)
            $s = (& $nbfcExe status -s 2>$null | Out-String)
            $duty = ""; $tgt = ""; $auto = ""; $et = ""
            if ($f -match 'Current fan speed\s*:\s*([\d\.]+)')  { $duty = $Matches[1] }
            if ($f -match 'Target fan speed\s*:\s*([\d\.]+)')   { $tgt  = $Matches[1] }
            if ($f -match 'Auto control enabled\s*:\s*(\w+)')   { $auto = $Matches[1] }
            if ($s -match '(?m)^Temperature\s*:\s*(\d+)')       { $et   = $Matches[1] }
            $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
            $lines.Add("$ts,$([math]::Round($sw.Elapsed.TotalSeconds,1)),$duty,$tgt,$auto,$et")
            Set-Content -Path $outPath -Value $lines -Encoding UTF8
            Start-Sleep -Seconds 5
        }
    } -ArgumentList $nbfc, $fanCsv, $secs
    Write-Host "fan sampler  : $fanCsv  (every 5 s, $secs s)" -ForegroundColor DarkGray
  } catch {
    # A failed side-channel must never cost us the 19.5 min main run.
    Write-Host "fan sampler  : failed to start ($($_.Exception.Message))" -ForegroundColor DarkYellow
    $fanJob = $null
  }
} else {
    Write-Host "fan sampler  : skipped (nbfc.exe not found)" -ForegroundColor DarkYellow
}

Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host " ThermalPilot deep run : 5 stages / 19.5 min / 3 power levels"
Write-Host " plan : $plan"
Write-Host " log  : $log"
Write-Host " Keep the machine idle. Ctrl+C aborts and restores power settings."
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host ""

$rc = 9
try { Start-Transcript -Path $log -Force | Out-Null } catch { }
try {
    & $dotnet $cli lab --plan $plan --out $dataDir --tag deep1
    $rc = $LASTEXITCODE
} catch {
    Write-Host "[X] $_" -ForegroundColor Red
} finally {
    if ($fanJob) { Stop-Job $fanJob -ErrorAction SilentlyContinue; Remove-Job $fanJob -Force -ErrorAction SilentlyContinue }
    try { Stop-Transcript | Out-Null } catch { }
}

Write-Host ""
Write-Host "=== sampling finished, exit code $rc ===" -ForegroundColor Green
Write-Host "Log: $log"
Write-Host "This window can be closed."
Start-Sleep -Seconds 3
