# ThermalTuner 架构说明

> 对应代码：`refactor/decision-tree-architecture` 分支（当前 **v0.5.2**）
> 验收机：Intel Core i7-7700HQ / 4C8T / Windows 10 19045
> 本文描述**当前实现**：MSR 后端为 PawnIO + IntelMSR 模块（直连 IOCTL），LHM 0.9.6 仅做传感器采样；
> 下发统一经 `WriteScheduler` 单入口，模式真值源是 `Settings.TuningMode`。
> 早期 v0.4.3 快照（WinRing0 / `_manualLock` 时期）的原文见 `deliverables/` 历史报告，不再在此维护。

## 0. 版本状态速览（先看这个）

| 项目 | 当前事实 |
| :--- | :--- |
| 决策模型 | **五节点短路求值决策树**。`ThermalDecisionEngine` 每拍由 `SessionState` 调用，不是死代码 |
| MSR 通道 | **PawnIO 内核驱动 + 随包 `IntelMSR.bin`（PawnIO.Modules 0.2.11，含 `ioctl_write_msr`）**。`MsrChannel` 是唯一门面，上层零处直接引用驱动 |
| 可写目标 | 只有 `0x610`（PL1/PL2/Tau）与 `0x150`（OC Mailbox，机型相关）在写白名单内；`0x774` 不可读写，`0x1AD` 只读 |
| 传感器 | LHM 0.9.6：温度 / 频率 / GPU / 风扇；真实包功耗走 RAPL 能量计数器 `0x611`（`RaplPowerMeter`） |
| 不控制 | 风扇 / EC / PWM / 电源计划 / 倍频。`TargetFanPercent` 只是决策树演算值，从未落到硬件 |
| 模式 | `Settings.TuningMode` = `intelligent` / `manual` 单一真值源；手工模式下智能档位与决策树只演算 |
| 下发 | 一律经 `WriteScheduler`：1W 台阶量化、升档连续 3 拍迟滞、升档冷却默认 20s（5~120s 可调）、高温降档立即、每分钟 ≤4 次熔断 |
| 生效性 | 只有「PL1 压到实际满载功耗之下」被实测证明有效（45W → 15W：−7.10°C / −9.51W / −0.58GHz）；放宽 PL1、睿频拉平 38x 零收益 |

## 1. 四象限参数分类

| 象限 | 类 | 关键字段 | 职责 |
| :--- | :--- | :--- | :--- |
| 物理硬约束 | `HardwareLimits` | `TjMax` `MinPL1` `MaxPL1` `LegalTauSeconds` | 平台寄存器有效编码集合，只读注入 |
| 用户意图 | `UserProfile` | `TargetTemperatureCeiling` `FanMaxPercent` `Epp` | 静音 / 均衡 / 性能预设；档位是意图，不是固定瓦数 |
| 算法微调 | `TuningGains` | `StepUpWatts` `StepDownWatts` `DeadbandDeltaT` | 步进幅度、收敛死区、稳态门限 |
| 在线状态 | `IdentifiedThermalState` | `EstimatedRth` `IsSolved` `IsConfident` `QualityDescription` | 运行时热阻拟合；不保存预测稳态温度 |

稳态预测唯一入口是 `ThermalCalibration.PredictSteady` / `MaxPowerForCeiling`：标定报告、界面预测卡、决策树共用同一套公式。

## 2. 决策树：五节点短路求值

`ThermalDecisionEngine` 每个控制周期按优先级顺序求值，命中即返回：

| # | 节点 | 判定条件 | 动作 |
| :--- | :--- | :--- | :--- |
| 1 | `SafetyWatchdog` | PROCHOT 触发 或 温度 ≥ TjMax−3°C | 紧急降功耗；风扇 100% 仅为演算建议值（未下发，EC 自调），立即生效 |
| 2 | `LightLoadFilter` | 负载 < 阈值 或 包功耗 < 阈值 | **锚定固件实读 PL1**（`FirmwarePL1W`），不是保持上一拍决策值——防止「只升不降」的棘轮 |
| 3 | `CeilingExceeded` | 温度 > 用户温度天花板 | 阶梯降功耗；风扇目标仅为演算建议值（未下发，EC 自调） |
| 4 | `ThermalPredictive` | 准稳态下预测升功耗后稳态温度将超标 | 提前锁定当前功耗，杜绝过冲震荡；ΔP 用 **PL1 设定值之差**而非传感器读数之差（传感器滞后会产生假预警） |
| 5 | `BottleneckArbiter` | 安全且未超温 | 功耗墙有余量 → 升功耗；已达睿频上限 → 维持节能；死区内 → 维持现状 |

节点输出之后还有一道钳制：`AutoCpuController` 用 `MaxPowerForCeiling` 反解「不越过天花板」的最大 PL1
作为上界，**仅在有实测标定值时启用**，在线拟合未收敛时不干预。

