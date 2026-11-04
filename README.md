# ThermalPilot（ThermalTuner）

> Windows 笔记本 CPU 热与功耗调优工具：读懂温度与真实功耗，用决策树给出功耗策略，可选写入 MSR 寄存器。
> 当前版本 **v0.4.3** ｜ 验收机 Intel Core i7-7700HQ / 4C8T / Windows 10 19045

---

## 目录

- [1. 这是什么](#1-这是什么)
- [2. 能力边界（先看这张表）](#2-能力边界先看这张表)
- [3. 版本状态与实测结论](#3-版本状态与实测结论)
- [4. 快速开始](#4-快速开始)
- [5. 界面](#5-界面)
- [6. 工作原理](#6-工作原理)
- [7. 自检与压测命令](#7-自检与压测命令)
- [8. 测试](#8-测试)
- [9. 目录结构](#9-目录结构)
- [10. 持久化与恢复语义](#10-持久化与恢复语义)
- [11. 已知限制与排障](#11-已知限制与排障)
- [12. 构建与发布](#12-构建与发布)
- [13. 文档索引](#13-文档索引)

---

## 1. 这是什么

ThermalPilot 是一个桌面端（WPF / .NET 8）CPU 热管理工具。它做三件事：

1. **看见**：读取 CPU 封装温度、核心频率、负载，并通过 RAPL 能量计数器（`0x611`）算出**真实包功耗**，而不是用负载估算。
2. **判断**：把物理约束、用户偏好、在线热模型拆成四象限参数，交给五节点决策树短路求值，输出带因果凭证的 PL1 / PL2 / Tau 建议。
3. **可选执行**：在管理员权限 + 用户显式授权下，把决策结果写入 MSR `0x610`（功耗墙）、`0x774`（EPP）、`0x1AD`（睿频倍率表），写入前后做备份、回读校验与自动回滚。

典型用途：**在散热吃紧的机器上主动设定功耗上限，用可控的性能损失换取温度与噪音下降**；以及观察 CPU 热行为、做稳态热阻标定。

## 2. 能力边界（先看这张表）

| 对象 | 是否控制 | 说明 |
| :--- | :--- | :--- |
| MSR `0x610` PL1 / PL2 / Tau | ✅ 真实写入 | 唯一被证明有效的调节路径 |
| MSR `0x774` EPP | ✅ 可写（机型需可读） | 能效偏好；本机 `0x774` 可读 |
| MSR `0x1AD` 睿频倍率表 | ✅ 可写 | 仅限 `[8, 出厂单核上限]`，不做超频；**本机实测无性能收益，见 §3** |
| MSR `0x606` / `0x611` RAPL | 只读 | 能量单位与能量计数器，用于计算真实包功耗 |
| 风扇 / EC / PWM | ❌ 不控制 | 决策树里的 `TargetFanPercent` 只是动作字段，从未落到硬件；风扇由 EC 按温度曲线自动调速 |
| 电源计划 / 电压 / 基频 | ❌ 不控制 | `0x150` FIVR 电压偏移未接入；不写电源计划 |

> 软件只能通过**降低发热**间接让风扇降速，不能直接拨风扇。这是设计选择，不是缺陷。

## 3. 版本状态与实测结论

| 项目 | 事实 |
| :--- | :--- |
| 决策树 | `ThermalDecisionEngine` 五节点短路求值，每拍由 `SessionState` 调用，非死代码 |
| 驱动通道 | 固定 `LibreHardwareMonitorLib 0.9.4`（WinRing0），`Ring0.ReadMsr/WriteMsr` 可用 |
| 默认闸门 | 决策树始终在算，但写入需 `AllowRealWrite && AutoTakeover` 且未被手动微调锁住 |
| 已证明有效 | 把 PL1 压到**实际满载功耗之下**：45W → 15W 实测 **−7.10°C / −9.51W / −0.58GHz** |
| 已证明无效 | 放宽 PL1（实际功耗只有 25W，天花板之上物理等价）；0x1AD 拉平 38x（核频 3.41→3.41GHz，只多 +2.04W / +1.11°C） |

> ⚠️ **不要升级 LHM 到 0.9.6+**：0.9.6 起驱动后端换成 PawnIO，随包 IntelMSR 模块只有
> `ioctl_read_msr`（只读），升级会**静默丢失** `0x610` 写入能力。升级前必须确认新版本提供写接口并跑 `--write-test`。

本机出厂寄存器实读（i7-7700HQ）：

```
0x610 = PL1 45.0W / PL2 56.2W / Tau 28672s(≈8h) / lock=false
0x1AD = 0x22232426 → 1C=38 / 2C=36 / 3C=35 / 4C=34（单核 3.8GHz / 全核 3.4GHz）
```

产品口径：固件里的值是**出厂默认，掉电即恢复、不可持久化修改**；可持久化的是软件侧实测标定值与手动微调值，存 `%LOCALAPPDATA%\ThermalPilot\settings.json`，开机由软件重放写入寄存器；软件不运行就按固件默认散热逻辑运转。

## 4. 快速开始

### 4.1 直接用发布包

| 包 | 适用 |
| :--- | :--- |
| `dist/ThermalPilot/`（自包含目录版） | 免装 .NET 运行时，解压即用 |
| `dist/ThermalPilot-win-x64.zip` | 同上，压缩包 |
| `dist/ThermalPilot-Stress-v0.4.3.zip` | 带全部自检/压测命令的便携包（框架依赖，需 .NET 8 桌面运行时） |

1. 解压后右键 `ThermalPilot.App.exe` → **以管理员身份运行**（读 MSR / RAPL 必需）。
2. 首次启动按向导确认档位与安全说明。
3. 想真正写入寄存器：**设置页**打开「允许写入 MSR」，再按需打开「自动接管」。
4. 只观察不写入：保持两个开关关闭，界面照常显示决策与预测，但寄存器不变。

### 4.2 从源码运行

```bash
dotnet build ThermalTuner.sln                  # 0 warning / 0 error
dotnet run --project src/ThermalPilot.App      # 启动图形界面
dotnet run --project src/ThermalPilot.Runner   # 环境探测 + 单测 + 模拟回路（无需硬件）
dotnet run --project src/ThermalPilot.Monitor -- --simulate --seconds 30
```

要求：Windows 10 1809+ / Windows 11，x64；.NET 8 SDK（构建）或 .NET 8 Desktop Runtime（运行框架依赖版）。读取 MSR / RAPL 需管理员权限。

## 5. 界面

| 页面 | 内容 |
| :--- | :--- |
| **概览** | 指标卡（温度 / 功耗 / 频率 / PL1）、温度与功耗曲线、最新决策凭证、决策流水 |
| **智能调优** | 3 档模式（静音 / 平衡 / 性能）、温度天花板滑块、在线热阻与预测稳态、两段式稳态标定、标定前馈上限 |
| **实时监控** | 连续采样大图、明细表、压测开关、CSV 导出；「PL1 实效」列为寄存器实读 |
| **高级参数** | 固件出厂默认 / 已保存软件优化值双锚点；PL1 / PL2 / Tau / EPP / 睿频倍率；「当前生效」列每拍实读 MSR |
| **诊断与日志** | 环境体检、传感器清单、事件中心、MSR 审计日志入口 |
| **设置** | 采样间隔、允许写入 MSR、自动接管、开机重放、退出恢复、开机自启、托盘行为 |

设计稿：`design/ThermalPilot-UI.html`。

## 6. 工作原理

```
 HardwareProbe ──► HardwareLimitResolver ──► HardwareLimits（TjMax / PL 范围）
                                                     │
 每拍遥测 ──► ThermalSnapshot ──► ThermalDecisionEngine（五节点短路）
 （温度/功耗/频率/负载）              │
                                     ├─ 1 SafetyWatchdog       PROCHOT 或 ≥TjMax−3°C → 紧急降功耗
                                     ├─ 2 LightLoadFilter      轻载 → 锚定固件 PL1（防棘轮）
                                     ├─ 3 CeilingExceeded      超用户天花板 → 阶梯降功耗
                                     ├─ 4 ThermalPredictive    预测稳态将超 → 提前锁功耗
                                     └─ 5 BottleneckArbiter    有安全余量 → 升功耗 / 频率封顶 → 维持
                                                     │
                                        DecisionReceipt（规则 / 因果 / 路径）
                                                     │
                          AutoCpuController（标定前馈上限钳制）──► 决策 DTO
                                                     │
                             TryAutoApply 闸门（手动锁 / 双开关 / 节流 / 熔断）
                                                     │
                                   MsrGuard（备份 → 写 → 回读校验 → 失败回滚）
```

关键设计：

- **唯一预测入口**：`ThermalCalibration.PredictSteady` / `MaxPowerForCeiling`，标定报告、界面预测、决策树共用同一套公式。
- **标定前馈**：有实测热阻时，每拍反解「不越过天花板的最大 PL1」作为硬上界；未标定时不干预，避免拿不可信模型误钳。
- **写入护栏**：写前备份原始值，写后回读校验，不一致自动恢复；退出时可恢复固件默认；异常退出下次启动提示并恢复 pending。
- **手动优先**：保存手动微调会置 `_manualLock` 并关闭自动接管，防止决策树每拍覆盖用户设定。

架构细节（四象限参数、节点实现、凭证字段、平台推导表）见 **[`README_REFACTOR.md`](README_REFACTOR.md)**。

## 7. 自检与压测命令

全部支持 `--out <file>`，压测类另支持 `--csv <file>`。涉及 MSR/RAPL 的命令**需管理员**。

| 命令 | 作用 | 写硬件 |
| :--- | :--- | :--- |
| `--smoke` | 构造会话、逐页加载布局，输出 `RESULT: PASS/FAIL` | 否 |
| `--shots <dir>` | 离屏渲染各页 PNG，供设计走查 | 否 |
| `--msr-probe` | 通道状态、0x610 解码、0x774/0x1AD 可读性 | 否 |
| `--msr-funcs` | 列出 Ring0 后端可用接口 | 否 |
| `--rapl-probe` | RAPL 能量单位与包功耗采样 | 否 |
| `--write-test` | **写入→回读→恢复闭环**（通道验收第一命令） | 同值写回 + 恢复 |
| `--turbo-probe` | 0x1AD 档位 / 锁位 / 38x 写入回读恢复 | 临时写入 + 恢复 |
| `--calibrate-test` | 两段式稳态热阻标定自检 | 临时写入 + 恢复 |
| `--calibrate-sweep --seconds 25 --ceiling 85` | 多档 PL1 阶梯标定，拟合 T(P) 并反解推荐值 | 临时写入 + 恢复 |
| `--stress-test --seconds 40 --low-pl1 15` | A/B/C 满载压测：固件默认 / 降墙 / 放宽 | 临时写入 + 恢复 |
| `--turbo-stress --seconds 30` | 睿频 A/B 满载实测 + HWP/PROCHOT 诊断 | 临时写入 + 恢复 |

示例：

```powershell
# 管理员 PowerShell
.\ThermalPilot.App.exe --write-test --out write-test.txt
.\ThermalPilot.App.exe --stress-test --out stress.txt --csv stress.csv --seconds 40 --low-pl1 15
```

压测结束会恢复固件默认值并释放压测线程；报告里会打印最终 `0x610` 实读供复核。

## 8. 测试

### 8.1 单元 / 功能测试（无需硬件）

```bash
dotnet run --project tests/ThermalPilot.Core.Tests -c Release
```

覆盖模块：

| 套件 | 覆盖 |
| :--- | :--- |
| `MsrEncodingTests` | 0x610 位域编解码、Tau 编码、保留位、越界钳制、已知原始值 |
| `TurboRatioLimitTests` | 0x1AD 八档解码、封顶 / 拉平、零档不臆造、非法值过滤 |
| `ThermalCalibrationTests` | 两点差商、门槛拒绝、统一预测入口、反解上限、前馈钳制 |
| `ThermalResistanceTests` | 在线最小二乘、样本/跨度门槛、标定值 ±15% 带内微调、重置 |
| `DecisionTreeTests` | 五节点因果、防棘轮、超温负反馈、频率封顶、参数防呆 |
| `PlatformProfileTests` | 平台探测推导、虚拟化收紧、档位参数、控制器边界钳制 |

期望输出：`套件 6/6 通过`、`断言 通过 173 / 失败 0`、`RESULT: PASS`，退出码 0。原始输出见 `deliverables/core-tests-v0.4.3.txt`。

### 8.2 集成测试

```powershell
# 轻量：构建 + 功能测试 + Runner + Monitor 冒烟 + App 冒烟（非提权可跑）
powershell -ExecutionPolicy Bypass -File build/test.ps1

# 含硬件通道（需管理员）：再加 msr-probe / rapl-probe / write-test / turbo-probe
powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware

# 含满载压测（约 5 分钟，风扇会转）：stress-test / calibrate-sweep / turbo-stress
powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware -Full

# 当前不是管理员时自动请求提权
powershell -ExecutionPolicy Bypass -File build/test.ps1 -Hardware -Full -Elevate
```

报告落 `artifacts/test-runs/`，包含每个用例的退出码与关键输出行；任一步失败脚本返回非零。
可用 `-SkipBuild` 复用已有 Release 产物。最近一次完整测试：**15/15 通过、323 秒**（2026-11-04，管理员 + 三轮满载压测）。

最近一次完整测试结果见 `deliverables/集成与功能测试报告-v0.4.3.md`。

### 8.3 实时监控工具

```bash
dotnet run --project src/ThermalPilot.Monitor -- --simulate --stress --seconds 60
ThermalPilot.Monitor.exe --seconds 120 --profile performance
ThermalPilot.Monitor.exe --diag
```

参数：`--profile silent|balanced|performance`、`--interval <ms>`、`--out <file>`、`--simulate`、`--stress`、`--diag`。

## 9. 目录结构

```
ThermalTuner/
├─ src/
│  ├─ ThermalPilot.Core/          纯逻辑：决策树、热模型、MSR 编解码、平台探测（无 UI / 无硬件依赖）
│  ├─ ThermalPilot.App/           WPF 主程序：会话状态、MSR 通道与护栏、六页界面、自检命令
│  ├─ ThermalPilot.Monitor/       控制台实时监控 / 决策流水（诊断用）
│  └─ ThermalPilot.Runner/        无界面演示：环境探测 → 约束推导 → 单测 → 模拟回路
├─ tests/ThermalPilot.Core.Tests/ 独立测试入口（零外部测试框架依赖）
├─ build/                         publish.ps1 / test.ps1 / Inno Setup 脚本
├─ docs/PRD.md                    需求与验收基线（v0.3.0 原文冻结）
├─ deliverables/                  实测报告、判定口径、审查记录
├─ design/                        UI 设计稿
└─ README_REFACTOR.md             决策树重构架构说明
```

## 10. 持久化与恢复语义

数据目录：`%LOCALAPPDATA%\ThermalPilot\`

| 文件 | 内容 |
| :--- | :--- |
| `settings.json` | 采样间隔、档位、双开关、`Calibration`（实测热模型）、`Manual`（已写入的微调值）、`ReplayOnStartup` |
| `backup/msr-startup.json` | 首次运行时的 0x610 原始值 |
| `backup/msr-epp-startup.json` | 0x774 EPP 原始值（可读时） |
| `backup/msr-turbo-startup.json` | 0x1AD 睿频表原始值（可读时） |
| `backup/pending-restore.json` | 异常退出时未完成的 0x610 写入，下次启动提示恢复 |
| `backup/pending-turbo-restore.json` | 异常退出时未完成的 0x1AD 写入，下次启动自动恢复出厂倍率表 |
| `logs/msr-audit.log` | 每次写入/恢复的审计记录（含 HRESULT） |

行为约定：

- **开机重放**：`ReplayOnStartup=true` 且管理员 + 已授权时，把 `Manual` 写回寄存器；失败只告警，不影响启动。
- **退出恢复**：`RestoreOnExit=true` 时退出前写回启动备份值。
- **软件不运行**：CPU 完全按固件默认逻辑运转，不存在"残留改参数"。
- **异常退出**：pending 文件存在时，下次启动恢复并提示。

## 11. 已知限制与排障

| 现象 | 原因 / 处理 |
| :--- | :--- |
| `--write-test` 返回 FAIL / `0x80070002` | 管理员权限不足，或 LHM 被换成 0.9.6+ 只读后端；确认 csproj 为 0.9.4 |
| 温度/功耗读不到 | WinRing0 驱动被 Defender / HVCI「内存完整性」拦截；关闭内核隔离后重试，不要安装 PawnIO |
| 高级参数「当前生效」显示 `--` | 该 MSR 在本机不可读（常见于 `0x774`）；对应项禁用写入与持久化 |
| 调了 PL1 没有感觉 | 实际满载功耗低于固件墙时，调高完全等价；只有压到实际功耗之下才有效（本机约 <25W） |
| 38 倍睿频没有提速 | 本机 HWP 接管选频，PCU 不采纳软件写入的多核档位；实测零收益，勿当卖点 |
| 界面数值与寄存器不一致 | 决策目标值 ≠ 寄存器实读值；以「当前生效」列/`--msr-probe` 为准 |

其他：

- `exe` 未签名，可能触发 SmartScreen / Defender 误报。
- `dist/` 不入库；仓库只保留源码与文档。
- 安装包脚本就绪（`build/installer/*.iss`），本机未安装 Inno Setup 6，未编译。

## 12. 构建与发布

```powershell
powershell -ExecutionPolicy Bypass -File build/publish.ps1                  # 框架依赖单文件（约 12MB）
powershell -ExecutionPolicy Bypass -File build/publish.ps1 -SelfContained   # 自包含单文件（约 150MB）
powershell -ExecutionPolicy Bypass -File build/publish.ps1 -Installer       # 追加 Inno Setup 安装包（需 iscc.exe）
```

产物：`artifacts/app-win-x64/`（脚本输出）；仓库内 `dist/` 为本地交付目录，不入库。

## 13. 文档索引

| 文档 | 内容 |
| :--- | :--- |
| [`README_REFACTOR.md`](README_REFACTOR.md) | 决策树重构架构：四象限参数、五节点细节、凭证字段、平台推导、自检闸门 |
| [`docs/PRD.md`](docs/PRD.md) | 需求与验收基线（v0.3.0 冻结，头部有现行口径声明） |
| [`deliverables/智能微调生效性判定-v0.4.3.md`](deliverables/智能微调生效性判定-v0.4.3.md) | **产品口径单一来源**：生效矩阵、四条件、可判定验收、分级建议 |
| [`deliverables/集成与功能测试报告-v0.4.3.md`](deliverables/集成与功能测试报告-v0.4.3.md) | 模块/集成测试矩阵与实跑结果 |
| [`deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md`](deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md) | 写入链路根因、A/B/C 压测、阶梯标定 |
| [`deliverables/睿频倍率调节与38倍性能验证-v0.4.3.md`](deliverables/睿频倍率调节与38倍性能验证-v0.4.3.md) | 0x1AD 工程接入与「38x 无收益」实测 |
| [`deliverables/三页参数逻辑一致性审查-v0.4.0.md`](deliverables/三页参数逻辑一致性审查-v0.4.0.md) | 早期逻辑审查与修复记录 |

---

**一句话定位**：这是一个"把 CPU 热约束翻译成可解释功耗决策"的温控 / 静音工具，不是性能释放工具。
只有在主动把 PL1 压到实际功耗之下时才有可观测效果；这一点已被三轮满载压测反复验证。
