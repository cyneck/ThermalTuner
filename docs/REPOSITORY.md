# ThermalTuner 仓库结构与模块设计

> 面向研发与审阅者：说明**每一级目录放什么**、**源码模块怎么分层**、**数据怎么流动**、
> 以及哪些是必须遵守的设计约定。产品口径与快速上手请看 [`README.md`](../README.md)。

---

## 1. 顶层目录规划

| 路径 | 用途 | 是否入库 | 说明 |
| :--- | :--- | :--- | :--- |
| `README.md` | **产品主入口**：能力边界、实测结论、快速开始、自检命令、排障 | ✅ | 单一产品口径 |
| `README_REFACTOR.md` | 决策树重构架构深潜：四象限参数、五节点、凭证字段、平台推导 | ✅ | 架构历史与细节 |
| `ThermalTuner.sln` | 经典格式解决方案（5 个项目） | ✅ | SDK 8.0.427 不能构建 `.slnx`，故保留 `.sln` |
| `src/` | 产品源码，4 个 .NET 项目 | ✅ | 见第 2 节 |
| `tests/` | 独立测试入口（零外部测试框架） | ✅ | 见第 2 节 |
| `build/` | 构建、发布、安装包、图标生成、分层测试脚本 | ✅ | `publish.ps1` / `test.ps1` / `generate-icon.ps1` / `installer/*.iss` |
| `docs/` | `PRD.md`（v0.3.0 原文冻结）+ 本文 | ✅ | 需求基线 + 仓库说明 |
| `deliverables/` | 实测报告、判定口径、设计与验证文档 | ✅ | 交付落盘，供审阅追溯 |
| `design/` | UI 设计稿（静态 HTML） | ✅ | 视觉参考，不参与构建 |
| `dist/` | **体验包**（自包含 exe / zip） | ❌ 已忽略 | 交付给用户的可执行产物 |
| `artifacts/` | `dotnet publish` 中间产物、发布日志 | ❌ 已忽略 | 每次发布前由脚本清理重建 |
| `bin/` `obj/` | 各项目编译产物 | ❌ 已忽略 | 不手工编辑 |
| `.workbuddy/` | 项目工作记忆（日志 / 长期约定） | ❌ 已忽略 | **不是临时缓存，不要删除** |

> 入库边界的原则：**源码、文档、脚本、实测证据入库；可执行文件与编译产物不入库**。

---

## 2. src 项目职责与依赖

```
ThermalTuner.App  (WPF WinExe, net8.0-windows)  ──►  ThermalTuner.Core
ThermalTuner.Monitor (控制台, net8.0-windows)    ──►  ThermalTuner.Core
ThermalTuner.Runner  (控制台, net8.0)            ──►  ThermalTuner.Core
                                                  └──►  ThermalTuner.Core.Tests
ThermalTuner.Core.Tests (控制台, net8.0)         ──►  ThermalTuner.Core
```

| 项目 | 目标框架 | 职责 | 边界 |
| :--- | :--- | :--- | :--- |
| `ThermalTuner.Core` | `net8.0` | **纯逻辑**：决策树、热模型、MSR 位域编解码、参数约束、平台探测、负载场景库 | 不引用 WPF、不碰硬件、不做 IO；便于独立单测 |
| `ThermalTuner.App` | `net8.0-windows` | **主程序**：WPF 五页界面、1 Hz 会话状态机、MSR 通道与写入护栏、遥测、持久化、开机重放、全部命令行自检 | 唯一直接访问硬件与用户配置的项目 |
| `ThermalTuner.Monitor` | `net8.0-windows` | 控制台实时监控 / 决策流水，诊断用 | 复用 Core，不写 MSR |
| `ThermalTuner.Runner` | `net8.0` | 无界面演示：环境探测 → 约束推导 → 单测 → 模拟回路 | 无 UI、无硬件依赖 |
| `ThermalTuner.Core.Tests` | `net8.0` | 独立测试入口，自带轻量 `Check` 断言 | 零外部测试框架依赖 |

---

## 3. Core 模块设计

