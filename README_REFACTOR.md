# ThermalTuner 决策树重构说明

> 对应代码：`main` / `refactor/decision-tree-architecture` @ `6692d89`（v0.4.3）
> 验收机：Intel Core i7-7700HQ / 4C8T / Windows 10 19045
> 本文按 2026-11-04 的实际代码与实测报告核对；历史实测原始数据见 `deliverables/`。

## 0. 版本状态速览（先看这个）

| 项目 | 当前事实 |
| :--- | :--- |
| 分支模型 | **是决策树模型**。`ThermalDecisionEngine` 五节点短路求值，每拍由 `SessionState` 调用，不是死代码 |
| MSR 写入 | **可写**。`ThermalPilot.App` 固定 `LibreHardwareMonitorLib 0.9.4`（WinRing0 驱动），`Ring0.ReadMsr/WriteMsr` 可用 |
| 真实写入目标 | `0x610` PL1 / PL2 / Tau；`0x774` EPP、`0x1AD` 睿频倍率也可写；`0x150` 电压偏移未接入 |
| 不控制 | 风扇 / EC / PWM / 电源计划 / 电压 / 倍频。风扇由 EC 按温度曲线自动调速 |
| 默认闸门 | 决策树每拍在跑，但 `TryAutoApply` 需过 `_manualLock` + `AllowRealWrite` + `AutoTakeover`；已保存手动微调会锁住自动接管 |
| 生效性 | 只有「PL1 压到实际满载功耗之下」被实测证明有效（−7.10°C / −9.51W / −0.58GHz）；放宽 PL1、睿频拉平 38x 零收益 |

> ⚠️ **不要升级 LHM 到 0.9.6+**：0.9.6 把驱动后端换成 PawnIO，其内嵌 IntelMSR 模块只有
> `ioctl_read_msr`（只读），升级会静默丢失 `0x610` 写入能力。升级前必须先确认新版本提供写接口并跑 `--write-test`。

---

## 1. 重构要解决的三个问题

旧版 `AutoCpuController` 是多层嵌套 `if-else`，带来三个后果：

| 痛点 | 表现 |
| :--- | :--- |
| 参数扁平混杂 | 物理硬约束、用户偏好、算法步进、在线状态混在一个配置里，易误配 |
| 决策黑盒 | 每次降功耗无法回答"为什么降"，没有因果追踪 |
| 难扩展难测试 | 加一条规则就加深嵌套，各逻辑节点无法独立单测 |

---

## 2. 四象限参数分类

| 象限 | 类 | 关键字段 | 职责 |
| :--- | :--- | :--- | :--- |
| 物理硬约束 | `HardwareLimits` | `TjMax` `MinPL1` `MaxPL1` `LegalTauSeconds` | 平台寄存器有效编码集合，只读注入 |
| 用户意图 | `UserProfile` | `TargetTemperatureCeiling` `FanMaxPercent` `Epp` | `Silent` / `Balanced` / `Performance` 预设 |
| 算法微调 | `TuningGains` | `StepUpWatts` `StepDownWatts` `DeadbandDeltaT` | 步进幅度、收敛死区、稳态门限 |
| 在线状态 | `IdentifiedThermalState` | `EstimatedRth` `IsSolved` `IsConfident` `QualityDescription` | 运行时热阻拟合；**不再保存预测稳态温度** |

稳态预测已收敛到唯一入口 `ThermalCalibration.PredictSteady` / `MaxPowerForCeiling`，
标定报告、界面预测卡片、决策树共用同一套口径。旧的 `PredictedSteadyTempC` 字段在 v0.4.0 三页审查时已删除。

---

## 3. 决策树：五节点短路求值

`ThermalDecisionEngine` 每个控制周期按优先级顺序求值，命中即返回
（`Core/DecisionTree/ThermalDecisionEngine.cs:20-27`）：

