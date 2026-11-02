#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make_demo_data.py —— 生成"物理仿真"的演示数据集（**不是真机数据**）

用途：在没有真机样本时，验证 lab_model.py 的建模链路（特征工程 → 模型比较 →
交叉验证 → 反解控制律 → 导出策略文件）是否跑得通、结论是否合理。

仿真模型（与 docs/MODEL.md 的物理关系一致）：
    P      = C·V²·f                       （动态功耗；f 由 cap 决定，V 随 f 上升）
    R_th   = r0 + r1/(fan + r2)           （风量越大热阻越小）
    T_ss   = T_amb + R_th(fan)·P
    dT/dt  = (T_ss − T)/τ                 （一阶惯性）
    BIOS   : fan = clamp(f0 + k·(T−T1), min, 100)   ← 固件闭环（温度高→转速高）
    NBFC   : fan = 常数（开环，用于分离风扇增益）

用法：python tools/make_demo_data.py [输出目录]
"""

import math
import os
import random
import sys

# ---- 机器参数（仿 i7-7700HQ + 单风扇笔记本，量级贴近真实）----
T_AMB = 31.0          # 环境温度 ℃
TAU = 74.0            # 热时间常数 s
C_EFF = 0.011626      # 有效开关电容系数，P(W) = C·V²·f(MHz)
F_MIN, F_MAX, F_TURBO = 800.0, 2800.0, 3400.0
R0, R1, R2 = 0.95, 30.0, 8.0     # R_th(fan) = R0 + R1/(fan+R2)

# BIOS 风扇曲线（闭环）
def fan_bios(t):
    return max(30.0, min(100.0, 30.0 + 1.6 * (t - 45.0)))

def freq_from_cap(cap, turbo=True):
    f = F_MIN + (cap / 100.0) * (F_MAX - F_MIN)
    if turbo and cap >= 99:
        f = F_TURBO
    return f

def volt_from_freq(f):
    return 0.72 + 1.10e-4 * (f - F_MIN)

def power(cap, turbo=True):
    f = freq_from_cap(cap, turbo)
    v = volt_from_freq(f)
    return C_EFF * v * v * f

def rth(fan):
    return R0 + R1 / (fan + R2)

ROWS_HEADER = ("timestamp,elapsed_s,stage,stage_elapsed_s,load,cpu_temp_c,cpu_core_max_c,"
               "cpu_volt_v,cpu_load_pct,cpu_mhz,cpu_power_w,gpu_temp_c,gpu_load_pct,"
               "gpu_power_w,fan_rpm,fan_out_pct,proc_cap,boost")


def simulate(stages, fan_mode="bios", seed=11, dt=1.0):
    """stages: [(id, label, sec, load, cap, boost, fan_override)]"""
    rnd = random.Random(seed)
    t_amb = T_AMB
    T = t_amb + 9.0                      # 起始：轻度使用
    rows = [ROWS_HEADER]
    t_all = 0.0
    for sid, label, sec, load, cap, boost, fan_ov in stages:
        p = power(cap, turbo=(boost != 0)) if load == "cpu" else 5.2
        for i in range(int(sec / dt)):
            fan = fan_ov if fan_mode == "nbfc" and fan_ov is not None else fan_bios(T)
            t_ss = t_amb + rth(fan) * p
            T += (t_ss - T) * (1.0 - math.exp(-dt / TAU))
            noisy = T + rnd.gauss(0, 0.32)
            rpm = 0 if fan_mode == "bios" else int(900 + fan * 42)
            rows.append(
                f"2026-10-28 12:00:00,{t_all:.1f},{sid},{i*dt:.0f},{load},"
                f"{noisy:.1f},{noisy + rnd.gauss(1.2, 0.3):.1f},{volt_from_freq(freq_from_cap(cap, boost != 0)):.3f},"
                f"{100.0 if load == 'cpu' else 4.0:.1f},{freq_from_cap(cap, boost != 0):.0f},"
                f"{p * (1 + rnd.gauss(0, 0.015)):.1f},47.0,0,0,{rpm},"
                f"{'' if fan_mode == 'bios' or fan_ov is None else int(fan)},"
                f"{cap},{-1 if boost is None else boost}")
            t_all += dt
    return rows


BIOS_STAGES = [
    ("S0", "空闲基线", 90, "idle", 100, 2, None),
    ("S1", "满载 cap35", 130, "cpu", 35, 2, None),
    ("S2", "满载 cap50", 130, "cpu", 50, 2, None),
    ("S3", "满载 cap65", 130, "cpu", 65, 2, None),
    ("S4", "满载 cap80", 130, "cpu", 80, 2, None),
    ("S5", "满载 cap100", 200, "cpu", 100, 2, None),
    ("S6", "满载 cap100 Turbo关", 130, "cpu", 100, 0, None),
    ("S7", "冷却", 130, "idle", 100, 2, None),
]

# NBFC 模式：**两因子全因子设计**（cap × fan）。
# 只变一个因子的"阶梯"在二维模型里不可辨识（LOO 会崩），必须让 cap 与 fan 交叉出现。
NBFC_STAGES = [("F0", "空闲", 90, "idle", 100, 2, 50)] + [
    (f"F{i+1}", f"满载 cap{c} fan{f}", 170, "cpu", c, 2, f)
    for i, (c, f) in enumerate([(100, 90), (100, 70), (80, 70), (80, 90),
                                (60, 70), (60, 90), (100, 50), (80, 50), (60, 50)])
] + [("FZ", "冷却 fan50", 150, "idle", 100, 2, 50)]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lab-data", "_demo")
    os.makedirs(out, exist_ok=True)
    for name, stages, mode in (("demo-bios", BIOS_STAGES, "bios"), ("demo-nbfc", NBFC_STAGES, "nbfc")):
        rows = simulate(stages, fan_mode=mode)
        p = os.path.join(out, f"lab-{name}.csv")
        with open(p, "w", encoding="utf-8") as f:
            f.write("\n".join(rows))
        print(f"{p}  ({len(rows)-1} 行, 仿真数据)")
    print("\n注意：这是物理仿真的演示数据，不是真机测量值，只能用来验证建模链路。")


if __name__ == "__main__":
    main()
