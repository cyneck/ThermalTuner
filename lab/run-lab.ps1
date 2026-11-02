#Requires -Version 5.1
<#
.SYNOPSIS
  ThermalPilot 实验台 —— 一键：压测 + 采集 + 分析 + 建模

.DESCRIPTION
  run-lab.cmd quick3          3 分钟快测（瞬态外推；只看趋势，不建控制表）
  run-lab.cmd quick5          5 分钟快测（推荐先跑这个；离线验证误差 ~1℃）
  run-lab.cmd quick10         10 分钟快测（三功率层级，可检验线性/二次项）
  run-lab.cmd                 BIOS 固件风扇策略，等稳态（cap 阶梯，8 阶段 ≈18 分钟）
  run-lab.cmd nbfc            装了 NBFC 后跑两因子设计（cap x fan 3x3，11 阶段 ≈30 分钟）
  run-lab.cmd -DryRun         只做环境自检并打印将要执行的命令，不压测

  快测与全量的区别：
    全量 = 每档等到稳态再测（每档 ≥3τ），数据直接可用，但耗时；
    快测 = 不等稳态，靠升降温轨迹外推出稳态温度（tools/lab_transient.py），
           3 分钟只够看趋势，5 分钟能给出 ±1℃ 的稳态估计，10 分钟才有三个功率层级。

  必须管理员权限（读 CPU 温度、写 powercfg / EC 都需要）。
  中途按 Ctrl+C 中止，tplab 会在 finally 中还原电源与风扇。

.NOTES
  主体用 PowerShell 而不是批处理：批处理在 UTF-8 中文下会被 cmd 解析器拆行，
  run-lab.cmd 只作 ASCII 垫片。
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Mode = "",

    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
# 不主动改 [Console]::OutputEncoding：让 PowerShell 用控制台自身的代码页（中文系统 936），
# 强制 UTF-8 反而会让 cp936 的控制台显示乱码。

$Root = Split-Path -Parent $PSScriptRoot
$DataDir = Join-Path $Root "lab-data"

function Write-Step([string]$Text) { Write-Host $Text -ForegroundColor Cyan }
function Write-Warn([string]$Text) { Write-Host $Text -ForegroundColor Yellow }
function Write-Err([string]$Text) { Write-Host $Text -ForegroundColor Red }

