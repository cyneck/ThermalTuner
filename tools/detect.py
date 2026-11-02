#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ThermalPilot 散热问题检测识别模块（独立、无副作用、可被产品运行时直接调用）

从一段压测 CSV 里提取热链路特征，跑判定树，输出结构化的「问题定位」结论。
本模块只做「识别」，不产出报告、不写文件——报告/策略由上层负责。

物理模型（两热阻串联 + 两个时间常数）：

    T_die = T_amb + R1*P + R2(fan)*P
        R1 = die -> heatsink（硅脂/压装，与风扇无关，坏 = 换硅脂）
        R2 = heatsink -> ambient（鳍片/风量/风道，受风扇占空比影响）
        tau_die ~ 1-2 s；tau_hs ~ 80-150 s

核心可辨识事实（为什么 5 分钟就够）：
    * 稳态里 R1、R2 只以和 (R1+R2) 出现，靠温度稳态永远分不开。
    * 唯一能分开 R1/R2 的观测 = 掉载瞬间的温度塌落：
        功率先掉（die 热容 τ~1s 立即降温），散热器热容 τ~100s 还没动。
        => R1 = (T_段末 − T_过渡) / (P_段末 − P_过渡)，不需要 T_amb，5 分钟测准。

判定树（阈值来自 45W 级笔记本工程常值，可解释、可复核）：
    N1   R1 >= 0.8 K/W                          -> TIM/压装断链（换硅脂级）
    N2   R2 >= 0.7 且 ΔT(风扇60→100) < 6℃       -> 风扇/鳍片/风道
         （R2 是主判据：5 分钟档渐近线外推误差 ±2~3℃，但 R2 误差仅 ±0.1；
           ΔT 只做次级确认。贴墙时风扇被掩盖 -> 不误判为风扇故障）
    N3   pin >= 0.5 且 R1 正常                   -> 整体散热不足（温度墙）
    N4   P_ss <= 0.85×设计功率 且 不贴墙          -> 电气功率墙（PL/供电）
    N4b  早期→稳态功率回落 >=2W 且贴墙           -> 功率被温度墙压住（伴随）
    其余                                          -> 健康

用法：
    from detect import detect
    result = detect(csv_path, design_w=50, t_amb=24)
    # result = dict(features=..., findings=[...], verdict=...)

命令行：
    python tools/detect.py <csv> [--design-w 50] [--tamb 24] [--json]
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from collections import OrderedDict

# ---------------------------------------------------------------- 阈值常量（与 lab_diagnose.py 保持一致）

TJMAX = 100.0
T_REG = 96.0            # 贴墙运行时平台温度的典型值
R1_FAULT = 0.8          # K/W，超过即 TIM/压装断链
R1_SEVERE = 1.4
R1_NORMAL = 0.35
DT_FAN_WEAK = 6.0       # ℃，R2 偏高时若 ΔT 仍 >= 6 说明风扇还在起效
DT_FAN_OK = 6.0         # ℃，风扇杠杆可用判定
R2_FAN_FAULT = 0.7      # K/W，风扇侧故障主判据（R2 误差只有 ±0.1）
PIN_T = 95.0
PIN_FRAC = 0.5
P_FRAC = 0.85
SAG_W = 2.0
R1_FIXED = 0.25         # 反事实：换硅脂后的典型 R1

# 场景编码（供上层策略引擎查表）
SCENE_HEALTHY = "healthy"
SCENE_TIM = "tim_fault"          # 硅脂/压装断链
SCENE_FAN = "fan_fault"          # 风扇/鳍片/风道
SCENE_OVERALL = "overall_weak"   # 整体散热不足
SCENE_POWER_LIMIT = "power_limit"  # 电气功率墙


# ---------------------------------------------------------------- 基础工具

def fnum(x):
    try:
        s = (x or "").strip()
        return float(s) if s else None
    except (TypeError, ValueError):
        return None


def load_csv(path):
    with open(path, encoding="utf-8-sig", newline="") as fh:
        return list(csv.DictReader(fh))


def stages_of(rows):
    order, g = [], OrderedDict()
    for r in rows:
        s = r.get("stage") or "?"
        if s not in g:
            order.append(s)
            g[s] = []
        g[s].append(r)
    return order, g