## 3. 轨迹整形与下发调度（v0.5.1 / v0.5.0）

决策树输出到寄存器之间有两层「减抖」：

```
DecisionReceipt ──► TrajectoryShaper ──► WriteScheduler ──► MsrGuard ──► 0x610
```

- **`TrajectoryShaper`**（Core 纯逻辑）：中值(3)去突刺 → 一阶低通 α=0.35 → 5°C 温度带 ±1°C 滞回 →
  升档带门控。升档按温度带放行、每带最多 +6W（静音档 4W）；**降档与高温/PROCHOT 硬闸完全直通**，
  绝不延迟降压。热余量充足（低于天花板 ≥2 个温度带）时免带门控只保留限幅。
- **`WriteScheduler`**（Core 纯逻辑，单一入口）：所有 `0x610` 下发（选中档位事件 / 决策树每拍 /
  手工保存 / 开机重放）都从这里走。1W 台阶量化、自动升档需连续 3 拍同台阶、升档冷却默认 20s
  （设置页 5~120s，只约束升档）、高温降档立即放行、每分钟 ≤4 次写入熔断。
  **PROCHOT / ≥ `MaxSafeCeiling` 的「只降不升」是唯一不可由二次确认解除的硬闸。**
  智能链路只写 PL1/PL2（Tau 保持寄存器原值）；手工微调页保留精确瓦数与 Tau 改写。

## 4. 因果凭证（DecisionReceipt）

每次决策输出不可变凭证，彻底白盒：

- `TriggeredRuleName`：命中的规则节点
- `CausalReason`：因果依据，如 `[热阻稳态预警] 若 PL1 由 40W 提至 44W 预计稳态温度 86°C，超出天花板`
- `ActiveLimit`：受限类型（`PowerWall` / `ThermalLimit` / `FrequencyCap` / `LightLoad`）
- `EvaluatedPath`：本次走过的完整决策路径

界面「最新决策凭证」卡片的 `LastRule` / `LastReason` / `LastPath` 直接来自这份凭证。
注意：**凭证里的 TargetPL1W 是决策目标，不是寄存器实读值**；手工微调页的「当前实读」列另走
`RefreshFirmwareValues()` 每拍实读 MSR。

## 5. 环境自适应（兼容新老 CPU）

```
HardwareProbe（探测） → HardwareLimitResolver（推导） → HardwareLimits（注入控制器）
```

- **探测**：注册表读取厂商、代数、后缀形态、混合架构、虚拟化；异常一律静默降级，永不抛出。
- **推导**：按「厂商 + 代数 + 产品形态」映射 TjMax 与 PL1/PL2 范围；未知平台回退保守基线；虚拟化环境压缩功耗上限。
- **注入**：`AutoCpuController` 接受探测结果，输出 PL1/PL2 全程钳制在硬件边界内；决策树节点与平台解耦。

**禁止把任何机型的瓦数写死**：启动直读 `0x610` PL1 作为该机的默认稳态功率，降墙 40% / 放宽 130% 一律按比例推导。

当前验收机（i7-7700HQ）的**实测**寄存器值：`0x610 = PL1 45.0W / PL2 56.2W / Tau 28672s / lock=false`，
`0x1AD = 0x22232426`（1C 38 / 2C 36 / 3C 35 / 4C 34）。以 `deliverables/` 实测报告为准。

## 6. 运行

```bash
dotnet build ThermalTuner.sln                         # 构建（0 warning / 0 error）
dotnet run --project src/ThermalTuner.Runner          # 环境报告 + 单测 + 模拟回路
dotnet run --project tests/ThermalTuner.Core.Tests    # 仅单测
dotnet run --project src/ThermalTuner.App             # 启动 WPF 界面
```

控制台实时监控工具（与 App 共用同一遥测口径，不写 MSR）：

```bash
dotnet run --project src/ThermalTuner.Monitor -- --simulate --stress --seconds 60
ThermalTuner.Monitor.exe --seconds 120 --profile performance --diag
```

## 7. 图形界面与自检命令

| 页面 | 现状 |
| :--- | :--- |
| 概览 | 8 张去重指标卡（含风扇档位）+ 多序列统一时序图 + 最新决策凭证 + 决策流水 + 压测开关 / 采样明细 / CSV 导出；顶部全局模式 / 写入状态条 |
| 智能微调 | 3 档模式选中即下发（二次确认）、档位卡「生效方式」两步说明 + 「之后持续动态微调」就地开关、天花板滑块、在线热阻与预测稳态、两段式稳态标定 |
| 手工微调 | 固件出厂默认 / 已保存软件优化值双锚点、PL1/PL2/Tau/EPP/HWP/睿频/电压、实读「当前实读」列、能力感知禁用 |
| 诊断与日志 | 环境体检、驱动与权限、环境自检报告、固件基线三层对照、日志与数据文件一览、传感器清单、事件中心（信息 / 注意 / 故障三级） |
| 设置 | 采样间隔、升档冷却、调优模式切换、允许写入 MSR、开机重放、退出恢复、自启、托盘（持续动态微调已迁到智能微调页档位卡） |