| 目录 / 文件 | 职责 |
| :--- | :--- |
| `DecisionTree/` | `ThermalDecisionEngine` + 五节点：`SafetyWatchdog → LightLoadFilter → CeilingExceeded → ThermalPredictive → BottleneckArbiter`；`ThermalSnapshot` / `ControllerAction` / `DecisionReceipt` 是节点间的数据契约 |
| `Msr/` | **纯函数位域编解码**：`RaplPowerLimit`(0x610)、`HwpRequest`(0x774)、`TurboRatioLimit`(0x1AD)、`FivrVoltage`(0x150)。只算位，不写硬件 |
| `Environment/` | `HardwareProbe` 采集 CPU/平台信息，`HardwareLimitResolver` 推导硬件合法区间，`CpuEnvironment` 是数据契约 |
| `Taxonomy/` | `HardwareLimits`、`UserProfile`（静音/均衡/性能档）、`CpuCapability`（型号能力预筛）、热状态与增益分类 |
| `Stress/` | `StressWorker` 六类负载内核（整数/标量浮点/AVX2-FMA/内存带宽/混合/脉冲）与 `StressScenarioCatalog` 场景阶梯、`Pl1Ladder` |
| `ThermalCalibration.cs` / `ThermalResistance.cs` | 两段式稳态标定、热阻 `R_th = ΔT/ΔP`、`PredictSteady` / `MaxPowerForCeiling` |
| `AutoCpuController.cs` | 决策树入口与自动接管闸门（双开关、冷却、失败熔断） |
| `ParamConstraints.cs` | 参数物理约束与分级违规（Block / Warn / Info） |

**分层原则**：Core 里所有编解码都是**可单测的纯函数**；真正的寄存器读写下沉到 App 的 `MsrGuard`。

---

## 4. App 模块设计

| 位置 | 职责 |
| :--- | :--- |
| `App.xaml` / `App.xaml.cs` | 启动入口：命令行自检分支（`--smoke` / `--msr-probe` / `--env-check` / `--scenario-sweep` …）、单实例互斥、提权模式标记、正常 UI 启动 |
| `MainWindow.xaml(.cs)` | 侧栏 + 五页容器、托盘生命周期、退出时恢复原值 |
| `Services/SessionState.cs` | **会话状态中枢**：1 Hz 采样、决策树调用、标定状态机、三层参数模型、自动接管闸门、告警与事件 |
| `Services/HardwareTelemetry.cs` | LHM 传感器采集（温度/频率/负载/包功耗），独立于 MSR 通道 |
| `Services/RaplPowerMeter.cs` | `0x611` 能量计数器 → 真实包功耗 |
| `Services/Msr/MsrChannel.cs` | **MSR 访问唯一门面**（LHM 0.9.4 / WinRing0），上层零处直接引用驱动 |
| `Services/Msr/MsrGuard.cs` | 写入护栏：启动备份、读-改-写、回读校验、失败回滚、脏标记、审计日志；`0x610 / 0x774 / 0x1AD / 0x150` |
| `Services/Msr/PawnIoNative.cs` `LhmPawnModule.cs` | 历史 PawnIO 适配死代码，**已由 csproj `<Compile Remove>` 排除**，保留仅作参考 |
| `Services/SettingsStore.cs` | `settings.json` 持久化 + **旧版数据目录迁移** |
| `Services/EnvironmentCheck.cs` | 首次安装启动的能力自检、`capability.json`、依赖处置建议、UI 禁用清单 |
| `Services/StartupRegistration.cs` | HKCU Run 自启注册与更名后的旧项清理 |
| `Services/TrayService.cs` | 托盘常驻、右键菜单、产品图标 |
| `Services/StressLoad.cs` | 压测负载薄封装（内核实现在 Core/Stress） |
| `Views/` | 五页（v0.5.0）：概览（含全部监控指标、统一时序图、全局模式状态条与压测/明细/导出）/ 智能微调（选中即下发）/ 手工微调（原「高级参数」）/ 诊断与日志（含环境自检与固件基线对照）/ 设置 |
| `Services/SessionState.cs` | 模式状态机 + `WriteScheduler` 单入口下发 + 热事件埋点（CSV 落盘）；页面 DataContext 的唯一来源 |
| `Windows/` | `ConfirmDialog`（含输入确认词的危险操作）、`FirstRunWizard` |
| `Themes/` `Controls/` `Assets/` | 设计令牌、图表控件、产品 ico |
| 根目录 `*Probe.cs` / `SmokeTest.cs` | 无界面自检命令：`MsrProbe`、`RaplProbe`、`StressProbe`、`CalibrationSweepProbe`、`ScenarioSweepProbe`、`TurboStressProbe`、`CalibrationProbe`、`SmokeTest` |

---

## 5. 关键数据流

