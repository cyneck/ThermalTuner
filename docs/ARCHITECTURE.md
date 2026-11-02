# 架构说明

这份文档回答三个问题：**代码怎么分层、参数怎么生效、测试怎么跑**。

---

## 1. 分层与依赖方向

```
ThermalPilot.App (WPF)  ─┐
ThermalPilot.Cli (tplab) ─┼──►  ThermalPilot.Core   ──►  Windows / LibreHardwareMonitor
ThermalPilot.Core.Tests  ─┘        （无 UI 依赖）
```

依赖是**单向**的：Core 不认识任何界面类型，App 与 Cli 都只调 Core。测试工程只引用 Core。

| 工程 | 职责 | 不做什么 |
|---|---|---|
| `Core` | 硬件采集、参数真源、机理约束、控制律、标定拟合、持久化 | 不弹窗、不读 XAML、不做格式化输出 |
| `App` | 界面、把控件值同步进参数中心、把提交结果下发到引擎 | 不含物理公式与阈值（全部来自 Core） |
| `Cli` | 现场验证与实验（能当场跑的诊断命令） | 不复制 Core 的判断逻辑 |
| `Core.Tests` | 把 Core 的不变量钉死（xUnit） | 不测 UI、不依赖真实硬件 |

---

## 2. 参数怎么生效（决策树）

参数体系只有一个真源：`ParameterCenter` 的三层状态。

```
Draft（草稿）      面板编辑的就是它，改动只在内存
Committed（已应用）上一次成功提交、当前硬件上真正生效的值
Preview（预览）    标定结果 / 控制器决策 / MSR 现值，只读派生，永不进 Diff
```

### 2.1 编辑链路

```
用户改控件（TextBox / CheckBox / ComboBox）
   │
   │  WireDraftCapture() 统一挂事件：
   │    文本 → 350 ms 防抖（避免每敲一个字符重建表格）
   │    勾选 → 立即
   ▼
CaptureDraftFromUi()
   ├─ BuildConfigFromUi()        界面 → Draft（唯一写 Draft 的地方）
   ├─ PlatformPower.Apply()      设计功耗变了 → 重算 PL1/PL2 的可用区间
   ├─ RefreshPending()           Diff(Committed, Draft) → 「待应用 N 项」→ 主按钮 enable
   └─ ShowValidation()           实时列出 Block / Warn（此时只提示，不阻断编辑）
```

### 2.2 提交链路（唯一入口）

```
底栏「应用（写入硬件）」
   │
   ├─ 1. BuildConfigFromUi()            再同步一次界面（防止有控件漏了事件）
   ├─ 2. PlatformPower.Apply(Draft)     用新的设计功耗去算校验区间
   ├─ 3. ConfirmUndervoltIfNeeded()     只有"新开降压/改偏移"才二次确认
   ├─ 4. YieldAutoForManualCpuEdit()    手改 PL1/PL2/τ/EPP → 智能自动让位（关自动、开手工）
   ├─ 5. ParamConstraints.Validate()    Block → 拒绝，原因原样回显；Warn → 放行
   ├─ 6. ParameterCenter.Apply()        Committed = Draft.Clone()
   │       ├─ ControlChannels.Normalize()      三套控制器互斥，只留一个
   │       └─ CpuCapabilities.Sanitize()       本机没有的旋钮直接清掉
   ├─ 7. ThermalEngine.ApplyConfig()    写 MSR / powercfg / NBFC
   └─ 8. SettingsStore.Save()           Config + Profile（基线+增量）同时落盘
```

「启用智能自动」按钮走同一入口但传 `autoYield:false` —— 那个动作的语义就是"要自动"，显式意图优先。

### 2.3 四条硬规则

1. **编辑只能进 Draft** —— 任何控件事件都不允许直接改 Committed 或写硬件。
2. **只有 `Apply()` 能改 Committed**，且必须先过 `Validate()`。
3. **只有 Committed 能进硬件** —— `ThermalEngine.ApplyConfig(Committed)` 是唯一出口。
4. **手调即让位** —— 用户手改 PL1/PL2/τ/EPP 并提交时，智能自动必须关闭、手工调控打开，
   否则控制器每 3 秒重算会把手调值顶掉。预设套用、「读取当前值」不算手调，不让位。

### 2.4 曾经踩过的坑（现在由测试守着）

| 症状 | 根因 | 防线 |
|---|---|---|
| 底部「应用（写入硬件）」永久置灰 | 高级页的输入框没挂事件，Draft 从不更新 | `ParameterCenterTests.任一参数编辑都必须产生待应用项` |
| 「应用旋钮」提示"已应用"但硬件没变 | 它只调了"存草稿"，名字与行为不符 | 该按钮已删除；提交只剩一个入口 |
| 手改 PL1 应用后几秒又变回去 | 智能自动每 3 秒重算四项，把手调值顶掉 | `YieldAutoForManualCpuEdit` + `ParameterCenterTests` 手调让位一组 |
| 三个开关同时写频率 | 通道未互斥 | `ControlChannelsTests` 6 条 |
| 改 `τ` 被直接拒绝 | 把"表示不精确"当成了非法 | `τ_非_2_的幂应警告`（Warn 而非 Block） |