def stage_kind(seg):
    loads = [fnum(r.get("cpu_load_pct") or r.get("load")) for r in seg]
    loads = [x for x in loads if x is not None]
    med = statistics.median(loads) if loads else 0.0
    # 负载标签可能是数值百分比或 'idle'/'cpu' 字符串
    labels = [str(r.get("load") or "").strip().lower() for r in seg]
    if any("idle" in x for x in labels) and not any("cpu" in x for x in labels):
        return "idle"
    if any("cpu" in x for x in labels):
        return "load"
    return "load" if med >= 50 else "idle"


def fan_of(seg):
    vals = [fnum(r.get("fan_out_pct")) for r in seg]
    vals = [x for x in vals if x is not None]
    return statistics.median(vals) if vals else None


def _mean(seg, key, lo=0):
    v = [fnum(r.get(key)) for r in seg[lo:]]
    v = [x for x in v if x is not None]
    return statistics.mean(v) if v else None


def steady(seg, frac=0.5, min_n=8):
    n = max(min_n, int(len(seg) * frac))
    tail = seg[-n:]
    tmax = [fnum(r.get("cpu_core_max_c")) for r in tail]
    tmax = [x for x in tmax if x is not None]
    pin = (sum(1 for x in tmax if x >= PIN_T) / len(tmax)) if tmax else 0.0
    return dict(
        P=_mean(tail, "cpu_power_w"), T=_mean(tail, "cpu_temp_c"),
        f=_mean(tail, "cpu_mhz"), Tmax=max(tmax) if tmax else None,
        pin=pin, n=len(tail),
        sdT=(statistics.pstdev([x for x in (fnum(r.get("cpu_temp_c")) for r in tail)
                                if x is not None]) if tail else 0.0),
    )


def fit_exp_decay(pts):
    """拟合 T(t)=A+C*e^(-t/tau)，外层对数网格搜 tau，内层对 (A,C) 线性最小二乘。"""
    if len(pts) < 8:
        return None
    ts = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    best = None
    for i in range(1, 2400):
        tau = 0.5 * (1500.0 / 0.5) ** (i / 2399.0)   # 0.5s ~ 1500s
        ph = [math.exp(-t / tau) for t in ts]
        s0 = sum(1.0 for _ in ph)
        s1 = sum(p for p in ph)
        s2 = sum(p * p for p in ph)
        b0 = sum(y for y in ys)
        b1 = sum(p * y for p, y in zip(ph, ys))
        det = s0 * s2 - s1 * s1
        if abs(det) < 1e-12:
            continue
        A = (b0 * s2 - b1 * s1) / det
        C = (s0 * b1 - b0 * s1) / det
        sse = sum((y - (A + C * p)) ** 2 for p, y in zip(ph, ys))
        if best is None or sse < best[0]:
            best = (sse, A, C, tau)
    sse, A, C, tau = best
    n = len(pts)
    return dict(A=A, C=C, tau=tau, rmse=math.sqrt(sse / n))


def steady_extrap(seg):
    """段内轨迹指数外推到渐近线 A 作为稳态；拟合不显著（已稳/贴墙）退回尾窗均值。"""
    base = steady(seg)
    dur = fnum(seg[-1].get("stage_elapsed_s")) or float(len(seg))
    # 拟合窗起点 = 功率稳定点 + 2s（躲开睿频/调节瞬态）
    settle = 0.25 * dur
    for i, r in enumerate(seg):
        P = fnum(r.get("cpu_power_w"))
        if P is None:
            continue
        tail = seg[i:i + 3]
        if abs(P - (base["P"] or P)) < 1.0 and all(
                (fnum(x.get("cpu_power_w")) is not None and
                 abs((fnum(x.get("cpu_power_w")) or 0) - (base["P"] or 0)) < 1.5)
                for x in tail):
            settle = min(fnum(seg[i].get("stage_elapsed_s")) or i, 0.6 * dur)
            break
    skip = max(10.0, settle + 2.0)
    pts = [(fnum(r.get("stage_elapsed_s")) or 0.0, fnum(r.get("cpu_temp_c")))
           for r in seg
           if (fnum(r.get("stage_elapsed_s")) or 0.0) >= skip
           and fnum(r.get("cpu_temp_c")) is not None]
    base["T_direct"] = base["T"]
    base["used_fit"] = False
    if len(pts) >= 12:
        fit = fit_exp_decay(pts)
        if fit:
            ybar = statistics.mean(p[1] for p in pts)
            rmse_const = math.sqrt(sum((p[1] - ybar) ** 2 for p in pts) / len(pts))
            if (fit["rmse"] < 0.75 * rmse_const and abs(fit["C"]) >= 3.0
                    and 10.0 <= fit["tau"] <= 1500.0 and fit["rmse"] < 1.5):
                base["T"] = fit["A"]
                base["used_fit"] = True
                base["tau_fit"] = fit["tau"]
    return base


