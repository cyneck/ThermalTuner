# ThermalTuner 决策树重构说明

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
| 在线状态 | `IdentifiedThermalState` | `EstimatedRth` `IsConfident` `PredictedSteadyTempC` | 运行时热阻拟合与温度预测 |

---

## 3. 决策树：五节点短路求值

`ThermalDecisionEngine` 每个控制周期按优先级顺序求值，命中即返回：

| # | 节点 | 判定条件 | 动作 |
| :--- | :--- | :--- | :--- |
| 1 | `SafetyWatchdog` | PROCHOT 触发 或 温度 ≥ TjMax−3°C | 紧急降功耗（−15W），风扇 100%，立即生效 |
| 2 | `LightLoadFilter` | 负载 < 25% 或 包功耗 < 12W | 维持 PL1，避免轻载频繁步进扰动风扇 |
| 3 | `CeilingExceeded` | 温度 > 用户温度天花板 | 阶梯降功耗（−StepDownWatts），风扇推至上限 |
| 4 | `ThermalPredictive` | 准稳态下预测升功耗后稳态温度将超标 | 提前锁定当前功耗，杜绝过冲震荡 |
| 5 | `BottleneckArbiter` | 安全且未超温 | 功耗墙且有余量 → 升功耗；已达睿频上限 → 维持节能；死区内 → 维持现状 |

---

## 4. 因果凭证（DecisionReceipt）

每次决策输出不可变凭证，彻底白盒：

- `TriggeredRuleName`：命中的规则节点
- `CausalReason`：因果依据，如 `[功耗墙突破] 尚有 18.0°C 余量，升功耗至 38.0W`
- `ActiveLimit`：受限类型（`PowerWall` / `ThermalLimit` / `FrequencyCap` / `LightLoad`）
- `EvaluatedPath`：本次走过的完整决策路径

---

## 5. 环境自适应（兼容新老 CPU）

原实现按 Intel 7 代移动平台写死约束。现拆成两段：

```
HardwareProbe（探测） → HardwareLimitResolver（推导） → HardwareLimits（注入控制器）
```

- **探测**：Windows 注册表 / Linux `/proc/cpuinfo` / macOS `sysctl` 级联读取，解析厂商、代数、后缀形态、混合架构、虚拟化；异常一律静默降级，永不抛出。
- **推导**：按"厂商 + 代数 + 产品形态"映射 TjMax 与 PL1/PL2 范围；未知平台回退保守基线；虚拟化环境压缩功耗上限。
- **注入**：`AutoCpuController` 接受探测结果，输出 PL1/PL2 全程钳制在硬件边界内。决策树节点与平台完全解耦。

本机实测（Intel Core Ultra 7 255H）：

| 项目 | 本机推导值 | 7 代基线 |
| :--- | :--- | :--- |
| 识别结果 | 代数 200（Arrow Lake）· H · 混合架构 | Kaby Lake · M |
| TjMax / 安全天花板 | 100°C / 95°C | 100°C / 95°C |
| PL1 范围 | [15W, 90W] | [10W, 65W] |
| PL2 范围 | [20W, 115W] | [15W, 90W] |

---

## 6. 运行

```bash
dotnet build ThermalTuner.slnx                 # 构建（0 warning / 0 error）
dotnet run --project src/ThermalPilot.Runner   # 环境报告 + 单测 + 8 拍模拟回路
dotnet run --project tests/ThermalPilot.Core.Tests   # 仅单测
```

### 实时监控（本机实测）

```bash
# ① 合成热模型 + 真实 CPU 压测负载（无需驱动，立刻可跑）
dotnet run --project src/ThermalPilot.Monitor -- --simulate --stress --seconds 60

# ② 读取真实温度/功耗（需管理员 + PawnIO 驱动）
artifacts/monitor-win-x64/ThermalPilot.Monitor.exe --seconds 120

# ③ 传感器诊断（温度读不到时先跑这个）
artifacts/monitor-win-x64/ThermalPilot.Monitor.exe --diag
```

参数：`--profile silent|balanced|performance`、`--interval ms`、`--out FILE`（输出落盘）。

> 真实温度需要硬件访问驱动。Windows 内核隔离（HVCI）会拦截旧版 WinRing0，此时安装
> [PawnIO](https://github.com/namazso/PawnIO/releases) 签名驱动后以管理员运行即可；
> 未安装时程序自动降级并提示，不会崩溃。

换任意 CPU 无需改代码：重新探测即得到对应约束。

---

## 7. 图形界面（WPF 骨架）

产品原型 `design/ThermalPilot-UI.html` 已落地为可运行的桌面工程 `src/ThermalPilot.App`（net8.0-windows，纯 XAML + 自绘曲线，无第三方 UI 库）。

```bash
dotnet run --project src/ThermalPilot.App        # 启动界面（首次会走 4 步向导）
```

| 页面 | 现状 |
| :--- | :--- |
| 概览 | 4 指标卡 + 温度/功耗双曲线 + 最新决策凭证 + 决策流水 |
| 智能调优 | 3 档模式、天花板滑块、在线热阻/预测稳态、20s 标定 |
| 实时监控 | 连续采样大图、明细表、压测开关、CSV 导出（UTF-8 BOM） |
| 高级参数 | 默认只读 → 解锁编辑 → `ParamConstraints` 实时校验 → diff → 二次确认 |
| 诊断与日志 | 环境体检、73 个传感器清单、HVCI 状态、事件中心 |
| 设置 | 采样间隔、自动接管、退出恢复、许可与数据目录 |

研发自检（无需人工点击）：

```bash
ThermalPilot.App.exe --smoke --out smoke.txt    # 逐页加载 + 布局，输出 PASS/FAIL
ThermalPilot.App.exe --shots artifacts/ui-shots # 离屏渲染 9 张界面 PNG 供设计走查
```

### MSR 通道自检（安全模式）

```bash
ThermalPilot.App.exe --msr-probe --out probe.txt    # 库/模块/0x610 解码，全程不写
ThermalPilot.App.exe --msr-funcs --out funcs.txt    # 探测模块导出了哪些函数（零参数调用）
ThermalPilot.App.exe --write-test --out wt.txt      # 同值写回 + 恢复，验证写链路（需管理员）
```

本机实测（Core Ultra 7 255H，管理员）：读正常 `PL1 85W / PL2 115W / lock=false`；
写 0x610 返回 `0x80070002` —— 当前签名模块不支持写功耗墙，程序自动降级为只读并隐藏写入入口。
替代方案（Intel DTT / powercfg）见 [`docs/PRD.md`](docs/PRD.md) §16。

---

## 8. 打包与分发

```bash
pwsh -File build/publish.ps1                  # 便携单文件（5.2MB，需 .NET 8 桌面运行时）
pwsh -File build/publish.ps1 -SelfContained   # 自包含（约 150MB，免装运行时）
pwsh -File build/publish.ps1 -Installer       # 追加生成安装包（需 Inno Setup 6）
```

| 产物 | 路径 |
| :--- | :--- |
| 便携 exe | `artifacts/app-win-x64/ThermalPilot.App.exe` |
| 便携 zip | `artifacts/ThermalPilot-portable-0.3.0.zip` |
| 安装包脚本 | `build/installer/ThermalPilot.App.iss`（向导 / 桌面快捷方式 / 卸载 / 可选自启） |

> 未签名 exe 可能被 Defender 误报，分发前建议代码签名或提交误报申诉。
> 需求、验收标准与实现状态见 [`docs/PRD.md`](docs/PRD.md)。