function Test-Admin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# ---------------- 0. 提权（干跑不需要） ----------------
if (-not $DryRun -and -not (Test-Admin)) {
    Write-Step "正在申请管理员权限，请在弹窗中点是..."
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($Mode) { $argList += $Mode }
    if ($Yes) { $argList += "-Yes" }
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

# ---------------- 1. 找 dotnet ----------------
$dotnet = $null
if ($env:THERMALPILOT_DOTNET -and (Test-Path $env:THERMALPILOT_DOTNET)) {
    $dotnet = $env:THERMALPILOT_DOTNET
}
if (-not $dotnet) {
    foreach ($c in @(
            (Join-Path $Root "..\.tools\dotnet\dotnet.exe"),
            (Join-Path $Root ".tools\dotnet\dotnet.exe"))) {
        if (Test-Path $c) { $dotnet = (Resolve-Path $c).Path; break }
    }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Err "[X] 没找到 dotnet。请安装 .NET 8 SDK，或把便携运行时放到 .tools\dotnet\"
    Write-Host "    也可以设环境变量 THERMALPILOT_DOTNET 指向 dotnet.exe"
    exit 1
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnet

# ---------------- 2. 找 tplab，没有就编译 ----------------
$cliDll = Join-Path $Root "src\ThermalPilot.Cli\bin\Release\net8.0-windows\tplab.dll"
if (-not (Test-Path $cliDll)) {
    Write-Step "== 还没编译实验台，正在编译（首次约 1-2 分钟）=="
    & $dotnet build (Join-Path $Root "src\ThermalPilot.Cli\ThermalPilot.Cli.csproj") -c Release --nologo -v q
    if (-not (Test-Path $cliDll)) {
        Write-Err "[X] 编译失败，请手动执行："
        Write-Host "    `"$dotnet`" build `"$(Join-Path $Root 'ThermalPilot.sln')`" -c Release"
        exit 1
    }
}

# ---------------- 3. 选实验计划 ----------------
# 两种节奏：
#   full  —— 等到稳态再测（每档 ≥3τ），数据直接可用，但耗时 18~30 分钟
#   quick —— 不等稳态（3/5/10 分钟），靠升降温轨迹外推稳态（tools/lab_transient.py）
switch ($Mode) {
    "nbfc" {
        $plan = Join-Path $Root "lab\plan-nbfc-v1.json"
        $tag = "nbfc1"; $kind = "full"
        $modeText = "NBFC 可控风扇（cap x fan 全因子 3x3）"
    }
    "quick3" {
        $plan = Join-Path $Root "lab\plan-quick3.json"
        $tag = "q3"; $kind = "quick"
        $modeText = "3 分钟快测（瞬态外推；只看趋势，不建控制表）"
    }
    "quick5" {
        $plan = Join-Path $Root "lab\plan-quick5.json"
        $tag = "q5"; $kind = "quick"
        $modeText = "5 分钟快测（瞬态外推；离线验证误差 ~1℃）"
    }
    "quick10" {
        $plan = Join-Path $Root "lab\plan-quick10.json"
        $tag = "q10"; $kind = "quick"
        $modeText = "10 分钟快测（三功率层级，线性性可检验）"
    }
    default {
        $plan = Join-Path $Root "lab\plan-doe-v1.json"
        $tag = "doe1"; $kind = "full"
        $modeText = "BIOS 固件风扇策略（cap 阶梯，等稳态）"
    }
}
if (-not (Test-Path $plan)) { Write-Err "[X] 找不到实验计划：$plan"; exit 1 }

if ($Mode -eq "nbfc") {
    $nbfc = Get-Command nbfc -ErrorAction SilentlyContinue
    if (-not $nbfc) {
        # NBFC 的 CLI 不提供 RPM 回读，只有占空比；语法是 status -f / -a，没有 -n。
        $known = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
        if (Test-Path $known) { $nbfc = Get-Item $known }
    }
    if (-not $nbfc) {
        Write-Err "[X] 没找到 nbfc 命令。请先装 NoteBook FanControl 1.6.3，"
        Write-Host "    并在配置里选 `"HP OMEN Notebook PC 15`"，确认能跑：nbfc status -f"
        exit 1
    }
    $svc = & $nbfc status -s 2>&1 | Out-String
    if ($svc -match "Service enabled\s*:\s*False") {
        Write-Warn "[!] NBFC 服务未启用，风扇不会被接管。请先执行（管理员）："
        Write-Host "    nbfc config -a `"HP OMEN Notebook PC 15`""
    }
}

$labArgs = @($cliDll, "lab", "--plan", $plan, "--out", $DataDir, "--tag", $tag)

# ---------------- 4. 环境自检 ----------------
$py = $null
foreach ($cand in @($env:THERMALPILOT_PYTHON)) {
    if ($cand -and (Test-Path $cand)) { $py = $cand; break }
}
if (-not $py) {
    $pyDirs = @()
    $pyDirs += Get-ChildItem (Join-Path $env:LOCALAPPDATA "Programs\Python") -Directory -Filter "Python3*" -ErrorAction SilentlyContinue
    $pyDirs += Get-ChildItem (Join-Path $env:USERPROFILE ".workbuddy\binaries\python\versions") -Directory -ErrorAction SilentlyContinue
    foreach ($d in $pyDirs) {
        $exe = Join-Path $d.FullName "python.exe"
        if (Test-Path $exe) { $py = $exe; break }
    }
}
if (-not $py) {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd) { $py = $cmd.Source }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host " 实验模式 : $modeText"
Write-Host " 计划文件 : $plan"
Write-Host " 数据目录 : $DataDir"
Write-Host " dotnet   : $dotnet"
if ($py) { Write-Host " python   : $py" } else { Write-Host " python   : (未找到，跑完不会自动分析)" -ForegroundColor Yellow }
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host " 提示：跑之前关掉游戏/渲染/下载，插上电源，别盖盖子。"
Write-Host "       中途可按 Ctrl+C，程序会尽量还原电源与风扇。"
Write-Host ""

if ($DryRun) {
    Write-Step "== 干跑模式：不动硬件，只打印将要执行的命令 =="
    Write-Host ("    `"$dotnet`" " + (($labArgs | ForEach-Object { "`"$_`"" }) -join " "))
    if ($py) {
        if ($kind -eq "quick") {
            Write-Host ("    `"$py`" `"$(Join-Path $Root 'tools\lab_transient.py')`" `"$DataDir`"")
            Write-Host "    （短时模式到此为止：策略由 lab_transient 直接导出为 model-$tag-*-transient.json）"
        } else {
            Write-Host ("    `"$py`" `"$(Join-Path $Root 'tools\lab_analyze.py')`" `"$DataDir`"")
            Write-Host ("    `"$py`" `"$(Join-Path $Root 'tools\lab_doe.py')`" `"$DataDir`"")
            Write-Host ("    `"$py`" `"$(Join-Path $Root 'tools\lab_model.py')`" `"$DataDir`"")
        }
    }
    exit 0
}

if (-not $Yes) {
    $ans = Read-Host "确认开始？(Y/n)"
    if ($ans -and $ans.Trim().ToLower() -ne "y") { Write-Host "已取消。"; exit 0 }
}

# ---------------- 5. 跑实验 ----------------
$a = @("lab", "--plan", $plan, "--out", $DataDir, "--tag", $tag)
& $dotnet $cliDll @a
$rc = $LASTEXITCODE
Write-Host ""
if ($rc -ne 0) { Write-Warn "[!] 实验返回码 $rc，数据可能不完整，下面仍会尝试分析。" }

# ---------------- 6. 分析 + 可辨识性 + 建模 ----------------
# 顺序有讲究：先看数据质量(analyze) → 再看这套数据够不够辨识(doe) → 最后才建模出策略(model)。
# doe 的退出码 0/2/4 分别表示「建议继续跑 / 可以停 / 先收紧策略」，都是正常结论，不是失败。
if ($py) {
    if ($kind -eq "quick") {
        # 短时模式：数据里没有稳态段，必须先外推，否则 analyze/model 会把每段都判成"未稳态"而全部丢弃。
        Write-Step "== 瞬态辨识（从升降温轨迹外推稳态）=="
        & $py (Join-Path $Root "tools\lab_transient.py") $DataDir
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "   → 外推有告警（见上）：这个时长偏短，或噪声偏大。"
        }
        # 不走 lab_model：它要求 ≥4 个满载阶段做留一交叉验证，短时计划只有 2~3 个，
        # 必然被拒。lab_transient 已经自己导出了阶梯表策略（model-*-transient.json）。
        Write-Host "   跳过 lab_model / lab_doe：前者要 ≥4 个满载档做交叉验证，后者要真实稳态段。" -ForegroundColor DarkGray
        Write-Host "   短时的策略已由 lab_transient 直接导出（阶梯表，仅实测 cap 档位）。" -ForegroundColor DarkGray
    } else {
        Write-Step "== 分析热模型 =="
        & $py (Join-Path $Root "tools\lab_analyze.py") $DataDir
        Write-Step "== 可辨识性预判（这套数据够不够用）=="
        & $py (Join-Path $Root "tools\lab_doe.py") $DataDir
        $doeRc = $LASTEXITCODE
        if ($doeRc -eq 0) {
            Write-Host "   → 建议再追加采集（详见上面的「下一档推荐」）。" -ForegroundColor Cyan
        } elseif ($doeRc -eq 4) {
            Write-Host "   → 建议先收紧策略（当前参数有一定超温概率），详见报告。" -ForegroundColor Yellow
        } elseif ($doeRc -eq 2) {
            Write-Host "   → 数据已够，可以停止采集。" -ForegroundColor Green
        }
        Write-Step "== 建模与策略导出 =="
        & $py (Join-Path $Root "tools\lab_model.py") $DataDir
    }
} else {
    Write-Warn "跳过自动分析。数据已落在 $DataDir，之后可手动跑："
    Write-Host "    python tools\lab_transient.py lab-data   # 短时数据先跑这个"
    Write-Host "    python tools\lab_analyze.py  lab-data"
    Write-Host "    python tools\lab_doe.py      lab-data"
    Write-Host "    python tools\lab_model.py    lab-data"
}

# ---------------- 7. 产出清单 + 打开报告 ----------------
Write-Host ""
Write-Step "== 产出清单（lab-data）=="
Get-ChildItem $DataDir -File -Include *.csv, *.html, *.json -ErrorAction SilentlyContinue |
    Select-Object Name, @{n = "KB"; e = { [math]::Round($_.Length / 1KB, 0) } } |
    Format-Table -AutoSize | Out-String | Write-Host

# 报告文件名带时间戳，用通配符取最新一份，不要写死名字
foreach ($pat in @("transient-$tag*.html", "report-$tag*.html",
                   "doe-$tag*.html", "model-$tag*.html")) {
    $f = Get-ChildItem $DataDir -Filter $pat -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { try { Start-Process $f.FullName } catch { } }
}

# 策略文件带时间戳：全量是 model-<tag>.json，短时是 model-<tag>-<stamp>-transient.json
$modelJson = Get-ChildItem $DataDir -Filter "model-$tag*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($modelJson) {
    Write-Step "== 验证策略模型可被程序读取 =="
    & $dotnet $cliDll model --file $modelJson.FullName --list
}

Write-Host ""
Write-Host "完成。下一步：打开 ThermalPilot → 高级调参 → ④ 实测策略模型 → 「用最新结果」。" -ForegroundColor Green
if (-not $Yes) { Write-Host "按回车退出（数据不会丢）。"; Read-Host | Out-Null }