---

## 3. 参数分四类

| 类别 | 谁能改 | 例子 |
|---|---|---|
| 内置基线 | 只有模型（代码） | 曲线形状、PID 系数、控制律常数 |
| 用户增量（Delta） | 用户，落 `settings.json → Profile` | 温度上限、风扇上限、PL1、EPP |
| 锁定项 | 谁都不能越界 | 安全线 ≤95℃、TJmax 100、τ ∈ 2 的幂、PL2 ≤ 2×PL1、芯片睿频上限、在线估计的 R |
| 自动接管项 | 控制器每 3 秒重算 | auto + msr 通道下的 PL1 / PL2 / τ / EPP（表格标「自动接管」，不计自定义；用户手改并应用时自动让位） |

配置档 = **基线 + 增量**：升级程序后，没改过的项跟随新基线，改过的保持。判定唯一出处是
`ProfileInstance.Rows()`。

---

## 4. 目录结构

```
ThermalPilot/
├─ src/
│  ├─ ThermalPilot.Core/           # 无 UI 依赖的内核
│  │  ├─ Models.cs                 # 快照 / 曲线 / 配置 / 预设 / 标定模型
│  │  ├─ HardwareService.cs        # 传感器采集 + 风扇通道 + NBFC 后端
│  │  ├─ TempFilter.cs             # 温度抗毛刺（5 点中值）
│  │  ├─ ThermalEngine.cs          # 闭环控制 + 标定接管 + 失效保护
│  │  ├─ ParameterCenter.cs        # 参数真源（Draft/Committed/Preview）+ 通道互斥
│  │  ├─ ParamConstraints.cs       # 机理约束（范围 + 交叉 + 温度预测）
│  │  ├─ ProfileInstance.cs        # 配置档（基线 + 增量 + 锁定项）
│  │  ├─ PlatformPower.cs          # CPU/GPU 功率上限探测
│  │  ├─ CpuCapabilities.cs        # 平台能力矩阵（本机有没有这个寄存器）
│  │  ├─ CpuControlService.cs      # MSR 读写（PL1/PL2/τ/EPP/睿频）+ 读回校验
│  │  ├─ PerfMetrics.cs            # 频率三口径 / 包功率 / 限制原因
│  │  ├─ ThermalResistance.cs      # 热阻在线估计（ΔT/ΔP 斜率）
│  │  ├─ Calibration.cs            # 标定编排 + 拟合 + 曲线生成
│  │  ├─ Diagnostics.cs            # 环境体检与修复建议
│  │  └─ ProcessMonitor.cs         # 进程功耗归因
│  ├─ ThermalPilot.App/            # WPF 界面
│  └─ ThermalPilot.Cli/            # tplab 实验台
├─ tests/
│  └─ ThermalPilot.Core.Tests/     # xUnit（见第 5 节）
├─ build/                          # publish.ps1 + WiX/Inno 安装包脚本
├─ docs/MODEL.md                   # 热模型与方法论
├─ docs/ARCHITECTURE.md            # 本文
├─ lab/  lab-data/                 # 实验计划与产出
└─ tools/                          # Python 分析器（拟合/DOE/建模）
```

---

## 5. 测试

```powershell
# 全部单元测试
dotnet test ThermalPilot.sln -c Release

# 只跑参数体系
dotnet test tests/ThermalPilot.Core.Tests -c Release --filter "FullyQualifiedName~ParameterCenter"
```

| 测试类 | 守住什么 |
|---|---|
| `ParameterCenterTests` | 编辑必须产生待应用项；阻断不污染已应用值；固定事件触发 |
| `ParamConstraintsTests` | 物理边界（TJmax/τ/EPP/PL2/风扇倒挂），且每条规格都有机理说明 |
| `ControlChannelsTests` | 三套控制器互斥，未知通道兜底 |
| `TempFilterTests` | 单点/交替毛刺剔除，真实升降阶跃不被削弱 |

界面行为（不写进 xUnit）用自检截图验证：

```powershell
ThermalPilot.exe --smoke --shot=out.png                 # 仪表盘
ThermalPilot.exe --smoke --smoke-nav=advanced --shot=a.png
```

`--smoke` 只走界面装配与退出路径，**不落盘、不写硬件**（`App.SmokeTest` 下所有提交函数直接返回）。

---

## 6. 构建

```powershell
# 便携 exe + MSI
powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Version x.y.z

# 只编不打
dotnet build ThermalPilot.sln -c Release
```

便携版产物 `artifacts/publish-v<版本>/ThermalPilot.exe`，安装包 `artifacts/ThermalPilot-Setup-<版本>.msi`。