# ---------------------------------------------------------------- 特征提取

def extract_features(rows, design_w, t_amb):
    order, g = stages_of(rows)
    kinds = {s: stage_kind(g[s]) for s in order}
    fans = {s: fan_of(g[s]) for s in order}

    load_ids = [s for s in order if kinds[s] == "load"]
    idle_ids = [s for s in order if kinds[s] == "idle"]
    if not load_ids:
        return None

    first_load = load_ids[0]
    ss1 = steady_extrap(g[first_load])

    cool_id, pre_id = None, None
    for s in load_ids:
        i = order.index(s)
        if i + 1 < len(order) and kinds[order[i + 1]] == "idle":
            cool_id, pre_id = order[i + 1], s
    if pre_id is None:
        pre_id = load_ids[-1]
    ss_main = steady_extrap(g[pre_id])

    tail6 = g[pre_id][-6:]
    P_end = _mean(tail6, "cpu_power_w")
    T_end = _mean(tail6, "cpu_temp_c")
    if cool_id is None:
        cool_id = next((s for s in order if kinds[s] == "idle"), None)

    P_ss, T_ss = ss_main["P"], ss_main["T"]

    trans = []
    if cool_id and P_end:
        for r in g[cool_id]:
            t = fnum(r.get("stage_elapsed_s")) or 0.0
            P = fnum(r.get("cpu_power_w"))
            T = fnum(r.get("cpu_temp_c"))
            if P is None or T is None:
                continue
            if 0.8 <= t <= 6.0 and P <= 0.55 * P_end:
                trans.append((P, T))
        trans = trans[:3]
    r1_samples = [(T_end - T) / (P_end - P) for P, T in trans if (P_end - P) > 3.0]
    R1 = statistics.median(r1_samples) if r1_samples else None
    R1_iqr = (statistics.quantiles(r1_samples, n=4)[2] -
              statistics.quantiles(r1_samples, n=4)[0]) if len(r1_samples) >= 4 else None

    T_cd_min = None
    if cool_id:
        v = [fnum(r.get("cpu_temp_c")) for r in g[cool_id]
             if 3.0 <= (fnum(r.get("stage_elapsed_s")) or 0) <= 15.0]
        v = [x for x in v if x is not None]
        T_cd_min = min(v) if v else None

    fan_st = {}
    for s in load_ids:
        d = fans[s]
        if d is not None:
            fan_st.setdefault(d, []).append(steady_extrap(g[s]))
    fan_pair = None
    duties = sorted(fan_st, reverse=True)
    if len(duties) >= 2:
        hi, lo = duties[0], duties[1]
        shi = fan_st[hi][0]
        slo = fan_st[lo][0]
        if abs(shi["P"] - slo["P"]) < 3.0:
            fan_pair = dict(hi=hi, lo=lo,
                            dT=slo["T"] - shi["T"], dP=shi["P"] - slo["P"],
                            R2_hi=((shi["T"] - t_amb) / shi["P"] - R1) if R1 else None,
                            R2_lo=((slo["T"] - t_amb) / slo["P"] - R1) if R1 else None)

    Rtot = (T_ss - t_amb) / P_ss if P_ss else None
    R2 = None
    if fan_pair and fan_pair["R2_hi"] is not None:
        R2 = fan_pair["R2_hi"]
    elif Rtot is not None and R1 is not None:
        R2 = Rtot - R1

    dec = None
    if cool_id:
        pts = [(fnum(r.get("stage_elapsed_s")) or 0.0, fnum(r.get("cpu_temp_c")))
               for r in g[cool_id]
               if (fnum(r.get("stage_elapsed_s")) or 0.0) >= 12.0
               and fnum(r.get("cpu_temp_c")) is not None]
        dec = fit_exp_decay(pts)

    seg1 = g[first_load]
    n_early = max(5, int(len(seg1) * 0.25))
    P_early = _mean(seg1[:n_early], "cpu_power_w")
    P_sag = (P_early - P_ss) if (P_early is not None) else None

    return dict(
        P_ss=P_ss, T_ss=T_ss, f_ss=ss1["f"], Tmax=ss_main["Tmax"],
        pin=ss_main["pin"], n_samples=len(rows),
        R1=R1, R1_iqr=R1_iqr, R1_lo=(T_ss - T_cd_min) / P_ss if T_cd_min else None,
        R2=R2, Rtot=Rtot, T_cd_min=T_cd_min,
        dec_A=dec["A"] if dec else None, dec_tau=dec["tau"] if dec else None,
        dec_rmse=dec["rmse"] if dec else None,
        fan_pair=fan_pair, P_early=P_early, P_sag=P_sag,
        design_w=design_w, t_amb=t_amb, trans_n=len(trans),
    )