| # | 节点 | 判定条件 | 动作 |
| :--- | :--- | :--- | :--- |
| 1 | `SafetyWatchdog` | PROCHOT 触发 或 温度 ≥ TjMax−3°C | 紧急降功耗，风扇推满，立即生效 |
| 2 | `LightLoadFilter` | 负载 < 阈值 或 包功耗 < 阈值 | **锚定固件实读 PL1**（`FirmwarePL1W`），不是保持上一拍决策值 |
| 3 | `CeilingExceeded` | 温度 > 用户温度天花板 | 阶梯降功耗，风扇推至上限 |
| 4 | `ThermalPredictive` | 准稳态下预测升功耗后稳态温度将超标 | 提前锁定当前功耗，杜绝过冲震荡 |
| 5 | `BottleneckArbiter` | 安全且未超温 | 功耗墙有余量 → 升功耗；已达睿频上限 → 维持节能；死区内 → 维持现状 |

两处 v0.4.0 修复后的关键实现，读代码时别按老逻辑理解：

- 节点 2 用 `TargetPL1W = snapshot.FirmwarePL1W`（`Nodes/LightLoadFilterNode.cs:27-32`）。
  若保持 `snapshot.CurrentPL1W`（上一拍决策目标），高载推高后轻载不回落会形成**只升不降的棘轮**，
  授权写入后甚至会在 7% 负载把 PL1 从 45W 抬到 65W。
- 节点 4 统一调 `ThermalCalibration.PredictSteady`，ΔP 用 **PL1 设定值之差**而非功率传感器读数之差
  （`Nodes/ThermalPredictiveNode.cs:29-34`）；传感器有 1~3 秒滞后，用它会产生 120°C 这类假预警。

节点输出之后还有一道钳制：`AutoCpuController.cs:114-127` 用 `MaxPowerForCeiling` 反解
「不越过天花板」的最大 PL1 作为上界，**仅在有实测标定值时启用**，在线拟合未收敛时不干预。

---

## 4. 因果凭证（DecisionReceipt）

每次决策输出不可变凭证，彻底白盒：

- `TriggeredRuleName`：命中的规则节点
- `CausalReason`：因果依据，如 `[热阻稳态预警] 若 PL1 由 40W 提至 44W 预计稳态温度 86°C，超出天花板`
- `ActiveLimit`：受限类型（`PowerWall` / `ThermalLimit` / `FrequencyCap` / `LightLoad`）
- `EvaluatedPath`：本次走过的完整决策路径

界面「最新决策」卡片的 `LastRule` / `LastReason` / `LastPath` 直接来自这份凭证
（`SessionState.cs:1269-1271`）。注意：**凭证里的 TargetPL1W 是决策目标，不是寄存器实读值**，
高级参数页的「当前生效」列另走 `RefreshFirmwareValues()` 每拍实读 MSR。

---

## 5. 环境自适应（兼容新老 CPU）

原实现按 Intel 7 代移动平台写死约束。现拆成两段：

```
HardwareProbe（探测） → HardwareLimitResolver（推导） → HardwareLimits（注入控制器）
```

- **探测**：Windows 注册表 / Linux `/proc/cpuinfo` / macOS `sysctl` 级联读取，解析厂商、代数、后缀形态、混合架构、虚拟化；异常一律静默降级，永不抛出。
- **推导**：按"厂商 + 代数 + 产品形态"映射 TjMax 与 PL1/PL2 范围；未知平台回退保守基线；虚拟化环境压缩功耗上限。
- **注入**：`AutoCpuController` 接受探测结果，输出 PL1/PL2 全程钳制在硬件边界内。决策树节点与平台完全解耦。

下表是**重构期示例平台（Arrow Lake 示例，非当前验收机）**的推导结果：

| 项目 | 示例平台推导值 | 7 代基线 |
| :--- | :--- | :--- |
| 识别结果 | 代数 200（Arrow Lake）· H · 混合架构 | Kaby Lake · M |
| TjMax / 安全天花板 | 100°C / 95°C | 100°C / 95°C |
| PL1 范围 | [15W, 90W] | [10W, 65W] |
| PL2 范围 | [20W, 115W] | [15W, 90W] |

