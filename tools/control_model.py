#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ThermalPilot CPU 精细调控参数模型（control_model.py）

把「三档目标温度 / 噪声预算」翻译成具体的 CPU + 风扇 + GPU 硬件旋钮，并按
「性能代价最小」的优先级求解。这是上一版 thermal_policy 只做「功率预算」的
升级：真实生效的 CPU 闸不是 powercfg PROCTHROTTLEMAX（本机实测无效），
而是 Intel 的 MSR 控制字——

    CPU 精细调控（ThrottleStop / 直接写 MSR，第 2 层，全软件可控）★主力
        降压           MSR 0x150 (IA32_VOLTAGE_OFFSET / Undervolt)
        包功率 PL1     MSR 0x610 (IA32_PACKAGE_POWER_LIMIT) bits 14:0
        包功率 PL2     MSR 0x610 bits 46:32        ← 短时突增功率上限（多高）
        PL2 持续 τ     MSR 0x610/0x618 时间窗字段   ← 突增能持续多久（与 PL2 独立）
        Speed Shift   MSR 0x773 (IA32_HWP_REQUEST) 的 EPP 字段（0=性能，255=节能）
        睿频/频率上限  MSR 0x1AD (TURBO_RATIO_LIMIT) 或 OS 频率上限策略
    GPU 精细调控（nvidia-smi / Afterburner，第 3 层，独立于 CPU，全软件可控）
        功率墙 % / 核心·显存频率偏移 / 电压曲线 / 温度目标
    风扇占空比         NBFC / EC（降 R2，唯一影响噪声的旋钮，第 2 层附属）

控制面分层（可控性，见 CONTROL_LAYERS）：检测（异常识别）跑在「出厂默认逻辑」
（第 1 层 Windows 电源计划，本机实测基本失效）下只观察；调控则伸到第 2/3 层
MSR/驱动级，而不是只有 Windows 自带电源管理。第 0 层硬件固有（TDP/R1/散热模组）
与第 4 层外部（环境温度/散热底座）不可软件控制，只能当输入识别或物理改动。

物理映射（与 detect 两热阻模型一致）：
    P(V,f) ≈ P_static + (P_dem - P_static) · (V/V0)² · (f/f0) · epp_k
    T_ss   = T_amb + (R1 + R2(fan)) · P_op
    P_op   = min( 工作负载需求(经降压/降频/EPP 后) ,  PL1  )

求解优先级（每一步只动「代价最小」的旋钮）：
    ① 降压到稳定值   —— 同频降功耗，性能无损（首选）
    ② 风扇拉到噪声预算上限 —— 降 R2，只增噪声（次选）
    ③ EPP 软偏置     —— 把 OS 推向节能选频，小幅降功耗（再次）
    ④ PL1 硬闸       —— 把 P 压到温度预算，牺牲性能（代价最高，最后用）
    ⑤ PL2 突增上限   —— 决定 boost 能冲多高（与 ⑥ 独立）
    ⑥ Time/τ 时长    —— 决定 boost 能持续多久，管「噪声太久」（与 ⑤ 独立）