### 自检命令（全部支持 `--out`，压测类另支持 `--csv`；写入类需管理员）

| 命令 | 作用 | 写硬件 |
| :--- | :--- | :--- |
| `--smoke` | 逐页加载 + 布局检查 + 只读选中下发计划，输出 PASS/FAIL | 否 |
| `--shots <dir>` | 离屏渲染界面 PNG 供设计走查 | 否 |
| `--capture-ui --page <页> --out <png>` | 渲染带导航的真实主窗口 + 当拍读数记录 | 否 |
| `--install-driver` / `--pawnio-svc` | PawnIO 驱动安装 / 修复（命令行）与安装器服务模式 | 安装驱动 |
| `--msr-probe` | 通道状态、0x610 解码、各 MSR 可读性 | 否 |
| `--msr-funcs` | 列出 IntelMSR 模块导出的 IOCTL 函数 | 否 |
| `--rapl-probe` | RAPL 能量单位与包功耗采样 | 否 |
| `--env-check` | 完整能力自检（管理员下含无害写探测），落盘 `capability.json` | 同值写回 / 电压回零 |
| `--write-test` | **写入→回读→恢复闭环**（通道验收第一命令） | 同值写回 + 恢复 |
| `--hwp-probe` / `--voltage-probe` / `--turbo-probe` | 各写入链路能力实测（0x774 在当前白名单下不可写，结论如实落盘） | 视白名单而定 |
| `--calibrate-test` / `--calibrate-sweep` | 两段式标定自检 / 多档 PL1 阶梯标定 | 临时写入 + 恢复 |
| `--scenario-sweep` | 场景化功耗覆盖扫描（12 个负载场景 × 实读 PL1 覆盖率） | 否（只读压测） |
| `--stress-test --low-pl1 15 --kernel SimdFma` | A/B/C 满载压测，可指定负载内核 | 临时写入 + 恢复 |
| `--turbo-stress` | 睿频 A/B 满载实测 + HWP/PROCHOT 诊断 | 临时写入 + 恢复 |

压测结束都会恢复固件默认值并释放压测线程；涉及 MSR / RAPL 的命令需管理员运行。

## 8. 决策树是否默认驱动寄存器

每拍 `SessionState` 都在调用决策树，但**下发有闸门**（按顺序）：

1. `Settings.TuningMode`：手工模式下智能档位与决策树只演算；
2. `MsrWritable && Settings.AllowRealWrite`：通道可写且用户显式授权；
3. 选中档位走 `PrepareProfileApply` → 二次确认 → `ApplyProfile`（事件通道，跳过升档迟滞）；
4. 决策树持续微调还需 `Settings.AutoTakeover`；
5. 之上全部先过 `WriteScheduler`：1W 台阶 → 3 拍迟滞 → 升档冷却 → 每分钟 ≤4 次熔断 → 高温只降不升。

「之后持续动态微调」（内部字段 `AutoTakeover`，开关在智能微调页档位卡内）只控制决策树是否每拍修正，不影响选中档位的立即下发。

## 9. 打包与分发

```bash
powershell -ExecutionPolicy Bypass -File build/publish.ps1 -SelfContained -Version 0.5.2
```

产物：`artifacts/app-win-x64/ThermalTuner.App.exe`（自包含单文件，约 159MB）。
交付目录 `dist/ThermalTuner-v<版本>-<日期>/` + 同名 zip；`dist/` 不入库。
版本号 5 处统一：`ThermalTuner.App.csproj`、`MainWindow.xaml` 侧栏（现绑定程序集 `VersionText`）、
`README.md`、`build/publish.ps1` 默认 `-Version`、`build/installer/ThermalTuner.App.iss`。

## 10. 口径要点（多次纠偏后的结论）

- 固件 `0x610` / `0x774` / `0x1AD` 里的值是**出厂默认，掉电即恢复，不可持久化修改**；
  可持久化的是软件侧实测标定值（L2）+ 手动微调值（L3），存 `%LOCALAPPDATA%\ThermalTuner\settings.json`，
  开机由软件重放写入寄存器；软件不运行、未提权、未开写入开关时，CPU 按固件默认散热逻辑运转。
- 标定值可能高于固件值，属正常优化，不简单判为拟合误差。
- 决策目标值 ≠ 寄存器实读值；以手工微调页「当前实读」列 / `--msr-probe` 为准。
- 写入成功只代表回读一致，功耗 / 频率收敛需要 Tau 量级时间。

架构与模块清单详见 [`docs/REPOSITORY.md`](docs/REPOSITORY.md)；产品口径与快速上手见 [`README.md`](README.md)。