当前验收机（i7-7700HQ）的**实测**寄存器值：`0x610 = PL1 45.0W / PL2 56.2W / Tau 28672s(≈8h) / lock=false`，
`0x1AD = 0x22232426`（1C 38 / 2C 36 / 3C 35 / 4C 34）。以 `deliverables/` 实测报告为准，勿用上表代替。

---

## 6. 运行

```bash
dotnet build ThermalTuner.slnx                        # 构建（0 warning / 0 error）
dotnet run --project src/ThermalPilot.Runner          # 环境报告 + 单测 + 模拟回路
dotnet run --project tests/ThermalPilot.Core.Tests    # 仅单测
dotnet run --project src/ThermalPilot.App             # 启动 WPF 界面
```

### 实时监控

```bash
dotnet run --project src/ThermalPilot.Monitor -- --simulate --stress --seconds 60
ThermalPilot.Monitor.exe --seconds 120
ThermalPilot.Monitor.exe --diag
```

参数：`--profile silent|balanced|performance`、`--interval ms`、`--out FILE`。

> **驱动口径差异（注意）**：主程序 `ThermalPilot.App` 固定 LHM **0.9.4 / WinRing0**；
> 而 `ThermalPilot.Monitor` 这个重构期的独立诊断工具仍引用 **0.9.6**，代码里还留着
> 「安装 PawnIO 签名驱动」的旧提示。两者通道不同，排障时以主程序为准。
> WinRing0 属老式内核驱动，可能被 Defender / HVCI 内存完整性拦截；本机 HVCI 未开启，实测可加载。
> 换机若 `--write-test` 失败，先查管理员权限与核心隔离设置，不要按 Monitor 的旧提示去装 PawnIO。

---

## 7. 图形界面与 MSR 通道自检

产品原型 `design/ThermalPilot-UI.html` 已落地为 `src/ThermalPilot.App`（net8.0-windows，纯 XAML + 自绘曲线）。

| 页面 | 现状 |
| :--- | :--- |
| 概览 | 指标卡 + 温度/功耗曲线 + 最新决策凭证 + 决策流水 |
| 智能调优 | 3 档模式、天花板滑块、在线热阻/预测稳态、两段式稳态标定（RAPL 实测功耗） |
| 实时监控 | 连续采样大图、明细表、压测开关、CSV 导出 |
| 高级参数 | 固件出厂默认 / 已保存软件优化值双锚点、PL1/PL2/Tau/EPP/睿频倍率、实读「当前生效」列 |
| 诊断与日志 | 环境体检、传感器清单、事件中心 |
| 设置 | 采样间隔、自动接管、允许写入 MSR、开机重放、退出恢复 |

### 研发自检命令（全部支持 `--out`，压测类另支持 `--csv`）

| 命令 | 作用 | 写入硬件 |
| :--- | :--- | :--- |
| `--smoke` | 逐页加载 + 布局检查，输出 PASS/FAIL | 否 |
| `--shots <dir>` | 离屏渲染界面 PNG 供设计走查 | 否 |
| `--msr-probe` | 通道状态、0x610 解码、0x774 / 0x1AD 可读性 | 否 |
| `--msr-funcs` | 列出 Ring0 后端可用接口 | 否 |
| `--rapl-probe` | RAPL 能量单位与包功耗采样 | 否 |
| `--write-test` | **写入→回读→恢复闭环**（通道验收第一命令） | 同值写回 + 恢复 |
| `--calibrate-test` | 两段式稳态热阻标定自检 | 临时写入 + 恢复 |
| `--calibrate-sweep --seconds 25 --ceiling 85` | 多档 PL1 阶梯标定，拟合 T(P) 并反解推荐值 | 临时写入 + 恢复 |
| `--stress-test --seconds 40 --low-pl1 15` | A/B/C 满载压测：固件默认 / 降墙 / 放宽 | 临时写入 + 恢复 |
| `--turbo-probe` | 0x1AD 档位、锁位、38x 写入→回读→恢复 | 临时写入 + 恢复 |
| `--turbo-stress --seconds 30` | 睿频 A/B 满载实测 + HWP/PROCHOT 诊断 | 临时写入 + 恢复 |

