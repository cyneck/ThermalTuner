# ThermalPilot 研究数据集归档

> 本目录保存散热研究采用的**原始实测数据、仿真对照数据、派生诊断结果**，
> 用于以后更多机器散热场景的研究分析。所有文件只增不改，请勿覆盖原始 CSV。

## 目录结构

```
research-data/
├── raw/                        # 原始真机实测（按 日期-机器 归档）
│   └── 2026-10-28-deep1/       # HP OMEN 15-ce0xx / i7-7700HQ 首次 19 分钟深测
│       ├── lab-deep1-*.csv     #   主采样数据（706 样本，1.65s/样本）
│       ├── lab-deep1-*.json    #   元数据（计划、采样参数、机器信息）
│       ├── fan-deep1.csv       #   NBFC 旁路风扇占空比采样（5s/次）
│       ├── run-deep1.log       #   运行日志（阶段切换、告警）
│       └── plan-deep-v1.json   #   实验计划（5 段 / 19.5 min）
├── simulated/                  # 仿真对照数据（生成工具：tools/lab_diagnose.py --selftest）
│   ├── sim-healthy.csv         #   健康对照（50W 全额释放）
│   ├── sim-fanfault.csv        #   风扇/风道故障对照
│   ├── sim-timfault.csv        #   硅脂干涸对照（按真实机参数）
│   ├── sim-pllimit.csv         #   电气功率墙对照
│   ├── lab-demo-bios.csv       #   早期 DOE 演示（BIOS 曲线策略）
│   └── lab-demo-nbfc.csv       #   早期 DOE 演示（NBFC 两因子）
└── derived/                    # 派生结果（诊断模型输出，可被程序读取）
    └── diag-lab-deep1-*.json   #   特征 + 判定树结论 + 功率预算表 + 反事实
```

## 主采样 CSV 字段说明（lab-deep1-*.csv）

| 字段 | 含义 | 单位 |
|---|---|---|
| `timestamp` | 采样时刻 | ISO8601 |
| `elapsed_s` | 全程累计时间 | s |
| `stage` | 阶段 ID（D0 空闲 / D1 满载cap100 / D2 cap70 / D3 cap40 / D4 冷却） | — |
| `stage_elapsed_s` | 段内时间 | s |
| `load` | 负载标签（idle / cpu） | — |
| `proc_cap` | 计划设定的处理器最大状态 | % |
| `boost` | 睿频开关 | — |
| `cpu_temp_c` | CPU 封装温度 | ℃ |
| `cpu_core_max_c` | 最高核心温度 | ℃ |
| `cpu_power_w` | CPU 封装功耗 | W |
| `cpu_mhz` | 当前频率 | MHz |
| `cpu_volt_v` | 核心电压 | V |
| `cpu_load_pct` | 负载率（GetSystemTimes 差分） | % |
| `fan_out_pct` / `fan_rpm` | 风扇输出（有风扇档位段才有值） | % / RPM |

## 目标机实测基线（2026-10-28 deep1）

- 满载稳态 **96.7℃ @ 34.9W @ 3.1GHz**，峰值核心 99℃（贴 Tjmax=100）。
- 静态映射 `T = 23.9 + 2.084·P`（Rtot ≈ 2.087 K/W）。
- 热阻分解：**R1 ≈ 1.76 K/W（硅脂/压装重度断链），R2 ≈ 0.32 K/W（散热器侧健康）**。
- 35W 上限 = 温度墙（早期 40W → 稳态 35W 回落），非电气 PL。
- `powercfg` PROCTHROTTLEMAX 在本机**无效**（cap 100/70/40 功率温度全一样）。
- 温度秒级收敛（τ_die ~1s），换段后 1 个采样间隔内到平台。

## 复用指引（研究新机器散热场景时）

1. **采集**：管理员双击 `lab\run-diag5.cmd`（5.3 min 诊断）或 `lab\run-deep.cmd`（19.5 min 深测）。
2. **诊断**：`python tools/lab_diagnose.py <新csv> --tamb 室温 --design-w 设计功率`。
3. **对照**：把新机器特征与 `raw/2026-10-28-deep1`、`simulated/` 四场景对比，
   判断落在哪个热链路环节（TIM / 风扇风道 / 整体不足 / 电气墙 / 健康）。
4. **跨机研究**：`derived/*.json` 是机器可读的结构化特征，可直接入库做多机统计分析。

## 字段 / 阈值约定（与 tools/lab_diagnose.py 保持一致）

| 常量 | 值 | 含义 |
|---|---|---|
| R1_FAULT / R1_SEVERE | 0.8 / 1.4 K/W | TIM 断链 / 重度 |
| R1_NORMAL | 0.35 K/W | TIM 正常上限 |
| R2_FAN_FAULT | 0.7 K/W | 风扇/风道故障主判据 |
| DT_FAN_WEAK / OK | 6 ℃ | 风扇杠杆次级判据 |
| PIN_FRAC | 0.5 | 贴墙样本占比阈值（温度墙） |
| P_FRAC | 0.85 | 电气功率墙判定比例 |
