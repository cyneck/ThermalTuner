#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ThermalPilot 三档散热策略模型（静音 / 平衡 / 性能）

输入：检测模块（tools/detect.py）输出的 scene + features
输出：三个档位的目标温度、功率预算、风扇档位映射 + 推荐档（默认平衡）

设计原则（用户要求）：
    * 静音   —— 优先压低噪声：限制风扇占空比上限，温度可以高一点，功率略降。
    * 平衡   —— 默认档：保持性能，同时散热噪声「不要太大、不要太久」。
    * 性能   —— 优先释放性能：风扇放开，温度压到最低，允许噪声。

物理模型（与 detect.py 一致）：
    T_die = T_amb + (R1 + R2(fan)) * P
    风扇只影响 R2；R2 随占空比 d 近似线性下降：R2(d) = r2_hi - (r2_hi - r2_lo)*(d/100)

三档的「差异」落在三个旋钮上：
    T_target   目标温度（决定功率预算）
    fan_max    风扇占空比上限（决定噪声上限）
    fan_idle   低负载时的风扇底速（决定「噪声持续多久」）
"""
from __future__ import annotations

import argparse
import json

# ---------------------------------------------------------------- 场景与档位

# 三档旋钮（目标温度 / 风扇上限 / 空闲风扇底速）
PROFILES = {
    "silent":      dict(T_target=88.0, fan_max=55, fan_idle=20,
                       label="静音", desc="压低噪声，温度可略高，功率略降"),
    "balanced":    dict(T_target=82.0, fan_max=75, fan_idle=30,
                       label="平衡", desc="保持性能，噪声不要太大太久（默认）"),
    "performance": dict(T_target=75.0, fan_max=100, fan_idle=40,
                       label="性能", desc="风扇放开，温度压最低，允许噪声"),
}

# 硬件安全硬约束
T_HARD_MAX = 95.0        # 结温硬顶（超过无条件降档）
T_AMBIENT_DEFAULT = 24.0

# 每个故障场景下，软件可控的「降功率手段」排序（cap 在部分机器失效时自动跳过）
# 返回该场景的降功率手段优先级与说明
SCENE_ACTION = {
    "tim_fault":     dict(title="TIM/压装断链",
                          hw="重涂硅脂 / 重新压装散热模组（一次到位，收益最大）",
                          sw="软件只能降功率保命：限睿频 → 限负载 → 降目标温度"),
    "fan_fault":     dict(title="风扇/鳍片/风道",
                          hw="清灰 / 检查风扇转速 / 疏通出风口",
                          sw="软件先降功率避开过热，风扇杠杆修复前不硬拉转速"),
    "overall_weak":  dict(title="整体散热不足",
                          hw="散热规模不够或环境温度过高，考虑散热底座/降低室温",
                          sw="软件压功率到散热能力之内"),
    "power_limit":   dict(title="电气功率墙",
                          hw="散热无恙，查 PL/供电/适配器限制",
                          sw="软件可适当放开功耗墙/睿频设置"),
    "healthy":       dict(title="健康",
                          hw="无需硬件动作",
                          sw="软件可做精细的性能/温度/噪声平衡优化"),
}


# ---------------------------------------------------------------- 核心计算

def r2_at(fan_duty, r2_hi, r2_lo):
    """给定占空比 d（0~100），插值 R2。r2_hi 对应低占空比，r2_lo 对应满档。"""
    if r2_hi is None:
        return r2_lo
    if r2_lo is None:
        return r2_hi
    return r2_hi - (r2_hi - r2_lo) * (fan_duty / 100.0)


def power_budget(T_target, fan_duty, R1, r2_hi, r2_lo, t_amb):
    """在目标温度 + 风扇占空比下，允许的功率（W）。"""
    r2 = r2_at(fan_duty, r2_hi, r2_lo)
    return (T_target - t_amb) / (R1 + r2)


def build_policy(features, scene):
    """根据检测特征 + 场景，生成三档散热策略。"""
    R1 = features.get("R1") or 0.25
    t_amb = features.get("t_amb") or T_AMBIENT_DEFAULT
    # R2 上下界：fan_pair 有则用实测，否则用 Rtot-R1 估算
    fp = features.get("fan_pair")
    if fp and fp.get("R2_hi") is not None and fp.get("R2_lo") is not None:
        # fan_pair 语义：R2_hi = 风扇满档（高占空比）下的 R2（应更低）；
        #                R2_lo = 低占空比下的 R2（应更高）。
        # 本模块用 r2_lo 表示「满档 R2(低)」，r2_hi 表示「低档 R2(高)」，故交换。
        r2_lo, r2_hi = fp["R2_hi"], fp["R2_lo"]
    else:
        R2 = features.get("R2")
        if R2 is None:
            Rtot = features.get("Rtot") or (R1 + 0.35)
            R2 = Rtot - R1
        r2_lo = max(R2, 0.10)
        r2_hi = R2 + 0.15          # 低占空比时 R2 略高（无实测时的默认假设）

    profiles = {}
    for name, prof in PROFILES.items():
        T_tgt = prof["T_target"]
        fan_max = prof["fan_max"]
        # 故障场景的温度目标修正：硅脂断链/整体不足时，目标温度被热阻锁死，
        # 硬要压到 75℃ 需要把功率砍到不现实 → 对故障场景放宽目标温度并降低功率预算
        p_full = power_budget(T_tgt, fan_max, R1, r2_hi, r2_lo, t_amb)
        p_idle = power_budget(T_tgt, prof["fan_idle"], R1, r2_hi, r2_lo, t_amb)

        # 贴墙机器（pin 高）实际功率被温度墙削过，预算取「物理允许」与「当前实测」的较小者
        P_ss = features.get("P_ss")
        p_allow = p_full
        if P_ss is not None and scene in ("tim_fault", "overall_weak"):
            # 故障机：不要让功率预算超过当前已贴墙的实测功率，避免误导
            p_allow = min(p_full, P_ss * 1.05)

        # 精细调控求解：把目标温度/噪声预算翻译成具体 CPU+风扇旋钮
        try:
            import os, sys
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            from control_model import optimize as _opt
            ctrl = _opt(features, mode=name, design_w=features.get("design_w") or 50,
                        t_amb=t_amb)
        except Exception:
            ctrl = None

        profiles[name] = dict(
            label=prof["label"], desc=prof["desc"],
            T_target=T_tgt, fan_max=fan_max, fan_idle=prof["fan_idle"],
            p_budget=round(p_allow, 1), p_budget_idle=round(p_idle, 1),
            controls=ctrl,
        )

    # 推荐档：默认平衡；故障场景（硅脂/整体）在未修复前建议静音档保命
    recommended = "balanced"
    if scene in ("tim_fault", "overall_weak"):
        recommended = "silent"

    return dict(
        scene=scene,
        scene_info=SCENE_ACTION.get(scene, SCENE_ACTION["healthy"]),
        recommended=recommended,
        profiles=profiles,
        r1=R1, r2_lo=r2_lo, r2_hi=r2_hi, t_amb=t_amb,
    )


# ---------------------------------------------------------------- 入口

def solve(csv_path, design_w=50, t_amb=24, mode="balanced"):
    """一站式：检测 + 三档策略。mode 可选 silent/balanced/performance。"""
    import os
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from detect import detect
    res = detect(csv_path, design_w=design_w, t_amb=t_amb)
    pol = build_policy(res["features"], res["scene"])
    return res, pol


def main():
    ap = argparse.ArgumentParser(description="ThermalPilot 三档散热策略")
    ap.add_argument("csv")
    ap.add_argument("--design-w", type=float, default=50)
    ap.add_argument("--tamb", type=float, default=24)
    ap.add_argument("--mode", choices=["silent", "balanced", "performance"],
                    default="balanced")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    res, pol = solve(args.csv, design_w=args.design_w, t_amb=args.tamb,
                     mode=args.mode)
    if args.json:
        print(json.dumps(dict(verdict=res["verdict"], scene=res["scene"],
                              policy=pol), ensure_ascii=False, indent=1, default=str))
    else:
        print("判定：", res["verdict"])
        print("场景：", pol["scene_info"]["title"])
        print("推荐档：", pol["recommended"])
        print(f"  R1={pol['r1']:.2f}  R2_lo={pol['r2_lo']:.2f}  R2_hi={pol['r2_hi']:.2f}")
        for name, p in pol["profiles"].items():
            mark = " ←推荐" if name == pol["recommended"] else ""
            c = p.get("controls") or {}
            print(f"  [{name:<11}] 目标{p['T_target']:.0f}℃ "
                  f"风扇≤{p['fan_max']}% 空闲{p['fan_idle']}% "
                  f"功率预算{p['p_budget']}W 预测性能{c.get('perf_pct','?')}%{mark}")
            if c:
                print(f"       降压{c['undervolt_mv']}mV EPP={c['epp']} "
                      f"PL1={c['pl1_w']}W PL2={c['pl2_w']}W tau={c['tau_s']}s "
                      f"等效频限={c['freq_cap_mhz']}MHz")


if __name__ == "__main__":
    main()