涉及 MSR / RAPL 的命令**需管理员运行**；压测类结束都会恢复固件默认值并释放压测线程。

### 本机实测（i7-7700HQ，管理员，v0.4.3）

```
--msr-probe  : 通道 Writable / 可写；PL1 45.000W / PL2 56.250W / Tau 28672s / lock=false
--write-test : PASS（同值写回 → 改 PL1 → 回读校验 → 恢复原值）
--turbo-probe: PASS（写 38x → 回读 0x26262626 → 恢复 0x22232426）
--smoke      : PASS
```

> 旧版 README 曾写「写 0x610 返回 0x80070002、签名模块不支持写、程序自动降级只读」——
> **该结论已作废**。根因是当时把 LHM 升到 0.9.6，后端换成只读的 PawnIO IntelMSR 模块；
> 回退 0.9.4 后本机写入链路完全打通。详见 `deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md`。

### 决策树是否默认驱动寄存器

每拍 `SessionState.cs:1239` 调 `_controller.Update(...)`、`:1299` 调 `TryAutoApply(...)`，
决策树**始终在跑**；但 `TryAutoApply`（`:924-950`）有闸门：
`_manualLock` → `MsrWritable && AllowRealWrite && AutoTakeover` → 3 秒节流 → 高温只许降不许升
→ 变化 < 1W 跳过 → 失败 2 次熔断并关闭自动接管。

`SaveManualTuning`（`:790-792`）会设 `_manualLock = true` 且 `AutoTakeover = false`——
**手动微调与自动接管互斥**，防止决策树每拍覆盖用户保存的值。
此时决策树输出只用于界面展示与预测，寄存器由开机重放的手动微调值控制。

---

## 8. 打包与分发

```bash
pwsh -File build/publish.ps1                  # 便携单文件（需 .NET 8 桌面运行时）
pwsh -File build/publish.ps1 -SelfContained   # 自包含（目标机免装运行时）
pwsh -File build/publish.ps1 -Installer       # 追加安装包（需 Inno Setup 6，本机未安装）
```

| 产物 | 路径 |
| :--- | :--- |
| 构建输出 | `artifacts/app-win-x64/`（脚本默认输出，版本 `0.4.3`） |
| 主程序（自包含目录版） | `dist/ThermalPilot/ThermalPilot.App.exe` |
| 主程序压缩包 | `dist/ThermalPilot-win-x64.zip` |
| 带全部自检命令的便携包 | `dist/ThermalPilot-Stress-v0.4.3.zip` |
| 安装包脚本 | `build/installer/ThermalPilot.App.iss` |

> `dist/` 已在 `.gitignore` 排除，不入库；未签名 exe 可能被 Defender 误报。
> 需求、验收标准与实现状态见 [`docs/PRD.md`](docs/PRD.md)。

---

## 9. 文档索引与统一口径

| 文档 | 内容 |
| :--- | :--- |
| `deliverables/智能微调生效性判定-v0.4.3.md` | **产品口径单一来源**：三档判定矩阵、生效四条件、可判定验收、分级建议 |
| `deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md` | 写入链路修复根因、A/B/C 压测、阶梯标定 |
| `deliverables/睿频倍率调节与38倍性能验证-v0.4.3.md` | 0x1AD 工程接入与「38x 无性能收益」实测 |
| `deliverables/三页参数逻辑一致性审查-v0.4.0.md` | 棘轮、三套预测公式、EPP 等早期审查与修复记录 |
| `deliverables/智能调优生效性与压力测试验证-v0.4.0.md` | **已作废**（错误判定「本机不可写」），仅留作历史 |

口径要点：固件 `0x610` / `0x774` / `0x1AD` 里的值是**出厂默认，掉电即恢复，不可持久化修改**；
可持久化的是软件侧实测标定值 + 手动微调值，存 `%LOCALAPPDATA%\ThermalPilot\settings.json`，
开机由软件重放写入寄存器；软件不运行、未提权、未开写入开关时，CPU 按固件默认散热逻辑运转。
标定值可能高于固件值，属正常优化，不简单判为拟合误差。