```
LHM 传感器 / RAPL ──► HardwareTelemetry ──► SessionState(1Hz)
                                              │
                        ┌─────────────────────┼──────────────────────┐
                        ▼                     ▼                      ▼
                 AutoCpuController      三层参数模型              UI 五页
                 (决策树 → 目标值)      L1 固件 / L2 标定 / L3 用户
                        │                     │
                        └────► MsrGuard ──────┘
                       备份 → 读-改-写 → 回读 → 失败回滚 → 审计
                                │
                                ▼
                    0x610 / 0x774 / 0x1AD / 0x150（易失寄存器）
```

**三层参数模型**（务必区分，历史多次纠偏）：

| 层 | 载体 | 谁产生 | 是否持久化 |
| :--- | :--- | :--- | :--- |
| L1 固件固化值 | `MsrGuard` 启动备份 `backup/msr-*-startup.json` | BIOS 上电 | ❌ 只读基准，掉电即回默认 |
| L2 智能标定推荐 | `Settings.Calibration` | 两段式实测标定 | ✅ 只含通用安全参数 |
| L3 用户编辑值 | `Settings.Manual` | 「手工微调」页保存并下发成功后 | ✅ 开机由软件重放写入寄存器（仅手工模式） |

---

## 6. 必须遵守的设计约定

1. **LHM 固定 0.9.4**（WinRing0 提供 `Ring0.WriteMsr`）；0.9.6+ / PawnIO 只有 `ioctl_read_msr`，升级会静默丢失写入能力。
2. **固件默认值不可持久化修改**；软件不运行 / 未授权时 CPU 按固件默认散热逻辑运转。
3. **智能标定与应用标定不碰危险参数**（电压偏移、睿频倍率、温度墙）；个性化项只能由用户在「手工微调」页手动微调。
4. **所有写入必须过 `MsrGuard`**：备份 → 回读校验 → 失败自动回滚 → 审计，不允许上层直接写 MSR。
5. **只读演练与真实写入分列**；界面「决策目标值」不得冒充「寄存器实读值」。
6. **参数项能否编辑由首次环境自检 `capability.json` 决定**，不靠机型硬编码猜测。
7. **v0.5.0 模式与下发**：`Settings.TuningMode` 是模式唯一真值源（旧版 `_manualLock` 已移除）；所有 `0x610` 下发（选中事件 / 决策树每拍 / 手工保存 / 开机重放）必须经 `WriteScheduler` 单一入口：1W 台阶、升档 3 拍迟滞、升档冷却默认 20s 可调、高温降档立即、每分钟 ≤4 次熔断。PROCHOT / ≥ 安全上限的「只降不升」不可由二次确认解除。
8. **自适应冷却公式未实现**：`thermal-events.csv` 只是研究埋点；本机零个真实触顶样本，不得把未验证系数写进策略。
7. 机型瓦数一律来自 `0x610` **实读**，不写死常量；压测用场景库而非单一循环。

---

## 7. 构建、发布与自检入口

| 入口 | 作用 |
| :--- | :--- |
| `build/publish.ps1` | Release / win-x64 单文件发布（`-SelfContained` 自包含；`-Installer` 需 Inno Setup） |
| `build/generate-icon.ps1` | 生成多尺寸 `Assets/ThermalTuner.ico`（PNG-in-ICO，无外部依赖） |
| `build/test.ps1` | 分层集成测试 |
| `ThermalTuner.App.exe --smoke` | 五页 + MainWindow 加载冒烟 |
| `--msr-probe` / `--env-check` | 只读寄存器盘点 / 完整能力自检（管理员下含无害写探测） |
| `--write-test` / `--hwp-probe` / `--voltage-probe` / `--turbo-probe` | 各写入链路闭环自检，结束必须回到出厂基线 |
| `--scenario-sweep` / `--stress-test` / `--calibrate-sweep` | 场景功耗覆盖扫描 / A/B/C 压测 / 阶梯标定 |
| `tests/ThermalTuner.Core.Tests` | `dotnet run --project tests/ThermalTuner.Core.Tests` |

---

## 8. 命名与兼容

- 产品于 **2026-11-06 由 ThermalPilot 更名为 ThermalTuner**：命名空间、程序集、项目目录、
  可执行文件名、安装包、文档、托盘与窗口标题统一替换。
- `docs/PRD.md` 是 v0.3.0 历史原文冻结，正文保留当时命名，**头部有更名声明**指向新产品。
- 用户数据目录由 `%LOCALAPPDATA%\ThermalPilot` 迁到 `%LOCALAPPDATA%\ThermalTuner`：
  首次启动**复制**旧目录内容（标定、微调、MSR 出厂备份、能力快照、日志），
  旧目录保留不删、用标记文件保证只迁一次；HKCU Run 旧项仅在明确指向旧 exe 时清理。
