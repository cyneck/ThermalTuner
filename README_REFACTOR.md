# ThermalTuner 架构与决策树重构说明 (Refactoring Overview)

## 1. 重构背景与核心解决的问题
在原有实现中，`AutoCpuController.cs` 采用多层嵌套的 `if-else` 条件判断，存在以下架构痛点：
1. **参数混杂扁平**：物理硬约束、用户场景预设、算法调节步进与模型在线状态混在单一配置中，普通用户难以理解且易误配导致系统不稳定。
2. **决策逻辑黑盒**：控制策略没有因果追踪，每一次功耗增降缺乏显式的“因果凭证（Decision Receipt）”，无法回答“为什么当前降功耗了”。
3. **扩展与测试困难**：新增控制条件（如热阻预测超调预警）直接增加原有条件分支的圈复杂度，难以对各个逻辑节点做独立隔离的单元测试。

---

## 2. 新架构：四象限参数分类学 (Taxonomy)

| 象限分类 | 命名空间/类 | 核心字段示例 | 职责契约 |
| :--- | :--- | :--- | :--- |
| **物理硬件硬界限** | `Taxonomy.HardwareLimits` | `TjMax`, `MinPL1`, `MaxPL1`, `LegalTauSeconds` | 只读注入，代表平台物理硬约束与寄存器有效编码集合。 |
| **用户意图配置文件** | `Taxonomy.UserProfile` | `TargetTemperatureCeiling`, `FanMaxPercent`, `Epp` | 业务意图抽象（如 `Silent`, `Balanced`, `Performance`）。 |
| **算法微调专家参数** | `Taxonomy.TuningGains` | `StepUpWatts`, `StepDownWatts`, `DeadbandDeltaT` | 控制算法的步进幅度、收敛死区与稳态门限。 |
| **在线估计模型状态** | `Taxonomy.IdentifiedThermalState` | `EstimatedRth`, `IsConfident`, `PredictedSteadyTempC` | 运行时动态拟合状态，包含热阻与温度预测。 |

---

## 3. 白盒决策树拓扑 (Causal Decision Tree Engine)

每个控制周期的决策评估由 `ThermalDecisionEngine` 驱动，依序经历以下五个单一职责节点：

```text
[Telemetry Sampling Tick]
          │
          ▼
┌────────────────────────────────────────────────────────┐
│ 1. SafetyWatchdogNode (最高优先级)                      │
│    判定: PROCHOT 触发 或 当前温度 >= TjMax - 3°C?       │
│    动作: 紧急降功耗 (-15W)，强制风扇 100%，立即生效      │
└────────────────────────────────────────────────────────┘
          │ (否)
          ▼
┌────────────────────────────────────────────────────────┐
│ 2. LightLoadFilterNode (防轻载扰动)                     │
│    判定: 负载 < 25% 或 封装功耗 < 12W?                   │
│    动作: 维持当前 PL1，避免频繁启停扰动风扇与系统时钟      │
└────────────────────────────────────────────────────────┘
          │ (否)
          ▼
┌────────────────────────────────────────────────────────┐
│ 3. CeilingExceededNode (超温负反馈)                     │
│    判定: 当前温度 > TargetTemperatureCeiling?          │
│    动作: 阶梯下调功耗 (-StepDownWatts)，风扇推至上限     │
└────────────────────────────────────────────────────────┘
          │ (否)
          ▼
┌────────────────────────────────────────────────────────┐
│ 4. ThermalPredictiveNode (热阻稳态超调预警)              │
│    判定: 准稳态下，预测提升功耗后的稳态温度将超出天花板? │
│    动作: 提前锁定当前功耗，杜绝过冲震荡                  │
└────────────────────────────────────────────────────────┘
          │ (否)
          ▼
┌────────────────────────────────────────────────────────┐
│ 5. BottleneckArbiterNode (性能瓶颈仲裁)                 │
│    • Power Wall (有温度余量) -> 提升功耗 (+StepUpWatts) │
│    • Frequency Cap (已达最大睿频) -> 维持功耗，节约能耗 │
│    • Deadband (稳态平衡) -> 维持现状                   │
└────────────────────────────────────────────────────────┘
```

---

## 4. 显式因果凭证 (DecisionReceipt)
每一次调整均输出不可变的 `DecisionReceipt`：
* `TriggeredRuleName`：命中的具体规则节点名称
* `CausalReason`：详细的因果依据（如 `[功耗墙突破] 系统受限于 PL1 功耗，且尚有 18.0°C 温度安全余量，执行升功耗至 38.0W`）
* `ActiveLimit`：受限类型枚举
* `EvaluatedPath`：该决策所走过的完整决策树审计路径列表