# ---------------------------------------------------------------- 判定树

def decide(F):
    """跑判定树，返回 (findings, verdict)。每条 finding 含 node/sev/ok/title/text。"""
    finds = []
    R1 = F["R1"]
    if R1 is not None:
        if R1 >= R1_FAULT:
            sev = "重度" if R1 >= R1_SEVERE else "中度"
            finds.append(dict(node="N1", sev=sev, ok=False,
                              scene=SCENE_TIM,
                              title=f"TIM/压装断链（{sev}）：R1 = {R1:.2f} K/W",
                              text=(f"die 到散热器的热阻是正常值（0.1~0.35）的 "
                                    f"{R1 / 0.25:.0f} 倍。热量出不了芯片，"
                                    "风扇再大也吹不到结温。对应硬件动作：重涂硅脂 / "
                                    "重新压装散热模组。软件层无解，只能降功率保命。")))
        elif R1 > R1_NORMAL:
            finds.append(dict(node="N1", sev="早期", ok=False,
                              scene=SCENE_TIM,
                              title=f"TIM 劣化早期：R1 = {R1:.2f} K/W",
                              text="介于正常与断链之间，建议列入观察，一年内复测。"))
        else:
            finds.append(dict(node="N1", sev="-", ok=True,
                              scene=SCENE_HEALTHY,
                              title=f"TIM 正常：R1 = {R1:.2f} K/W",
                              text="die 到散热器通路健康。"))

    fp = F["fan_pair"]
    if fp is not None:
        r2h = fp["R2_hi"]
        if F["pin"] >= PIN_FRAC and fp["dT"] < DT_FAN_OK:
            finds.append(dict(node="N2", sev="被掩盖", ok=None,
                              scene=SCENE_FAN,
                              title=f"风扇杠杆被温度墙掩盖：ΔT = {fp['dT']:.1f}℃（贴墙运行）",
                              text=("结温被温控钉在墙上，风扇效果被调节回路吃掉了。"
                                    "先修主故障（N1/N3）再复测风扇杠杆。")))
        elif r2h is not None and r2h >= R2_FAN_FAULT and fp["dT"] < DT_FAN_WEAK:
            finds.append(dict(node="N2", sev="中度", ok=False,
                              scene=SCENE_FAN,
                              title=f"风扇/鳍片/风道侧：R2 = {r2h:.2f} K/W 偏高，"
                                    f"且加大风量只降 {fp['dT']:.1f}℃",
                              text=("散热器到环境的热阻明显高于正常（0.15~0.4），风量加大也压不下来。"
                                    "对应动作：清灰 / 检查风扇转速是否达标 / 出风口是否被堵。")))
        elif r2h is not None and r2h >= R2_FAN_FAULT:
            finds.append(dict(node="N2", sev="观察", ok=True,
                              scene=SCENE_FAN,
                              title=f"R2 = {r2h:.2f} K/W 偏高，但风扇仍在起效（ΔT = {fp['dT']:.1f}℃）",
                              text="可能是轻度积灰；风量尚能弥补，列入观察，半年复测。"))
        elif fp["dT"] >= DT_FAN_OK:
            finds.append(dict(node="N2", sev="-", ok=True,
                              scene=SCENE_HEALTHY,
                              title=f"风扇杠杆可用：ΔT(风扇60→100) = {fp['dT']:.1f}℃",
                              text="风扇是本机有效的软件控制通道，值得围绕它做策略。"))
        else:
            finds.append(dict(node="N2", sev="弱", ok=True,
                              scene=SCENE_HEALTHY,
                              title=f"风扇杠杆偏弱：ΔT = {fp['dT']:.1f}℃",
                              text="介于正常与故障之间，结合 R2 数值判断。"))
    else:
        finds.append(dict(node="N2", sev="未测", ok=None,
                          scene=None,
                          title="风扇杠杆未测（计划里没有风扇档位段）",
                          text="用 lab/plan-diag5-v1.json 这类含风扇档位的计划可测。"))

    if F["pin"] >= PIN_FRAC and (R1 is None or R1 < R1_FAULT):
        finds.append(dict(node="N3", sev="中度", ok=False,
                          scene=SCENE_OVERALL,
                          title=f"整体散热不足：{F['pin'] * 100:.0f}% 样本贴 {PIN_T:.0f}℃ 运行",
                          text=("R1 与风扇都正常仍贴温度墙：散热规模对这台机器的功率设计不够，"
                                "或环境温度过高。软件层必须压功率。")))

    if (F["P_ss"] is not None and F["P_ss"] <= P_FRAC * F["design_w"]
            and F["pin"] < PIN_FRAC and (F["P_sag"] or 0) < SAG_W):
        finds.append(dict(node="N4", sev="信息", ok=False,
                          scene=SCENE_POWER_LIMIT,
                          title=f"电气功率墙：稳态 {F['P_ss']:.1f}W < 设计 {F['design_w']:.0f}W×{P_FRAC:.0%}，"
                                "且不贴温度墙",
                          text=("温度远没到墙，功率却上不去：PL/供电/适配器限制。"
                                "散热没问题，性能释放被电气参数卡住。")))

    if (F["P_sag"] or 0) >= SAG_W and F["pin"] >= PIN_FRAC:
        finds.append(dict(node="N4b", sev="伴随", ok=False,
                          scene=SCENE_OVERALL,
                          title=f"功率被温度墙压住：早期 {F['P_early']:.1f}W → 稳态 {F['P_ss']:.1f}W"
                                f"（回落 {F['P_sag']:.1f}W）且贴墙",
                          text=("这是热的墙不是电的墙：给多少功率预算都会被结温顶回来。"
                                "修好散热链路后这部分性能会自己回来。")))

    faults = [f for f in finds if f["ok"] is False and f["node"] in ("N1", "N2", "N3", "N4")]
    verdict = faults[0]["title"] if faults else "健康：热链路各环节正常"

    # 主场景（供策略引擎选档）：第一个真实故障，否则健康
    primary = next((f["scene"] for f in finds
                    if f["ok"] is False and f.get("scene")), SCENE_HEALTHY)
    return finds, verdict, primary


# ---------------------------------------------------------------- 对外入口

def detect(csv_path, design_w=50, t_amb=24):
    """主入口：输入 CSV 路径，返回结构化检测结果。"""
    rows = load_csv(csv_path)
    F = extract_features(rows, design_w, t_amb)
    if F is None:
        return dict(features=None, findings=[], verdict="无法识别：缺少负载段", scene=SCENE_HEALTHY)
    finds, verdict, primary = decide(F)
    return dict(features=F, findings=finds, verdict=verdict, scene=primary)


def main():
    ap = argparse.ArgumentParser(description="ThermalPilot 散热问题检测识别")
    ap.add_argument("csv")
    ap.add_argument("--design-w", type=float, default=50)
    ap.add_argument("--tamb", type=float, default=24)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    res = detect(args.csv, design_w=args.design_w, t_amb=args.tamb)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=1, default=str))
    else:
        print("判定：", res["verdict"])
        print("主场景：", res["scene"])
        for f in res["findings"]:
            print(f"  [{f['node']}] {f['sev']:<4} {f['title']}")


if __name__ == "__main__":
    main()
