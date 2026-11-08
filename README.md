# ThermalTuner

> Windows 笔记本 CPU 热与功耗调优工具：读懂温度与真实功耗，用决策树给出功耗策略，可选写入 MSR 寄存器。
> 当前版本 **v0.5.3** ｜ 验收机 Intel Core i7-7700HQ / 4C8T / Windows 10 19045
> **v0.5.3（启动体验重构 + 产品语言分层）**：
> · **启动不再每次全量检测**：环境能力快照按「程序版本 + 机器 + 运行权限」做指纹缓存（`capability.json`），
>   命中即直接进入界面（实测「点开 → 看到主界面」约 1.3~1.6 秒，此前每次启动都要同步等约 3~5 秒）；
>   只有首次运行、更换设备、升级程序、权限变化或快照超过 30 天才重新检测，耗时部分（传感器枚举）转后台。
> · **启动初始化页**：先给一帧可见窗口（三步进度：载入设置 → 确认硬件与参数能力 → 准备监测数据），
>   结束后按闸门分流——**已就绪**直接进入；**仅观测**（普通权限）进入但停用调优并给出提权入口；
>   **未就绪**（连温度都读不到）停在初始化页给「重新检测 / 以管理员身份重启」，不再进入一个没有数据的调优界面。
> · **传感器异步就绪**：界面先出、数据后到，等待期显示「正在读取传感器…」而不是白屏或 `0`；
>   启动各阶段耗时写入 `startup-log.txt`（诊断页「日志与数据」可查）。
> · **产品语言与工程语言分层**：界面正文不再出现 `0x610 / MSR / PawnIO / L2/L3` 等工程词，改说
>   「持续功耗上限 / 短时功耗上限 / 短时时长 / 能效偏好 / 自动推荐值 / 手工微调」；寄存器地址收进
>   每行参数的工具提示「技术详情」，驱动与组件名只留在「组件与许可」「诊断与日志」。新增
>   `Services/ProductTerms.cs` 作为术语唯一词表。
> **v0.5.2（界面产品化整改）**：
> · **五页重新排版**：内容区统一滚动容器，长页不再被裁切；状态条、指标卡、工具条收敛到「一屏可读」，正文只留一句说明，长解释移入问号提示（`TipBadge`），界面不再出现版本沿革、内部研究备注等日志式文本。
> · **置灰即说明**：按钮禁用时原因**常驻显示**在按钮下方（WPF 禁用控件弹不出 ToolTip），「开始标定」缺哪项读数一眼可见。
> · **文案与实现对齐**：删掉「数据来自本机真实传感器」这类过度承诺；「选中即生效」在无写入权限时明确降级为「选中仅演算」；手工页读不到寄存器当前值时直接报「已禁止下发」，不再显示「沿用固件当前值」。
> · **口径统一**：产品可见文本（界面 / 提示 / 自检报告 / CLI 日志）统一为 PawnIO + IntelMSR + LHM 0.9.6+，不再出现已退役驱动与旧版本号。
> · **设置页重组**为「运行 / 调优模式 / 数据 / 持久化 / 组件与许可 / 关于」，手工页头部重叠、参数表折行等版式问题一并修复。
> **v0.5.1（MSR 通道迁移 PawnIO + 轨迹整形）**：
> · **WinRing0 彻底废弃，无降级后端**。原因不是"能修"的问题：OpenLibSys 的 WinRing0 已停止维护，`1.2.0.5`（2008 年编译）是最终版本，漏洞是设计性的（设备 ACL 宽松 + 无调用者校验，暴露任意 MSR 写与物理内存读写，CVE-2023-1048），被列入微软易受攻击驱动名单，Defender / 火绒会按情报库哈希直接删除驱动文件——产品无法依赖它。
> · **新后端：PawnIO 内核驱动 + IntelMSR 模块**，直连 IOCTL（不依赖 `PawnIOLib.dll`）。驱动本体为微软 WHQL 签名，只执行**已签名白名单模块**；`IntelMSR` 模块的写白名单覆盖 `0x610`（PL1/PL2/Tau）、`0x150`（OC Mailbox）等本项目所需寄存器。模块随安装包分发（`PawnIO.Modules 0.2.11`，LGPL-2.1，附 COPYING 与源码指向）。
> · **自管驱动安装**：官方安装器在本机恒返回 31 且不落文件，因此改为内置驱动三件套 + `pnputil` 导入 + LocalSystem 临时服务创建 Root 设备节点（`Enum\Root` 管理员也只读，只有 SYSTEM 能建），失败按错误码分流提示。命令行 `--install-driver`，GUI 在诊断页。
> · **能力边界（模块白名单的硬约束，不是本机故障）**：读白名单不含 `0x770 / 0x772 / 0x774 / 0x1A0 / 0x194` → **EPP 与 HWP 微调（Min/Max Perf、Activity Window）不可用**，UI 如实标注并禁用；`0x1AD` 睿频倍率表只读不可写。`0x610` 功耗墙、`0x150` 电压项不受影响。
> · **轨迹整形层** `TrajectoryShaper`（Core 层纯逻辑）：链路为「中值(3)去突刺 → 一阶低通 α=0.35 → 5°C 温度带 ±1°C 滞回 → 升档带门控」。升档按温度带放行、每带最多 `+6W`（静音档 4W），**降档与高温/PROCHOT 硬闸完全直通**，绝不延迟降压。热余量充足（低于天花板 ≥2 个温度带）时免带门控只保留限幅，避免「只降不升」的棘轮。
> · **旧版 WinRing0 相关说明已失效**：下文 v0.5.0 及更早的 `0x774` EPP 写入、`0x1AD` 睿频改写描述，在当前后端下已被模块白名单阻断，保留作历史记录。
> **v0.5.0（调优模式重构）**：
> · **选中即生效**：「智能微调」页点选档位立即触发一次「决策树重算 + `0x610` 下发」，不再是只改意图；有权限缺口 / 跨档升功率 / 升档冷却 / 离开手工模式 / 高温时先二次确认。不是把档位写死成固定瓦数，PL1 仍由温度与热阻决定。
> · **阶梯式下发调度器** `WriteScheduler`（Core 层纯逻辑，13/13 套件、443 断言覆盖）：PL1 量化到 **1W 台阶**、自动升档需**连续 3 拍同台阶**、**升档冷却默认 20s 且设置页可调**（5~120s）、高温降档立即放行、每分钟 ≤4 次写入熔断。只写 `0x610` 的 PL1/PL2，回读校验与审计不变；EPP / 风扇从不写入。
> · **模式状态机**：`Settings.TuningMode` 是单一真值源（`intelligent` / `manual`），替换旧版 `_manualLock` 的静默锁死；旧配置按「是否存在 L3 手动值」自动迁移。智能 / 手工互斥、状态在概览状态条与底栏可见；从手工切回智能会二次确认并停止开机重放。
> · **页面方案 B**：「高级参数」改名重构为「手工微调」，按钮改「保存并下发」；环境自检按钮与固件基线对照迁入「诊断与日志」，原页顶部只留一行能力状态；导航仍 5 项。「自动接管」语义改为「是否允许决策树在首次下发后持续动态微调」，不再决定能否开始写。
> · **热事件研究埋点**：每个真实触顶事件写一行 `%LOCALAPPDATA%\ThermalTuner\thermal-events.csv`（峰值、天花板、超温幅度、降档次数/瓦数、回落 dT/dt P50/P95、实际冷却、恢复后 60s 二次触顶次数）。本机 1001 个历史压测样本最高仅 78°C、零个触顶事件，**自适应冷却公式属 P1 研究项，本期不实现任何未验证系数**。
> · 硬红线不变：PROCHOT / ≥ 安全上限时「只降不升」不可由二次确认解除；写入成功只代表寄存器回读一致，功耗/频率收敛仍需 Tau 量级时间。
> v0.4.8：CPU 负载改用 Windows 系统计时（`GetSystemTimes` 差分，与任务管理器 `% Processor Time` 同源），
> LHM `CPU Total` 降为诊断对照、不再进指标卡 / 时序图 / 决策树 / 功耗估算——管理员态实测
> `CPU Total 82.1~86.8%` 而系统计时仅 `13.9~14.5%`，旧值会造成「高负载 + 低功耗 + 低温」的
> 自相矛盾判断并让决策树误判异常耗散区；构造末尾重置差分基准排除启动开销，`--sensor-probe` 两拍取第二拍。
> GPU 卡补齐显卡全名 / GPU Core 温度 / 来源口径，显存明确「专用显存 Used/Total」并说明与任务管理器
> 「GPU 内存（专用+共享）」的分母差异，修正包功耗卡副标题错绑 `GpuHintText` 的展示错位。
> **GPU 负载主指标保持 NVAPI `GPU Core` 整卡核心活动率**（贴近热与功耗、跨厂商稳定）；
> 任务管理器的 `GPU Engine` 是 per-PID × per-引擎 的分引擎利用率，本机一次枚举 318 个实例、
> 聚合规则随 Windows 版本变化，不宜作为散热工具的主指标，两者数值不必逐拍一致。
> v0.4.7：`HardwareTelemetry` 在采样出口把 CPU / GPU / 显存 / 风扇百分比统一钳到 `0~100`（`NaN` 保留给 UI 显示 `N/A`），修正 LHM `Load/CPU Total` 瞬时越界；风扇采集按方案①补齐**真实性与口径**：卡片显示 EC 原始字节、`FanStagnationDetector` 仅在真实 EC 读（非缓存命中）时计数，连续 10 次不变标注「疑似非实时 · 非 RPM」，图例 / ToolTip / CSV 同步附原始值，不把恒定刻度冒充实测转速。`ThermalTuner.Monitor` 遥测源同步同一口径。
> v0.4.6：风扇只读采集改为**内置 EC 端口读**（`EcPortReader`，复用 LHM 0.9.4 WinRing0 端口接口），不再依赖 NBFC / `ec-probe.exe`；寄存器号与刻度映射可在 `settings.json` 覆盖；`--ec-probe` 改为内置自测 + 可选 NBFC 交叉验证。**2026-11-08 管理员实机验证（v0.4.7，`--ec-probe` 两轮）**：WinRing0 链路可用、EC 端口读返回 `Ok=True`，但寄存器 149 原始字节恒为 `1`（一轮偶发一次 `144`），与 NBFC 时期读到的 `80` 不一致；按内置刻度换算被钳到 `100%`，这是**刻度钳制结果，不是风扇满速、更不是 RPM**。该寄存器当前语义未确认（端口握手竞态脏读 / 机型映射不匹配待查），界面按原始值 + 恒定检测标注「疑似非实时」；普通权限下仍降级显示 `N/A`。
> v0.4.5：概览页成为唯一监控主视图——8 张去重指标卡（含风扇档位）+ 一张多序列统一时序图 + 压测/明细/CSV 导出；删除独立「实时监控」页，导航 6 → 5 项；新增自绘 `MetricChart`、`MetricSeries`，不引入第三方图表依赖。
> v0.4.4：监控面板新增 CPU / GPU / 显存 / 风扇指标与明细列；风扇 EC 只读自动判断（`--ec-probe`）、传感器探针（`--sensor-probe`）、UI 截图工具（`--capture-ui`）；修复档位持久化恢复。
> 2026-11-06 起产品由 **ThermalPilot** 正式更名为 **ThermalTuner**；旧版配置会自动迁移，见 [§9](#9-仓库结构与模块设计)。

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
- [9. 仓库结构与模块设计](#9-仓库结构与模块设计)
- [10. 持久化与恢复语义](#10-持久化与恢复语义)
- [11. 已知限制与排障](#11-已知限制与排障)
- [12. 构建与发布](#12-构建与发布)
- [13. 文档索引](#13-文档索引)

---

## 1. 这是什么

ThermalTuner 是一个桌面端（WPF / .NET 8）CPU 热管理工具。它做三件事：

1. **看见**：读取 CPU 封装温度、核心频率、负载，并通过 RAPL 能量计数器（`0x611`）算出**真实包功耗**，而不是用负载估算。
2. **判断**：把物理约束、用户偏好、在线热模型拆成四象限参数，交给五节点决策树短路求值，输出带因果凭证的 PL1 / PL2 / Tau 建议。
3. **可选执行**：在管理员权限 + 用户显式授权下，把决策结果写入 MSR `0x610`（功耗墙），写入前后做备份、回读校验与自动回滚。v0.5.0 起所有 `0x610` 下发经 `WriteScheduler` 单入口，智能链路只写 PL1/PL2。**v0.5.1 起后端为 PawnIO，模块白名单已阻断 `0x774` / `0x1AD` 写入**（`0x1AD` 仅可读），下文涉及这两项的旧描述保留作历史记录。

典型用途：**在散热吃紧的机器上主动设定功耗上限，用可控的性能损失换取温度与噪音下降**；以及观察 CPU 热行为、做稳态热阻标定。

## 2. 能力边界（先看这张表）

| 对象 | 是否控制 | 说明 |
| :--- | :--- | :--- |
| MSR `0x610` PL1 / PL2 / Tau | ✅ 真实写入 | 唯一被证明有效的调节路径；PawnIO IntelMSR 模块写白名单覆盖，`--write-test` 实测 PASS |
| MSR `0x774` EPP | ❌ v0.5.1 起不可写 | 模块读/写白名单均不含 `0x774`，HWP 请求寄存器整体不可访问；历史上本机写入后也会被 Windows 电源计划覆盖 |
| MSR `0x1AD` 睿频倍率表 | ⚠️ 只读 | 在读白名单内、不在写白名单；**且本机实测拉平 38x 无性能收益，见 §3** |
| MSR `0x606` / `0x611` RAPL | 只读 | 能量单位与能量计数器，用于计算真实包功耗 |
| 风扇 / EC / PWM | ❌ 不控制 | 决策树里的 `TargetFanPercent` 只是动作字段，从未落到硬件；风扇由 EC 按温度曲线自动调速。支持**只读采集**风扇档位：内置 EC 端口读不依赖 NBFC，只写 `0x66=0x80` / `0x62=地址` 做读握手（需管理员；当前 PawnIO 后端下该通道可用性以诊断页实测为准），**绝不写 EC 寄存器内容或风扇控制寄存器 148**。界面同时给出 EC 原始字节与恒定检测标注，不把恒定刻度冒充实测转速 |
| 电源计划 / 电压 / 基频 | ❌ 不控制 | `0x150` FIVR 电压偏移未接入；不写电源计划 |

> 软件只能通过**降低发热**间接让风扇降速，不能直接拨风扇。这是设计选择，不是缺陷。

## 3. 版本状态与实测结论

| 项目 | 事实 |
| :--- | :--- |
| 决策树 | `ThermalDecisionEngine` 五节点短路求值，每拍由 `SessionState` 调用，非死代码 |
| 驱动通道 | **v0.5.1：PawnIO 内核驱动 + IntelMSR 模块**（直连 IOCTL，不依赖 `PawnIOLib.dll`）。LHM 0.9.6 仅用于传感器采样；WinRing0 已废弃且无降级后端。模块写白名单含 `0x610`，`--write-test` 实测写入→回读→恢复 PASS |
| 默认闸门 | 决策树始终在算，但写入需 `AllowRealWrite && AutoTakeover` 且未被手动微调锁住 |
| 已证明有效 | 把 PL1 压到**实际满载功耗之下**：45W → 15W 实测 **−7.10°C / −9.51W / −0.58GHz** |
| 已证明无效 | 放宽 PL1（在当时的单一标量负载下实际功耗 25~34W，天花板之上物理等价）；0x1AD 拉平 38x（核频 3.41→3.41GHz，只多 +2.04W / +1.11°C） |
| 风扇监控 | 只读采集风扇档位：优先 LHM 传感器 → **内置 EC 端口读（`EcPortReader`，不依赖 NBFC，需管理员；当前后端下可用性以诊断页实测为准）** → NBFC `ec-probe` / 服务状态（可选交叉验证）→ `N/A`。NBFC 卸载后仍可用；读到的值是 EC 原始刻度（0~255）按 `Min/Max` 归一化的档位，**不是物理转速 RPM**。默认映射为寄存器 149 / 255~58（本机校准值），可用 `settings.json` 的 `FanEcRegister` / `FanEcMinRaw` / `FanEcMaxRaw` 覆盖，换机型须重新确认。2.2s 低频、每步严格超时、连续失败自动停止探询，降低与 BIOS 争用 EC 端口的风险。**界面同时显示 EC 原始字节**：`FanStagnationDetector` 只在真正读完一次 EC 时计数（缓存命中不计数），连续 10 次原始值不变即标注「疑似非实时 · 非 RPM」，卡片副标题用警示色、ToolTip 给出判定依据，图例与 CSV 都附原始字节。该标注只是实时性提示，不把读数判为无效，更不代表物理转速。**2026-11-08 管理员实测**：内置 EC 链路可用，但寄存器 149 原始值恒为 `1`（偶发 `144`），换算恒 `100%`（钳制），与 NBFC 时期的 `80` 不符；该寄存器语义未确认，读数不可作为实时档位或转速依据，恒定检测会将其标为「疑似非实时」。历史分析见 [`deliverables/风扇只读采集与EC自动判断-2026-11-06.md`](deliverables/风扇只读采集与EC自动判断-2026-11-06.md) |
| CPU 负载口径 | `LoadPercent` 取 Windows 系统计时（`GetSystemTimes`）差分：总时间中非空闲占比，按全部逻辑核汇总后落在 0~100%，与任务管理器同源；**每拍都推进差分基准**，避免仅在回退路径调用时拿到「自启动以来」的平均值。任务管理器 CPU 页显示的是频率加权的 `% Processor Utility`，轻载睿频时略高于本值；「速度」= 基准频率 × `% Processor Performance`，是瞬时有效频率，不代表满载。LHM `Load/CPU Total` 属线程级估算、随两次 `Update()` 的窗口跳动，仅作 `--sensor-probe` 诊断对照，不进界面与决策树。GPU / 显存 / 风扇百分比仍在采样出口统一钳到 `0~100`（`NaN` 保留给 UI 显示 `N/A`）；`ThermalTuner.Monitor` 的独立遥测源同步同一口径 |
| GPU 负载口径 | 主指标取 LHM / NVIDIA NVAPI 的 `GPU Core` **整卡核心活动率**（核显回退 D3D 引擎峰值），与 GPU 温度、显存同源于 LHM，跨 NVIDIA / AMD / Intel 统一，最贴近「这块卡是否在耗电发热」的散热判断。任务管理器「利用率」来自 PDH `\GPU Engine(*)\Utilization Percentage`，按 PID × LUID × 引擎类型分实例（本机一次枚举 **318** 个；3D 引擎合计 14.11%、单实例峰值 8.72%，同期 `GPU Core` = 10.6~12.2%），需按引擎聚合并取最大 / 加权，规则随 Windows 版本变化；分引擎口径适合看「哪个引擎在忙」，但视频解码满载可能几乎不耗电、3D 轻载也可能高功耗，不适合作为热策略输入。因此**不替换主指标**，界面与 ToolTip 明确标注两者口径不同、数值不必逐拍一致 |
| GPU 显存与温度 | 显存按**专用显存** `Used/Total` 计算（对应任务管理器「专用 GPU 内存」）；任务管理器「GPU 内存」= 专用 + 共享显存，分母不同不可直接对比百分比。GPU 温度取 LHM `GPU Core` 温度传感器 |

> **关于「PawnIO 会静默丢失 MSR 写入能力」的澄清（v0.5.1 更正）**：该说法只适用于
> **LHM 0.9.6 内嵌的那份 IntelMSR 模块**——它是只读构建，仅导出 `ioctl_read_msr`。
> 本项目并不使用这份内嵌模块，而是随包分发 **PawnIO.Modules 0.2.11 独立构建的 `IntelMSR.bin`**，
> 其符号表同时含 `ioctl_read_msr` 与 `ioctl_write_msr`，写白名单包含 `0x610`。
> 另外 `PawnIoMsrBackend.Shared()` 会扫描模块字符串表，缺少 `ioctl_write_msr` 时直接报
> `ModuleUnavailable`，属于**显式失败而非静默降级**。实测：`artifacts/write-test-pawnio.txt`
> 显示 `Writable`、写入→回读→恢复 `RESULT: PASS`。
> 更换 LHM / 模块版本后仍应跑 `--write-test`，但不能把「LHM 内嵌只读模块」与「PawnIO 官方模块」混为一谈。

本机出厂寄存器实读（i7-7700HQ）：

```
0x610 = PL1 45.0W / PL2 56.2W / Tau 28672s(≈8h) / lock=false
0x1AD = 0x22232426 → 1C=38 / 2C=36 / 3C=35 / 4C=34（单核 3.8GHz / 全核 3.4GHz）
```

**可达功耗取决于负载场景，不是机器的固定上限。** 早期压测只用一种标量 `Math.Sqrt` 循环，
峰值 25~33.9W，曾被误判为「本机物理上限、功率根本跑不到 35W」。改用场景化负载后
（`--scenario-sweep`，2026-11-05 实测，LHM 实测包功耗）：

| 场景 | 线程 | 稳态均功耗 | 稳态峰值 |
| :--- | ---: | ---: | ---: |
| 整数 ALU 四分之一核 | 2 | 23.66 W | 24.04 W |
| 标量浮点半核 / 全核 | 4 / 8 | 31.02 / 40.42 W | 31.45 / 40.53 W |
| SIMD/FMA 半核 / 全核 | 4 / 8 | 33.97 / 44.29 W | 34.63 / **44.40 W** |
| 内存带宽全核 | 8 | 27.49 W | 27.83 W |
| 混合轮换 / 70% 脉冲全核 | 8 | 37.40 / 36.49 W | 37.87 / 37.18 W |

`SIMD_FULL` 的 44.40W 已逼近实读 PL1 45W（约 98%）：**「跑不到 35W」是压测负载过轻，不是 CPU 或软件的问题**。
历史报告里的 25W / 30.5W / 33.9W 都是**当时那一种负载**下的读数，换负载或换机器必须重跑 `--scenario-sweep`；
以管理员运行还能拿到「峰值功耗 / 实读 PL1」的覆盖率判定。

产品口径：固件里的值是**出厂默认，掉电即恢复、不可持久化修改**；可持久化的是软件侧实测标定值与手动微调值，存 `%LOCALAPPDATA%\ThermalTuner\settings.json`，开机由软件重放写入寄存器；软件不运行就按固件默认散热逻辑运转。

## 4. 快速开始

### 4.1 直接用发布包

| 包 | 适用 |
| :--- | :--- |
| `dist/ThermalTuner-v0.5.2-2026-11-08/`（自包含单文件目录） | 免装 .NET 运行时，直接运行 `ThermalTuner.App.exe` |
| `dist/ThermalTuner-v0.5.2-2026-11-08-win-x64.zip` | 同上，压缩包 |
| `dist/ThermalTuner-v0.5.1-2026-11-08/` | 上一版（PawnIO 迁移首个发布版），留作回滚 |

1. 解压后右键 `ThermalTuner.App.exe` → **以管理员身份运行**（读 MSR / RAPL 必需）。
2. 首次启动按向导确认档位与安全说明。
3. 想真正写入寄存器：**设置页**打开「允许写入 MSR」；然后在「智能微调」页点选档位 → 二次确认 → 立即下发一次。
4. 想让决策树之后按温度持续低频微调：再打开「自动接管」（不影响选中即下发）；升档冷却默认 20s，可在设置页调整。
5. 想完全手工控制：到「手工微调」页解锁编辑 → 「保存并下发」→ 自动进入手工模式，智能档位与决策树随即只演算。
6. 只观察不写入：保持「允许写入 MSR」关闭，界面照常显示决策与预测，但寄存器不变（确认框会明确说明只演算）。

### 4.2 从源码运行

```bash
dotnet build ThermalTuner.sln                  # 0 warning / 0 error
dotnet run --project src/ThermalTuner.App      # 启动图形界面
dotnet run --project src/ThermalTuner.Runner   # 环境探测 + 单测 + 模拟回路（无需硬件）
dotnet run --project src/ThermalTuner.Monitor -- --simulate --seconds 30
```

要求：Windows 10 1809+ / Windows 11，x64；.NET 8 SDK（构建）或 .NET 8 Desktop Runtime（运行框架依赖版）。读取 MSR / RAPL 需管理员权限。

## 5. 界面

| 页面 | 内容 |
| :--- | :--- |
| **概览** | 唯一监控主视图：8 张去重指标卡（温度 / 决策 PL1 / 包功耗 / 最高核频 / CPU 负载 / GPU 负载 / GPU 显存 / **风扇档位**）、一张多序列统一时序图（含风扇历史，各序列独立归一化、图例带真实单位）、最新决策凭证、决策流水、压测开关与采样明细表（含「风扇」列）、CSV 导出。风扇为 EC 只读采集：LHM 读不到时走**内置 EC 端口只读**（不依赖 NBFC，需管理员；当前后端下可用性以诊断页实测为准），NBFC 仅作可选交叉验证；卡片显示档位百分比 **+ EC 原始字节**，恒定不变时标注「疑似非实时 · 非 RPM」并用警示色，图例附原始字节，全部不可读才显示 `N/A`。CPU 负载已在采样入口钳到 `0~100%`。v0.5.0 起顶部新增**全局模式 / 写入状态条**：当前模式、是否真会写、具体闸门原因、最近一次下发结果与升档冷却剩余。详见 [监控面板指标扩展说明](deliverables/监控面板指标扩展说明-2026-11-06.md) 与 [风扇只读采集与 EC 自动判断](deliverables/风扇只读采集与EC自动判断-2026-11-06.md) |
| **智能微调** | 3 档模式（静音 / 均衡 / 性能）**选中即下发** + 二次确认 + 下发结果横幅；温度天花板滑块、在线热阻与预测稳态、两段式稳态标定、标定前馈上限 |
| **手工微调**（原「高级参数」） | 固件出厂默认 / 已保存软件优化值双锚点；PL1 / PL2 / Tau / EPP / 睿频倍率 / HWP / 电压偏移；「当前实读」列每拍读 MSR；「保存并下发」立即写入并进入手工模式。不可读 / 不可写的项显示 `--` 并禁用（能力感知，「合法范围」列直接写明不可读原因） |
| **诊断与日志** | 环境体检、驱动与权限、管理员重新自检、完整自检报告、**固件基线与三层参数对照**、日志与数据文件一览（settings / capability / 固件基线快照 / 写入审计 / 热事件）、传感器原始读数清单、事件中心（信息 / 注意 / 故障三级标识） |
| **设置** | 采样间隔、**升档冷却（默认 20s 可调）**、**调优模式切换**、允许写入 MSR、自动接管（仅管持续动态微调）、开机重放、退出恢复、开机自启、托盘行为 |

设计稿：`design/ThermalTuner-UI.html`。

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
              WriteScheduler 单一入口（v0.5.0）
              ├ 模式互斥：手工模式下智能档位/决策只演算
              ├ 阶梯量化 1W · 升档连续 3 拍迟滞 · 升档冷却 20s（可调）
              ├ 高温降档立即 · 每分钟 ≤4 次熔断 · 高温只降不升硬闸
              └ 通道：选中档位(事件，跳过迟滞) / 决策树每拍 / 手工保存 / 开机重放
                                                     │
                                   MsrGuard（备份 → 写 → 回读校验 → 失败回滚）
```

关键设计：

- **唯一预测入口**：`ThermalCalibration.PredictSteady` / `MaxPowerForCeiling`，标定报告、界面预测、决策树共用同一套公式。
- **标定前馈**：有实测热阻时，每拍反解「不越过天花板的最大 PL1」作为硬上界；未标定时不干预，避免拿不可信模型误钳。
- **写入护栏**：写前备份原始值，写后回读校验，不一致自动恢复；退出时可恢复固件默认；异常退出下次启动提示并恢复 pending。
- **模式互斥（v0.5.0）**：`Settings.TuningMode` 是单一真值源。手工模式下智能档位与决策树只演算；切回智能需二次确认并停止开机重放。旧版由 `_manualLock` 隐式推导、静默锁死智能档位的行为已移除。
- **为什么要阶梯节流**：`WRMSR` 单次只有微秒级开销，但写 `0x610` 改的是 RAPL 的 PL1 **平均功率预算**，PCU 需按 Tau 窗口（本机实读 16~32s）重新收敛。决策树每拍输出连续瓦数，旧版 3s 冷却 + Δ≥1W 会让 PL1 在两个值间来回跳（理论上限 1200 次/小时），控制环永远不收敛、频率与帧时间震荡。阶梯量化 + 迟滞 + 升档冷却把稳态写入压到 0~几次/小时。
- **不承诺即时性能**：写入成功只代表寄存器回读一致；功耗/频率实际收敛需要 Tau 量级时间。

架构细节（四象限参数、节点实现、凭证字段、平台推导表）见 **[`README_REFACTOR.md`](README_REFACTOR.md)**。

## 7. 自检与压测命令

全部支持 `--out <file>`，压测类另支持 `--csv <file>`。涉及 MSR/RAPL 的命令**需管理员**。

| 命令 | 作用 | 写硬件 |
| :--- | :--- | :--- |
| `--smoke` | 构造会话、逐页加载布局，输出 `RESULT: PASS/FAIL`；v0.5.0 起额外输出调优模式、升档冷却、自动写入闸门、模式状态条与一份**只读的选中下发计划**（不写寄存器） | 否 |
| `--shots <dir>` | 离屏渲染各页 PNG，供设计走查 | 否 |
| `--capture-ui --page overview --out ui.png [--height 1700]` | 渲染**带左侧导航的真实主窗口**为 PNG（需桌面会话；默认 `overview` 页），附同名 `.txt` 记录当拍读数；`--height` 用于长页整页走查 | 否 |
| `--ec-probe --out ec.txt --seconds 6` | **EC 只读诊断**：先做内置端口读自测（不依赖 NBFC，列出端口 API 绑定、驱动状态、寄存器 149 原值与档位换算），NBFC 仍在时追加 status / dump / monitor 交叉验证（管理员；绝不写 EC 寄存器内容，绝不调用 `ec-probe write`） | 否 |
| `--sensor-probe --out sensors.txt` | 枚举全部 LHM 传感器 + 一拍聚合指标（含风扇运行百分比与来源） | 否 |
| `--msr-probe` | 通道状态、0x610 解码、0x774/0x1AD 可读性 | 否 |
| `--msr-funcs` | 列出 IntelMSR 模块导出的 IOCTL 函数与通道状态 | 否 |
| `--rapl-probe` | RAPL 能量单位与包功耗采样 | 否 |
| `--env-check` | **首次安装启动自动跑只读自检**；此命令由管理员手动补跑完整自检（含无害写入探测），输出参数能力矩阵 + 外部依赖处置建议 + UI 禁用清单，落盘 `capability.json` | 同值写回 / −10mV 后回零 |
| `--write-test` | **写入→回读→恢复闭环**（通道验收第一命令） | 同值写回 + 恢复 |
| `--install-driver` / `--pawnio-svc` | PawnIO 驱动安装 / 修复（命令行）与安装器服务模式 | 安装驱动 |
| `--hwp-probe` | 0x774 能力实测（当前模块白名单不含 0x774，结论如实落盘供 UI 禁用该项） | 视白名单而定 |
| `--voltage-probe` | 0x150 电压偏移能力实测（−10mV 探针 → 回读 → 强制回零），结论落盘供 UI 启用/禁用该项 | 临时写入 + 强制回零 |
| `--turbo-probe` | 0x1AD 档位 / 锁位 / 38x 写入回读恢复 | 临时写入 + 恢复 |
| `--calibrate-test` | 两段式稳态热阻标定自检 | 临时写入 + 恢复 |
| `--calibrate-sweep --seconds 25 --ceiling 85` | 多档 PL1 阶梯标定，拟合 T(P) 并反解推荐值 | 临时写入 + 恢复 |
| `--scenario-sweep --seconds 12 --target-ratio 0.9` | **场景化功耗覆盖扫描**：12 个负载场景逐级加压，输出各场景「峰值功耗 / 默认功率」覆盖率 | 否（只读压测） |
| `--stress-test --seconds 40 --low-pl1 15 --kernel SimdFma` | A/B/C 满载压测：固件默认 / 降墙 / 放宽；可指定负载内核 | 临时写入 + 恢复 |
| `--turbo-stress --seconds 30` | 睿频 A/B 满载实测 + HWP/PROCHOT 诊断 | 临时写入 + 恢复 |

**默认功率与场景化压测（通用产品口径）**：软件不预设任何机型的瓦数。启动时直读 `0x610` 的 PL1，
这个实读值就是该机器的**默认稳态功率**；预测基准、降墙/放宽档位一律按它的比例推导
（降墙 40%、放宽 130%），不再出现 35W / 45W / +20W 之类的机型常量。

压测负载也不是单一循环。`StressWorker` 覆盖 6 类执行单元 × 线程规模 × 占空比：
`ScalarInt` / `ScalarFloat` / `SimdFma`(AVX2+FMA) / `MemoryStream` / `Mixed` / `Pulse`。
`--scenario-sweep` 跑完整场景阶梯（空载 → 单核整数 → 半核浮点 → 全核 SIMD → 内存带宽 → 混合 → 脉冲），
给出每个场景的功耗覆盖率，用来找出这台机器上真正能逼近功耗墙的场景；
若所有场景都远低于实读 PL1，说明瓶颈在 CPU 选频 / 电流墙 / 散热策略，而不是 PL1 天花板。
界面「概览」页底部的压测开关也可直接切换负载场景。

`--stress-test --kernel <内核>` 可指定 A/B/C 使用的负载内核（默认 `SimdFma`，可达功耗最高）；
`--low-pl1` 省略时降墙档按实读 PL1 的 40% 自动推导。

示例：

```powershell
# 管理员 PowerShell
.\ThermalTuner.App.exe --write-test --out write-test.txt
.\ThermalTuner.App.exe --stress-test --out stress.txt --csv stress.csv --seconds 40 --low-pl1 15
```

压测结束会恢复固件默认值并释放压测线程；报告里会打印最终 `0x610` 实读供复核。

## 8. 测试

### 8.1 单元 / 功能测试（无需硬件）

```bash
dotnet run --project tests/ThermalTuner.Core.Tests -c Release
```

覆盖模块：

当前共 **15 个套件**：`MsrEncoding`、`TurboRatioLimit`、`ThermalCalibration`、`ThermalResistance`、
`DecisionTree`、`PlatformProfile`、`StressScenario`、`AdvancedParams`、`ProfileEffect`、`FanPercent`、
`FanStagnation`、`FanSource`、`WriteScheduler`、`TrajectoryShaper`、`ThermalEvent`。
覆盖 0x610 位域编解码、0x1AD 解码、标定与热阻拟合、决策树因果与防棘轮、平台推导、负载场景、
三层参数约束、档位效果、风扇口径与恒定检测、风扇数据源抽象、下发调度、轨迹整形、热事件埋点。

期望输出：`套件 : 15/15 通过`、`断言 通过 536 / 失败 0`、`RESULT: PASS`，退出码 0（以最近一次实跑为准）。

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

### 8.3 控制台实时监控工具

```bash
dotnet run --project src/ThermalTuner.Monitor -- --simulate --stress --seconds 60
ThermalTuner.Monitor.exe --seconds 120 --profile performance
ThermalTuner.Monitor.exe --diag
```

参数：`--profile silent|balanced|performance`、`--interval <ms>`、`--out <file>`、`--simulate`、`--stress`、`--diag`。

## 9. 仓库结构与模块设计

> 完整版见 [`docs/REPOSITORY.md`](docs/REPOSITORY.md)（各级目录用途、模块依赖、数据流、设计约定）。

```
ThermalTuner/
├─ src/
│  ├─ ThermalTuner.Core/          纯逻辑：决策树、热模型、MSR 编解码、平台探测、负载场景库（无 UI / 无硬件依赖）
│  ├─ ThermalTuner.App/           WPF 主程序：会话状态、MSR 通道与护栏、五页界面、自检命令、Assets 图标
│  ├─ ThermalTuner.Monitor/       控制台实时监控 / 决策流水（诊断用）
│  └─ ThermalTuner.Runner/        无界面演示：环境探测 → 约束推导 → 单测 → 模拟回路
├─ tests/ThermalTuner.Core.Tests/ 独立测试入口（零外部测试框架依赖）
├─ build/                         publish.ps1 / test.ps1 / generate-icon.ps1 / installer/*.iss
├─ docs/                          PRD.md（v0.3.0 原文冻结）+ REPOSITORY.md（本文详版）
├─ deliverables/                  实测报告、判定口径、设计与验证文档（入库，供审阅追溯）
├─ design/                        UI 设计稿（静态 HTML）
├─ dist/                          体验包 exe / zip（.gitignore 排除）
├─ artifacts/                     publish 中间产物（.gitignore 排除）
└─ .workbuddy/                    项目工作记忆（.gitignore 排除，不是缓存，勿删）
```

**模块分层（简版）**：`Core` 全是可单测的纯函数（决策树 `DecisionTree/`、位域编解码 `Msr/`、
平台探测 `Environment/`、档位与能力 `Taxonomy/`、负载内核 `Stress/`）；
`App` 负责唯一门面 `Services/Msr/MsrChannel`、写入护栏 `MsrGuard`、状态中枢 `SessionState`、
遥测、持久化与界面 `Views/`；`Monitor` / `Runner` 只复用 Core，不写 MSR。

**数据流**：`LHM/RAPL → HardwareTelemetry → SessionState(1Hz) → AutoCpuController → MsrGuard
→ 0x610/0x774/0x1AD/0x150`，写入一律经过「备份 → 读-改-写 → 回读校验 → 失败回滚 → 审计」。
参数分三层：L1 固件固化值（只读基准，不可持久化）、L2 智能标定推荐、L3 用户编辑值（开机重放）；
能否编辑由首次环境自检 `capability.json` 决定。详见 [`docs/REPOSITORY.md`](docs/REPOSITORY.md)。

**更名兼容**：数据目录由 `%LOCALAPPDATA%\ThermalPilot` 迁到 `ThermalTuner`，首次启动自动复制
旧配置（标定/微调/MSR 出厂备份/能力快照），旧目录保留不删；HKCU Run 旧启动项仅在指向旧 exe 时清理。

## 10. 持久化与恢复语义

数据目录：`%LOCALAPPDATA%\ThermalTuner\`

| 文件 | 内容 |
| :--- | :--- |
| `settings.json` | 采样间隔、档位、双开关、`Calibration`（实测热模型）、`Manual`（已写入的微调值）、`ReplayOnStartup` |
| `capability.json` | 环境自检快照：决定哪些参数项可编辑（界面禁用的唯一依据） |
| `thermal-events.csv` | 真实触顶热事件埋点（首行表头；0 条是常态，不参与冷却策略） |
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
| `--write-test` 返回 FAIL / `0x80070002` | 管理员权限不足，或 PawnIO 设备 / IntelMSR 模块不可用（模块缺 `ioctl_write_msr` 会显式报 `ModuleUnavailable`，不是静默降级）。查「诊断与日志」页的后端名称与模块来源；**不要**用 LHM 0.9.6 内嵌的只读模块替换随包 `IntelMSR.bin` |
| 手工微调页某些项置灰不可编辑 | 首次安装启动的环境自检判定该项本机不可获取或实测不可写；完整报告在「诊断与日志」页，提权后点该页「管理员重新自检」刷新能力快照（`capability.json`）。v0.5.1 起 `0x774` / `0x1AD` 因模块白名单整体禁用，属预期而非故障 |
| 温度/功耗读不到 | PawnIO 驱动未安装 / 设备对象未创建 / 非管理员。在「诊断与日志」页执行驱动安装并查看错误码；**不要**为此回退 WinRing0（已废弃，且会被 Defender 按易受攻击驱动名单清除） |
| 手工微调「当前生效」显示 `--` | 该 MSR 在本机不可读（常见于 `0x774`）；对应项禁用写入与持久化 |
| 选中档位后只演算、没写寄存器 | 看概览状态条与档位区第二行的原因：常见于非管理员 / `允许写入 MSR` 未开 / 手工模式未切回。权限闸门不能靠二次确认绕过，确认前会明确告知 |
| 选中档位弹出「升档冷却中」 | 两次**升档**写入之间默认需 20s（设置页可调 5~120s），让 RAPL 按 Tau 收敛；确认框可强制放行这一次。降档与高温降档不受冷却限制 |
| 决策树每拍都在改寄存器 | 检查设置页「自动接管」：它只控制智能模式下决策树的**持续动态微调**，关闭后选中档位仍会立即下发一次。所有写入都经 1W 台阶 + 3 拍迟滞 + 升档冷却，稳态 0~几次/小时 |
| `thermal-events.csv` 是空的 | 只有真实「温度 ≥ 天花板」的触顶热事件才落一行；本机历史 1001 个压测样本最高 78°C、未触发过任何事件，属正常。该文件是研究埋点，不参与冷却策略 |
| 调了 PL1 没有感觉 | 实际满载功耗低于实读墙时，调高完全等价；只有压到实际功耗之下才有效 |
| 功率跑不到默认 PL1 | PL1 是上限不是目标，CPU 按负载取电；先跑 `--scenario-sweep` 找能逼近墙的负载场景，并核对覆盖率。所有场景都远低于墙说明瓶颈在选频/电流墙，不是 PL1 |
| 风扇转不到 100% | 软件不写风扇 / EC / PWM；界面的「风扇演算值」只是决策树建议值、未下发。转速由 EC 按温度曲线自动调节 |
| CPU 负载与任务管理器对不上 / 出现异常高值 | 界面 CPU 负载取 Windows 系统计时（`GetSystemTimes` 差分），采样出口已钳 `0~100`；任务管理器「利用率」是频率加权口径，轻载睿频时会略高。接近 100% 的异常值只会出现在 `--sensor-probe` 的 LHM 对照列，它不进界面与决策树 |
| 风扇显示 100% 却听不到声音 | 该值是 EC 原始字节按 `Min/Max` 换算并钳到 `0~100` 的**档位刻度**，不是转速；原始字节 ≤ Max 侧时一律显示 100%。看卡片副标题的「EC 原始值」与恒定标注，连续 10 次不变会提示「疑似非实时」。本机无 RPM 传感器，无法给出物理转速 |
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
> 若脚本的清理步骤被安全删除包装拦截（fail-closed），可绕过脚本直接 `dotnet publish`：
> `dotnet publish src/ThermalTuner.App/ThermalTuner.App.csproj -c Release -r win-x64 --self-contained true
> -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=X -p:FileVersion=X.0
> -p:AssemblyVersion=X.0 -o artifacts/app-win-x64`；打 zip 用 Windows 自带 bsdtar（Git Bash 的 GNU tar
> 不认 `.zip` 后缀，会产出裸 tar）。

## 13. 文档索引

| 文档 | 内容 |
| :--- | :--- |
| [`docs/REPOSITORY.md`](docs/REPOSITORY.md) | **仓库各级目录规划、源码模块设计、数据流与设计约定**（改名后新增） |
| [`deliverables/监控面板指标扩展说明-2026-11-06.md`](deliverables/监控面板指标扩展说明-2026-11-06.md) | 监控面板四项指标的数据源、实测口径、风扇 `N/A` 原因与验证结果 |
| [`README_REFACTOR.md`](README_REFACTOR.md) | 架构说明（v0.5.2 校订）：四象限参数、五节点、轨迹整形与下发调度、凭证字段、自检命令 |
| [`deliverables/产品体验评估-v0.5.1-2026-11-08.md`](deliverables/产品体验评估-v0.5.1-2026-11-08.md) | 五页 UX 评估 18 项问题与整改记录（v0.5.2 的整改依据） |
| [`docs/PRD.md`](docs/PRD.md) | 需求与验收基线（v0.3.0 冻结，头部有现行口径声明） |
| [`deliverables/智能微调生效性判定-v0.4.3.md`](deliverables/智能微调生效性判定-v0.4.3.md) | 生效矩阵、四条件、可判定验收、分级建议（v0.4.3 历史文档：生效性结论仍有效，后端与命令口径以本文 §3 / §7 为准） |
| [`deliverables/集成与功能测试报告-v0.4.3.md`](deliverables/集成与功能测试报告-v0.4.3.md) | 模块/集成测试矩阵与实跑结果 |
| [`deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md`](deliverables/智能调优生效性与压力测试验证-v0.4.2（修正版）.md) | 写入链路根因、A/B/C 压测、阶梯标定 |
| [`deliverables/睿频倍率调节与38倍性能验证-v0.4.3.md`](deliverables/睿频倍率调节与38倍性能验证-v0.4.3.md) | 0x1AD 工程接入与「38x 无收益」实测 |
| [`deliverables/三页参数逻辑一致性审查-v0.4.0.md`](deliverables/三页参数逻辑一致性审查-v0.4.0.md) | 早期逻辑审查与修复记录 |

---

**一句话定位**：这是一个"把 CPU 热约束翻译成可解释功耗决策"的温控 / 静音工具，不是性能释放工具。
只有在主动把 PL1 压到实际功耗之下时才有可观测效果；这一点已被三轮满载压测反复验证。