说明：降压/EPP/PL1 的灵敏度系数来自 CPU 物理（P∝V²·f）与 i7-7700HQ 一级近似，
R1/R2 取自 detect 的实测。本模块只产出「旋钮应该设成多少」，真正的写寄存器
由运行时执行层（MSR/NBFC/nvidia-smi）负责；不同机器暴露的 MSR 不同，所以
同一条目标会同时给出 PL1 版与频率上限版两套答案，便于执行层按可用能力择一。
"""
from __future__ import annotations

import argparse
import json

# ---------------------------------------------------------------- 物理常数与默认

V0 = 1.00               # 满载典型 VID（i7-7700HQ 约 1.0V @ 3.1GHz）
P_STATIC_FRAC = 0.25    # 静态功耗占比（漏电流，只随 V² 变，不随 f）
UV_DEFAULT_MV = -80     # 默认保守降压（多数 7 代 U/H 可稳定 -50~-125mV）
TJMAX = 100.0
PIN_T = 95.0

# 三档旋钮（温度上限 / 风扇上限 / 空闲风扇底速 / 噪声取向）
#
# ⚠ 语义关键：T_target 是「允许的最高温度（ceiling）」，不是「要压到哪个值」。
#   旧版把性能档的 T_target 设成 75℃（最低），结果 P_budget=(75-24)/R 反而最小，
#   于是"性能模式"风扇最响、功率最低、频率最低 —— 语义倒挂。已修正为：
#   性能档 ceiling 最高（90℃）且风扇 100%（R 最小），因此 P_budget 最大、交付频率最高；
#   静音档 ceiling 88℃ 但风扇锁 55%，是靠"允许它热"来省噪声，而不是靠削性能。
#   平衡/静音共用 88℃ 上限：两者用「风扇 + EPP + PL2/τ」区分，而不拿温度上限区分
#   —— 把温度偏好和功率预算解耦，才不会又滑回"越静音越有性能"的倒挂。
#   不变量：P_budget(silent) < P_budget(balanced) < P_budget(performance)。
PROFILES = {
    "silent":      dict(T_target=88.0, fan_max=55, fan_idle=20,
                       label="静音", epp_bias="power",
                       desc="压低噪声：风扇不超 55%，EPP 偏节能，靠允许更高温度省噪声"),
    "balanced":    dict(T_target=88.0, fan_max=75, fan_idle=30,
                       label="平衡", epp_bias="balanced",
                       desc="默认：保性能，噪声不大不持久（风扇≤75%、空闲降到 30%）"),
    "performance": dict(T_target=90.0, fan_max=100, fan_idle=40,
                       label="性能", epp_bias="perf",
                       desc="风扇放开 + 允许温度到 90℃ → 功率预算最大、交付频率最高（仍受 95℃ 安全线约束）"),
}
# 绝对安全线（i7-7700HQ TJmax=100℃，留 5℃ 余量）
SAFETY_CEILING = 95.0

EPP_PERF, EPP_BAL, EPP_POWER = 0, 128, 200
EPP_RANGE = 255.0

# PL2（短时突增功率上限）与 Time/τ（能持续多久）是两个**独立**旋钮，必须分开。
# PL2 = P_op × 倍率（决定 burst 冲多高）；τ = 持续时间（决定 burst 撑多久）。
# 静音：PL2 倍率低 + τ 短 → 几乎不 boost，风扇不啸叫；性能：PL2 高 + τ 长 → 放开。
PL2_RATIO = {"silent": 1.15, "balanced": 1.25, "performance": 1.35}
# Intel 的 Time 字段按 log2 编码，只有 2 的幂能精确落地（8/16/32），别填 20/45
TAU_S     = {"silent": 8,    "balanced": 16,   "performance": 32}


# ---------------------------------------------------------------- 控制面分层（可控性）
# 检测跑在「出厂默认逻辑」（第 1 层）只观察；调控伸到第 2/3 层 MSR/驱动级。
# controllable=False 的旋钮软件只能识别/建议，不能改（只能物理换件或外部配件）。
CONTROL_LAYERS = [
    # layer, name, knobs, controllable, tool, note
    dict(layer=0, name="硬件固有", controllable=False,
         knobs="TDP 设计值 / R1(硅脂,压装) / 散热模组规模 / 出厂电压频率曲线",
         tool="物理改动（换硅脂/换模组）",
         note="软件不可控；R1 断链是 deep1 这台机的根因，软件只能绕开（降功率）"),
    dict(layer=1, name="Windows 电源管理（粗）", controllable=True,
         knobs="电源计划 / 最大处理器状态% / 系统散热策略(主动/被动)",
         tool="powercfg",
         note="OS 级粗杠杆；本机 PROCTHROTTLEMAX 实测无效（写回不生效）"),
    dict(layer=2, name="CPU 精细调控（细）★主力", controllable=True,
         knobs="降压UV(MSR0x150) / PL1(0x610) / PL2(0x610) / Time-τ(0x610,0x618) / "
               "EPP(0x773) / 睿频倍率(0x1AD)",
         tool="ThrottleStop 或直接写 MSR",
         note="真正生效的温控闸；全软件可控，独立于 Windows 电源计划"),
    dict(layer=3, name="GPU 精细调控（细）", controllable=True,
         knobs="功率墙% / 核心·显存频率偏移 / 电压曲线 / 温度目标",
         tool="nvidia-smi（功率墙/温度目标）或 MSI Afterburner（频率/电压曲线）",
         note="独立于 CPU；降 GPU 功耗=给共享散热腾出 CPU 余量，同样全软件可控"),
    dict(layer=4, name="外部（不可控，仅识别）", controllable=False,
         knobs="环境温度 T_amb / 散热底座·支架 / 桌面材质·遮挡",
         tool="—",
         note="软件改不了；只能当输入记录（T_amb 影响绝对预算）或建议用户改善"),
]


# ---------------------------------------------------------------- GPU 杠杆（第 3 层）
# GPU 与 CPU 共享同一散热模组，降 GPU 功耗直接给 CPU 腾热容余量。以下为推荐模板，
# 执行走 nvidia-smi/Afterburner，非 MSR。尚未接入真机 GPU 压测标定（灵敏度待校）。
GPU_LEVERS = {
    # mode: (功率墙%上限, 温度目标℃, 频率偏移MHz, 说明)
    "silent":      dict(power_pct=70, temp_target=80, clk_offset=-150,
                       desc="GPU 大幅限功率+降频，给 CPU 让出热容，噪声最低"),
    "balanced":    dict(power_pct=85, temp_target=83, clk_offset=-50,
                       desc="GPU 略限，平衡档默认：保游戏帧率同时不拖累 CPU 散热"),
    "performance": dict(power_pct=100, temp_target=86, clk_offset=0,
                       desc="GPU 放开，温度目标略压，允许噪声换帧率"),
}


# ---------------------------------------------------------------- 物理映射

def epp_to_fac(epp):
    """EPP(0..255) → 频率/功耗乘数 epp_k。0=满速，255=约 0.6 倍。"""
    epp = max(0, min(255, int(epp)))
    return 1.0 - 0.4 * (epp / EPP_RANGE)


def power_factor(V_mv_offset, f_cap_mhz, f0_mhz, epp):
    """相对「出厂满载」的功耗系数（出厂满载=1.0）。"""
    V = V0 + V_mv_offset / 1000.0
    vfac = (V / V0) ** 2
    ffac = (f_cap_mhz / f0_mhz) if (f_cap_mhz and f0_mhz) else 1.0
    ek = epp_to_fac(epp)
    dyn = (1.0 - P_STATIC_FRAC) * vfac * ffac * ek
    sta = P_STATIC_FRAC * vfac
    return sta + dyn


def r2_at(fan_duty, r2_hi, r2_lo):
    """占空比 d → R2 插值（r2_hi 低占空比，r2_lo 满档）。"""
    if r2_hi is None:
        return r2_lo
    if r2_lo is None:
        return r2_hi
    return r2_hi - (r2_hi - r2_lo) * (fan_duty / 100.0)


# ---------------------------------------------------------------- GPU 杠杆（第 3 层）

def gpu_policy(mode="balanced"):
    """GPU 精细调控推荐（执行走 nvidia-smi/Afterburner，非 MSR）。

    返回该档的功率墙%、温度目标、频率偏移与说明。GPU 与 CPU 共享散热模组，
    降 GPU 功耗=给 CPU 让出热容余量，故硅脂断链机（如 deep1）更该压 GPU。
    """
    g = GPU_LEVERS[mode]
    return dict(mode=mode, power_pct=g["power_pct"], temp_target=g["temp_target"],
                clk_offset_mhz=g["clk_offset"], desc=g["desc"])


def control_surface():
    """返回控制面分层（可控性）清单，供 UI/文档直接渲染。"""
    return CONTROL_LAYERS


# ---------------------------------------------------------------- 求解器

def optimize(features, mode="balanced", design_w=50, uv_mv=UV_DEFAULT_MV,
             f0_mhz=None, t_amb=None):
    """根据检测特征 + 选档，求解具体旋钮设置。

    返回 dict：含 undervolt_mv / fan_max / fan_idle / epp / pl1_w / pl2_w /
    tau_s / freq_cap_mhz(可选) / 预测 T_ss / 性能指数 / 分步说明。
    """
    prof = PROFILES[mode]
    T_target = min(prof["T_target"], SAFETY_CEILING)   # ceiling，不是 setpoint
    fan_max = prof["fan_max"]
    fan_idle = prof["fan_idle"]

    R1 = features.get("R1") or 0.25
    if t_amb is None:
        t_amb = features.get("t_amb") or 24.0
    P_ss = features.get("P_ss") or design_w          # 出厂满载实测功率
    f0 = f0_mhz or features.get("f_ss") or 3100.0    # 出厂满载频率

    # R2 上下界（fan_pair 实测优先，否则退而用 Rtot-R1）
    fp = features.get("fan_pair")
    if fp and fp.get("R2_hi") is not None and fp.get("R2_lo") is not None:
        r2_lo, r2_hi = fp["R2_hi"], fp["R2_lo"]      # 满档 R2 低 / 低档 R2 高
    else:
        R2 = features.get("R2")
        if R2 is None:
            Rtot = features.get("Rtot") or (R1 + 0.35)
            R2 = Rtot - R1
        r2_lo = max(R2, 0.10)
        r2_hi = R2 + 0.15

    r2 = r2_at(fan_max, r2_hi, r2_lo)
    steps = []
    pin = features.get("pin") or 0.0

    # ① 降压（性能无损）
    uv_fac = power_factor(uv_mv, None, f0, 0) / power_factor(0, None, f0, 0)
    P_uv = P_ss * uv_fac
    T_uv = t_amb + (R1 + r2) * P_uv
    steps.append(f"① 降压 {uv_mv}mV → 功耗系数 {uv_fac:.3f}（性能无损），"
                 f"风扇 {fan_max}% 时 T≈{T_uv:.1f}℃")

    epp = EPP_PERF
    pl1 = None
    freq_cap = None
    P_op = P_uv
    perf = 100.0

    if T_uv <= T_target:
        steps.append(f"② 降压+风扇已使 T≈{T_uv:.1f}℃ ≤ 目标 {T_target:.0f}℃，"
                     f"无需压功率，性能保持 100%")
        epp = EPP_BAL if prof["epp_bias"] == "balanced" else (
            EPP_POWER if prof["epp_bias"] == "power" else EPP_PERF)
    else:
        P_budget = (T_target - t_amb) / (R1 + r2)
        steps.append(f"② 仍超温（{T_uv:.1f}℃>{T_target:.0f}℃），允许功率预算 "
                     f"P_budget={P_budget:.1f}W")

        # ③ EPP 软偏置：能靠节能偏置覆盖的部分先吃 EPP
        epp_k_needed = P_budget / P_uv
        if epp_k_needed >= 0.6:                       # EPP 够压到预算
            epp = min(EPP_POWER + 30, max(0, (1.0 - epp_k_needed) / 0.4 * EPP_RANGE))
            P_op = P_budget
            perf = 100.0 * (P_budget / P_uv)          # 软调，性能近似无损
            steps.append(f"③ EPP 软偏置到 {epp:.0f} → P≈{P_op:.1f}W，"
                         f"性能≈{perf:.0f}%（无硬削频）")
        else:
            # ④ PL1 硬闸（代价最高，最后用）；同时把 EPP 推到偏节能
            epp = EPP_POWER
            pl1 = P_budget
            freq_cap = f0 * (P_budget / P_uv)         # 等效频率上限（备选执行路径）
            P_op = P_budget
            perf = 100.0 * (P_budget / P_uv)
            steps.append(f"③ EPP 推到节能 {epp:.0f} 仍不足 → ④ PL1 硬闸 "
                         f"{pl1:.1f}W（等效频率上限 {freq_cap:.0f}MHz），"
                         f"性能≈{perf:.0f}%")

    # ⑤ PL2（突增功率上限，与 ⑥ 独立）：决定 boost 能冲多高
    pl2 = round((pl1 if pl1 else P_op) * PL2_RATIO[mode], 1)
    steps.append(f"⑤ PL2 突增上限={pl2:.1f}W（=P_op×{PL2_RATIO[mode]}），"
                 f"决定短时 boost 能冲多高")
    # ⑥ Time/τ（持续时间，与 ⑤ 独立）：决定 boost 能撑多久，管「噪声太久」
    tau = TAU_S[mode]
    if pl1 is None:
        steps.append(f"⑥ Time/τ={tau}s：boost 仅短时，避免风扇长时间啸叫")
    else:
        steps.append(f"⑥ Time/τ={tau}s：PL2 维持 {tau}s 后回落到 PL1，"
                     f"限制 boost 时长以免风扇持久高转")

    T_pred = t_amb + (R1 + r2) * P_op

    return dict(
        mode=mode, label=prof["label"],
        T_target=T_target, T_predicted=round(T_pred, 1),
        R1=round(R1, 3), r2_at_fan=round(r2, 3), t_amb=t_amb,
        undervolt_mv=uv_mv,
        fan_max=fan_max, fan_idle=fan_idle,
        epp=round(epp),
        pl1_w=(round(pl1, 1) if pl1 else None),
        pl2_w=pl2, pl2_ratio=PL2_RATIO[mode], tau_s=tau,
        freq_cap_mhz=(round(freq_cap) if freq_cap else None),
        gpu=gpu_policy(mode),
        P_stock=round(P_ss, 1), P_op=round(P_op, 1),
        perf_pct=round(perf, 1),
        pin=round(pin, 2),
        steps=steps,
    )


def solve_csv(csv_path, mode="balanced", design_w=50, t_amb=24, uv_mv=UV_DEFAULT_MV):
    import os, sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from detect import detect
    res = detect(csv_path, design_w=design_w, t_amb=t_amb)
    ctrl = optimize(res["features"], mode=mode, design_w=design_w,
                    uv_mv=uv_mv, t_amb=t_amb)
    return res, ctrl


def main():
    ap = argparse.ArgumentParser(description="ThermalPilot CPU 精细调控求解")
    ap.add_argument("csv")
    ap.add_argument("--mode", choices=list(PROFILES), default="balanced")
    ap.add_argument("--design-w", type=float, default=50)
    ap.add_argument("--tamb", type=float, default=24)
    ap.add_argument("--uv-mv", type=int, default=UV_DEFAULT_MV)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    res, ctrl = solve_csv(args.csv, mode=args.mode, design_w=args.design_w,
                          t_amb=args.tamb, uv_mv=args.uv_mv)
    if args.json:
        print(json.dumps(dict(verdict=res["verdict"], scene=res["scene"],
                              control=ctrl), ensure_ascii=False, indent=1, default=str))
    else:
        print(f"判定：{res['verdict']}  | 场景：{res['scene']}  | 档位：{ctrl['label']}")
        print(f"  目标温度 {ctrl['T_target']:.0f}℃ → 预测 {ctrl['T_predicted']:.1f}℃"
              f"  |  预测性能 {ctrl['perf_pct']:.0f}%")
        print(f"  降压 {ctrl['undervolt_mv']}mV  风扇≤{ctrl['fan_max']}% 空闲{ctrl['fan_idle']}%"
              f"  EPP={ctrl['epp']}")
        print(f"  PL1={ctrl['pl1_w']}W  PL2={ctrl['pl2_w']}W(×{ctrl['pl2_ratio']})  "
              f"tau={ctrl['tau_s']}s  等效频限={ctrl['freq_cap_mhz']}MHz")
        g = ctrl["gpu"]
        print(f"  GPU(第3层): 功率墙{g['power_pct']}%  温度目标{g['temp_target']}℃  "
              f"频率偏移{g['clk_offset_mhz']}MHz")
        print("  步骤：")
        for s in ctrl["steps"]:
            print("    " + s)


if __name__ == "__main__":
    main()
