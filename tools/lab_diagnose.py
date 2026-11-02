#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ThermalPilot 热链路诊断模型（5 分钟级压测 → 故障环节定位 → 降级运转策略）

物理模型（两热阻串联 + 两个时间常数）：

    T_die = T_amb + R1*P + R2(fan)*P
    |    R1 = die -> heatsink（硅脂/压装，与风扇无关，坏 = 换硅脂）
    |    R2 = heatsink -> ambient（鳍片/风量/风道，受风扇占空比影响）
    |    tau_die ~ 1-2 s（die 热容，秒级到达稳态）
    |    tau_hs  ~ 80-150 s（散热器热容，阶段间漂移）

可从一段压测数据里**无歧义**提取的特征（不依赖 T_amb）：
    R1_trans = (T_ss - T_transition) / (P_ss - P_transition)
        取载荷->空闲切换后 2~12 s 内的干净样本：功率已掉、散热器还没来得及冷。
        这是唯一能把 R1 与 R2 分开的观测——稳态里两者只以和 R1+R2 出现。
    fan_leverage = T_ss(fan60) - T_ss(fan100)（同功率）
    pin_frac    = 稳态窗内 Tmax>=95 的比例（贴温度墙运行）
    P_sag       = 早期功率 - 稳态功率（>2W 且贴墙 => 功率被温度墙压住，不是电气 PL）

决策树（阈值来自 45W 级笔记本工程常值，可解释、可复核）：
    N1  R1 >= 0.8 K/W                          -> TIM/压装断链（换硅脂级问题）
    N2  R2 >= 0.7 且 ΔT(风扇60->100) < 6       -> 风扇/鳍片/风道
        （R2 是主判据：5 分钟档渐近线外推误差 ±2~3℃，但 R2 误差只有 ±0.1；
          ΔT 只做次级确认——R2 高且加大风量仍无效才是风扇侧问题）
    N3  pin >= 0.5 且 R1 正常                   -> 整体散热不足（温度墙）
    N4  P_ss <= 0.85*设计功率 且 无贴墙          -> 电气功率墙（PL/供电）
    N5  其余                                    -> 健康

输出：lab-data/report-diag.html（合并报告）+ lab-data/diag-<tag>.json（供程序读取）

用法：
    python tools/lab_diagnose.py --selftest [--real <csv>] [--tamb 24] [--design-w 50]
    python tools/lab_diagnose.py <csv> [--tag x] [--tamb 24] [--design-w 45]
"""
import argparse
import csv
import json
import math
import os
import random
import statistics
from collections import OrderedDict

TJMAX = 100.0
T_REG = 96.0            # 贴墙运行时平台温度的典型值
R1_FAULT = 0.8          # K/W，超过即 TIM/压装断链
R1_SEVERE = 1.4
R1_NORMAL = 0.35
DT_FAN_WEAK = 6.0       # ℃，R2 偏高时若 ΔT 仍 >= 6 说明风扇还在起效
DT_FAN_OK = 6.0         # ℃，风扇杠杆可用的判定（5 分钟档测量噪声 ±3℃ 已计入）
R2_FAN_FAULT = 0.7      # K/W，风扇侧故障的主判据（R2 误差只有 ±0.1）
PIN_T = 95.0
PIN_FRAC = 0.5
P_FRAC = 0.85
SAG_W = 2.0
R1_FIXED = 0.25         # 反事实：换硅脂后的典型 R1


# ---------------------------------------------------------------- csv / stages

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
    g = OrderedDict()
    for r in rows:
        g.setdefault(r["stage"], []).append(r)
    return list(g), g


def stage_kind(seg):
    ls = [fnum(r["cpu_load_pct"]) for r in seg[1:5]]
    ls = [x for x in ls if x is not None]
    if not ls:
        ls = [fnum(seg[0]["cpu_load_pct"]) or 0.0]
    return "load" if statistics.median(ls) > 40 else "idle"


def fan_of(seg):
    fs = [fnum(r["fan_out_pct"]) for r in seg]
    fs = [x for x in fs if x is not None]
    return statistics.median(fs) if fs else None


def _mean(seg, key, lo=0):
    v = [fnum(r[key]) for r in seg[lo:]]
    v = [x for x in v if x is not None]
    return statistics.mean(v) if v else None


def steady(seg, frac=0.5, min_n=8):
    n = max(min_n, int(len(seg) * frac))
    tail = seg[-n:]
    tmax = [fnum(r["cpu_core_max_c"]) for r in tail]
    tmax = [x for x in tmax if x is not None]
    pin = (sum(1 for x in tmax if x >= PIN_T) / len(tmax)) if tmax else 0.0
    return dict(
        P=_mean(tail, "cpu_power_w"), T=_mean(tail, "cpu_temp_c"),
        f=_mean(tail, "cpu_mhz"), Tmax=max(tmax) if tmax else None,
        pin=pin, n=len(tail),
        sdT=(statistics.stdev([x for x in (fnum(r["cpu_temp_c"]) for r in tail)
                               if x is not None]) or 0.0),
    )


def steady_extrap(seg):
    """5 分钟档 tau_hs~80-150s 走不完：对段内轨迹拟合 T=A+C*exp(-t/tau)，
    指数外推到渐近线 A 作为稳态；拟合不显著（已稳/贴墙）则退回尾窗均值。
    拟合窗起点 = 功率稳定点 + 2s（躲开睿频/调节瞬态；无瞬态则提前到 10s）。"""
    base = steady(seg)
    dur = fnum(seg[-1]["stage_elapsed_s"]) or float(len(seg))
    settle = 0.25 * dur
    if base["P"] is not None:
        for i, r in enumerate(seg):
            P = fnum(r["cpu_power_w"])
            if P is None:
                continue
            if abs(P - base["P"]) < 1.0 and all(
                    (fnum(x["cpu_power_w"]) is not None
                     and abs(fnum(x["cpu_power_w"]) - base["P"]) < 1.5)
                    for x in seg[i:i + 3]):
                settle = min(fnum(seg[i]["stage_elapsed_s"]) or float(i), 0.6 * dur)
                break
    skip = max(10.0, settle + 2.0)
    pts = []
    for r in seg:
        t = fnum(r["stage_elapsed_s"]) or 0.0
        T = fnum(r["cpu_temp_c"])
        if t >= skip and T is not None:
            pts.append((t, T))
    base["T_direct"] = base["T"]
    base["used_fit"] = False
    if len(pts) >= 12:
        fit = fit_exp_decay(pts)
        if fit:
            ybar = statistics.mean(p[1] for p in pts)
            rmse_const = math.sqrt(sum((p[1] - ybar) ** 2 for p in pts) / len(pts))
            if (fit["rmse"] < 0.75 * rmse_const and abs(fit["C"]) >= 3.0
                    and 10.0 <= fit["tau"] <= 900.0 and fit["rmse"] < 1.5):
                base["T"] = fit["A"]
                base["used_fit"] = True
                base["tau_fit"] = fit["tau"]
    return base


# ---------------------------------------------------------------- features

def fit_exp_decay(pts):
    """T = A + C*exp(-t/tau)；外层搜 tau，内层 (A,C) 线性最小二乘。"""
    if len(pts) < 5:
        return None
    ts = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    best = None
    for i in range(1200):
        tau = 10.0 * (800.0 / 10.0) ** (i / 1199.0)
        es = [math.exp(-t / tau) for t in ts]
        s1, s2 = float(len(ts)), sum(e * e for e in es)
        s12 = sum(es)
        b1, b2 = sum(ys), sum(e * y for e, y in zip(es, ys))
        det = s1 * s2 - s12 * s12
        if abs(det) < 1e-12:
            continue
        A = (b1 * s2 - b2 * s12) / det
        C = (b2 * s1 - b1 * s12) / det
        sse = sum((y - (A + C * e)) ** 2 for y, e in zip(ys, es))
        if best is None or sse < best[0]:
            best = (sse, A, C, tau)
    if best is None:
        return None
    sse, A, C, tau = best
    return dict(A=A, C=C, tau=tau,
                rmse=math.sqrt(sse / len(pts)), n=len(pts))


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

    # 冷却段 = 最后一个载荷段之后的空闲段（R1/R2 分解的唯一观测）
    cool_id, pre_id = None, None
    for s in load_ids:
        i = order.index(s)
        if i + 1 < len(order) and kinds[order[i + 1]] == "idle":
            cool_id, pre_id = order[i + 1], s
    if pre_id is None:
        pre_id = load_ids[-1]
    ss_main = steady_extrap(g[pre_id])

    # R1 分解要用「段末实际状态」：过渡塌落是相对当前 die 温度，不是相对平衡态
    tail6 = g[pre_id][-6:]
    P_end = _mean(tail6, "cpu_power_w")
    T_end = _mean(tail6, "cpu_temp_c")
    if cool_id is None:
        cool_id = next((s for s in order if kinds[s] == "idle"), None)

    P_ss, T_ss = ss_main["P"], ss_main["T"]

    # --- 过渡样本：掉载后 0.8~6 s，P <= 0.55*P_end（散热器还热、die 已掉；
    #     窗口收窄到 6s 是为了限制冷却漂移对 R1 的正偏置）
    trans = []
    if cool_id and P_end:
        for r in g[cool_id]:
            t = fnum(r["stage_elapsed_s"]) or 0.0
            P = fnum(r["cpu_power_w"])
            T = fnum(r["cpu_temp_c"])
            if P is None or T is None:
                continue
            if 0.8 <= t <= 6.0 and P <= 0.55 * P_end:
                trans.append((P, T))
        trans = trans[:3]
    r1_samples = [(T_end - T) / (P_end - P) for P, T in trans if (P_end - P) > 3.0]
    R1 = statistics.median(r1_samples) if r1_samples else None
    R1_iqr = (statistics.quantiles(r1_samples, n=4)[2] -
              statistics.quantiles(r1_samples, n=4)[0]) if len(r1_samples) >= 4 else None

    # --- 冷却平台（朴素下界：把平台当 T_hs，会低估 R1）
    T_cd_min = None
    if cool_id:
        v = [fnum(r["cpu_temp_c"]) for r in g[cool_id]
             if 3.0 <= (fnum(r["stage_elapsed_s"]) or 0) <= 15.0]
        v = [x for x in v if x is not None]
        T_cd_min = min(v) if v else None

    # --- 风扇对（同负载不同占空比，稳态用外推渐近线）
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
    # R2 优先用风扇满档段的分解（外推稳态最准），否则用总量减 R1
    R2 = None
    if fan_pair and fan_pair["R2_hi"] is not None:
        R2 = fan_pair["R2_hi"]
    elif Rtot is not None and R1 is not None:
        R2 = Rtot - R1

    # --- 冷却指数拟合 -> tau_hs 与空闲稳态 A
    dec = None
    if cool_id:
        pts = []
        for r in g[cool_id]:
            t = fnum(r["stage_elapsed_s"]) or 0.0
            T = fnum(r["cpu_temp_c"])
            if t >= 12.0 and T is not None:
                pts.append((t, T))
        dec = fit_exp_decay(pts)

    # --- 早期功率（睿频预算 / 温度墙压功率的证据）
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


# ---------------------------------------------------------------- decision tree

def decide(F):
    finds = []
    R1 = F["R1"]
    if R1 is not None:
        if R1 >= R1_FAULT:
            sev = "重度" if R1 >= R1_SEVERE else "中度"
            finds.append(dict(node="N1", sev=sev, ok=False,
                              title=f"TIM/压装断链（{sev}）：R1 = {R1:.2f} K/W",
                              text=(f"die 到散热器的热阻是正常值（0.1~0.35）的 "
                                    f"{R1 / 0.25:.0f} 倍。热量出不了芯片，"
                                    "风扇再大也吹不到结温。对应硬件动作：重涂硅脂 / "
                                    "重新压装散热模组。软件层无解，只能降功率保命。")))
        elif R1 > R1_NORMAL:
            finds.append(dict(node="N1", sev="早期", ok=False,
                              title=f"TIM 劣化早期：R1 = {R1:.2f} K/W",
                              text="介于正常与断链之间，建议列入观察，一年内复测。"))
        else:
            finds.append(dict(node="N1", sev="-", ok=True,
                              title=f"TIM 正常：R1 = {R1:.2f} K/W",
                              text="die 到散热器通路健康。"))

    fp = F["fan_pair"]
    if fp is not None:
        r2h = fp["R2_hi"]
        if F["pin"] >= PIN_FRAC and fp["dT"] < DT_FAN_OK:
            finds.append(dict(node="N2", sev="被掩盖", ok=None,
                              title=f"风扇杠杆被温度墙掩盖：ΔT = {fp['dT']:.1f}℃（贴墙运行）",
                              text=("结温被温控钉在墙上，风扇效果被调节回路吃掉了。"
                                    "先修主故障（N1/N3）再复测风扇杠杆。")))
        elif r2h is not None and r2h >= R2_FAN_FAULT and fp["dT"] < DT_FAN_WEAK:
            finds.append(dict(node="N2", sev="中度", ok=False,
                              title=f"风扇/鳍片/风道侧：R2 = {r2h:.2f} K/W 偏高，"
                                    f"且加大风量只降 {fp['dT']:.1f}℃",
                              text=("散热器到环境的热阻明显高于正常（0.15~0.4），风量加大也压不下来。"
                                    "对应动作：清灰 / 检查风扇转速是否达标 / 出风口是否被堵。")))
        elif r2h is not None and r2h >= R2_FAN_FAULT:
            finds.append(dict(node="N2", sev="观察", ok=True,
                              title=f"R2 = {r2h:.2f} K/W 偏高，但风扇仍在起效（ΔT = {fp['dT']:.1f}℃）",
                              text="可能是轻度积灰；风量尚能弥补，列入观察，半年复测。"))
        elif fp["dT"] >= DT_FAN_OK:
            finds.append(dict(node="N2", sev="-", ok=True,
                              title=f"风扇杠杆可用：ΔT(风扇60→100) = {fp['dT']:.1f}℃",
                              text="风扇是本机有效的软件控制通道，值得围绕它做策略。"))
        else:
            finds.append(dict(node="N2", sev="弱", ok=True,
                              title=f"风扇杠杆偏弱：ΔT = {fp['dT']:.1f}℃",
                              text="介于正常与故障之间，结合 R2 数值判断。"))
    else:
        finds.append(dict(node="N2", sev="未测", ok=None,
                          title="风扇杠杆未测（计划里没有风扇档位段）",
                          text="用 lab/plan-diag5-v1.json 这类含风扇档位的计划可测。"))

    if F["pin"] >= PIN_FRAC and (R1 is None or R1 < R1_FAULT):
        finds.append(dict(node="N3", sev="中度", ok=False,
                          title=f"整体散热不足：{F['pin'] * 100:.0f}% 样本贴 {PIN_T:.0f}℃ 运行",
                          text=("R1 与风扇都正常仍贴温度墙：散热规模对这台机器的功率设计不够，"
                                "或环境温度过高。软件层必须压功率。")))

    if (F["P_ss"] is not None and F["P_ss"] <= P_FRAC * F["design_w"]
            and F["pin"] < PIN_FRAC and (F["P_sag"] or 0) < SAG_W):
        finds.append(dict(node="N4", sev="信息", ok=False,
                          title=f"电气功率墙：稳态 {F['P_ss']:.1f}W < 设计 {F['design_w']:.0f}W×{P_FRAC:.0%}，"
                                "且不贴温度墙",
                          text=("温度远没到墙，功率却上不去：PL/供电/适配器限制。"
                                "散热没问题，性能释放被电气参数卡住。")))

    if (F["P_sag"] or 0) >= SAG_W and F["pin"] >= PIN_FRAC:
        finds.append(dict(node="N4b", sev="伴随", ok=False,
                          title=f"功率被温度墙压住：早期 {F['P_early']:.1f}W → 稳态 {F['P_ss']:.1f}W"
                                f"（回落 {F['P_sag']:.1f}W）且贴墙",
                          text=("这是热的墙不是电的墙：给多少功率预算都会被结温顶回来。"
                                "修好散热链路后这部分性能会自己回来。")))

    faults = [f for f in finds if f["ok"] is False and f["node"] in ("N1", "N2", "N3", "N4")]
    verdict = faults[0]["title"] if faults else "健康：热链路各环节正常"
    return finds, verdict


# ---------------------------------------------------------------- policy

def policy_of(F, fan_ok_note=True):
    """给定诊断结果，给出 降级运转策略 + 反事实（换硅脂）。"""
    R1 = F["R1"] if F["R1"] is not None else R1_FIXED
    R2 = F["R2"] if F["R2"] is not None else 0.35
    Tamb = F["t_amb"]
    P_ss = F["P_ss"]
    fp = F["fan_pair"]
    r2_lo = fp["R2_hi"] if (fp and fp["R2_hi"]) else R2   # 风扇全开时
    r2_hi = fp["R2_lo"] if (fp and fp["R2_lo"]) else R2 + 0.15

    def p_allow(target, r2):
        return (target - Tamb) / (R1 + r2)

    rows = []
    for target in (95.0, 90.0, 85.0):
        rows.append(dict(target=target,
                         p100=p_allow(target, r2_lo),
                         p60=p_allow(target, min(r2_hi, r2_lo + 0.15)),
                         t_now=Tamb + (R1 + r2_lo) * P_ss if P_ss else None))

    cf_t = Tamb + (R1_FIXED + r2_lo) * P_ss if P_ss else None
    cf_p90 = p_allow(90.0, r2_lo) + (R1 - R1_FIXED)  # 换硅脂后在同风扇下的 90℃ 预算
    return dict(rows=rows, r2_lo=r2_lo, r2_hi=r2_hi,
                cf=dict(r1=R1_FIXED, t_at_p=cf_t, p90=cf_p90,
                        t_design=Tamb + (R1_FIXED + r2_lo) * F["design_w"]))


# ---------------------------------------------------------------- simulation

SIM_STAGES = [
    ("G0", 20, "idle", 100),
    ("G1", 90, "cpu", 100),
    ("G2", 120, "cpu", 60),
    ("G3", 45, "cpu", 100),
    ("G4", 45, "idle", 100),
]

SCENARIOS = {
    "sim-healthy": dict(R1=0.18, r2a=1.10, r2b=0.005, P=50, P_turbo=56,
                        turbo_s=28, regulate=False, design=50, note="健康对照：50W 设计全额释放"),
    "sim-fanfault": dict(R1=0.20, r2a=1.50, r2b=0.001, P=30, P_turbo=30,
                         turbo_s=0, regulate=False, design=35,
                         note="风扇/风道侧故障对照：R2 偏高且对占空比不敏感"),
    "sim-timfault": dict(R1=1.85, r2a=0.28, r2b=0.001, P=50, P_turbo=40,
                         turbo_s=60, regulate=True, design=50,
                         note="硅脂干涸对照（按真实机参数）：温度墙限功率"),
    "sim-pllimit": dict(R1=0.20, r2a=1.00, r2b=0.004, P=28, P_turbo=28,
                        turbo_s=0, regulate=False, design=50,
                        note="电气功率墙对照：温度健康、功率上不去"),
}


def sim_case(name, seed=7):
    c = SCENARIOS[name]
    rng = random.Random(seed)
    Tamb = 24.0
    P_idle = 10.0
    R1, r2a, r2b = c["R1"], c["r2a"], c["r2b"]

    def R2(d):
        return r2a - r2b * (d if d is not None else 80)

    def tau_hs(d):
        return 140.0 - 0.6 * (d if d is not None else 80)

    T_hs = Tamb + R2(100) * P_idle   # 冷启动等效
    rows = []
    t_total = 0
    for sid, sec, load, duty in SIM_STAGES:
        for k in range(sec):
            t_total += 1
            Pw = P_idle if load == "idle" else c["P"]
            if load == "cpu" and k < c["turbo_s"]:
                Pw = max(Pw, c["P_turbo"])
            if c["regulate"]:
                # 温度墙限功率：慢速调节器（先允许睿频冲高，结温贴墙后逐步削功率）
                if k == 0:
                    p_cap = [c["P_turbo"]]
                if T_hs + R1 * min(Pw, p_cap[0]) > T_REG:
                    p_cap[0] = max(12.0, p_cap[0] - 0.25)
                else:
                    p_cap[0] = min(c["P_turbo"], p_cap[0] + 0.1)
                Pw = min(Pw, p_cap[0])
            T_hs += (Tamb + R2(duty) * Pw - T_hs) / tau_hs(duty)
            T_die = T_hs + R1 * Pw + rng.gauss(0, 0.4)
            T_die = min(T_die, 99.0)
            rows.append(dict(
                timestamp=f"2026-10-28 20:00:{t_total % 60:02d}",
                elapsed_s=f"{t_total}.0", stage=sid, stage_elapsed_s=f"{k}.0",
                load=load, cpu_temp_c=f"{T_die:.1f}",
                cpu_core_max_c=f"{T_die + rng.gauss(0.4, 0.3):.1f}",
                cpu_volt_v="1.100", cpu_load_pct="100.0" if load == "cpu" else "6.0",
                cpu_mhz="3400" if load == "cpu" else "800",
                cpu_power_w=f"{Pw + rng.gauss(0, 0.3):.2f}",
                gpu_temp_c="40.0", gpu_load_pct="0.0", gpu_power_w="0.0",
                fan_rpm="", fan_out_pct="" if duty is None else f"{duty}",
                proc_cap="100", boost="2",
            ))
    return rows, c


# ---------------------------------------------------------------- report

CSS = """
body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;margin:0;background:#f1f5f9;color:#0f172a}
.wrap{max-width:980px;margin:0 auto;padding:24px 16px 60px}
h1{font-size:22px;margin:0 0 4px} h2{font-size:17px;margin:28px 0 10px;border-left:4px solid #2563eb;padding-left:8px}
.sub{color:#64748b;font-size:13px;margin-bottom:18px}
table{border-collapse:collapse;width:100%;font-size:13px;background:#fff}
th,td{border:1px solid #cbd5e1;padding:5px 8px;text-align:center}
th{background:#e2e8f0}
.card{background:#fff;border:1px solid #cbd5e1;border-radius:10px;padding:14px 16px;margin:14px 0}
.badge{display:inline-block;padding:2px 10px;border-radius:12px;font-size:12px;font-weight:600}
.b-bad{background:#fee2e2;color:#991b1b}.b-warn{background:#fef3c7;color:#92400e}
.b-ok{background:#dcfce7;color:#166534}.b-info{background:#dbeafe;color:#1e40af}
.find{margin:6px 0;padding:6px 10px;border-radius:6px;font-size:13px}
.f-bad{background:#fef2f2;border-left:4px solid #dc2626}
.f-ok{background:#f0fdf4;border-left:4px solid #16a34a}
.f-info{background:#eff6ff;border-left:4px solid #2563eb}
.muted{color:#64748b;font-size:12px}
code{background:#e2e8f0;padding:1px 4px;border-radius:3px;font-size:12px}
"""


def esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def tree_svg():
    """决策树示意（一次绘制，案例卡片里给命中路径）。"""
    Q = "#eef2f7"; QB = "#94a3b8"
    FB = "#fee2e2"; FBD = "#dc2626"; FT = "#7f1d1d"
    OB = "#dcfce7"; OBD = "#16a34a"; OT = "#14532d"
    boxes = []

    def q(x, y, w, h, lines):
        boxes.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="{Q}" stroke="{QB}"/>')
        for i, ln in enumerate(lines):
            boxes.append(f'<text x="{x + w / 2}" y="{y + 18 + i * 15}" font-size="12" text-anchor="middle" fill="#0f172a">{ln}</text>')

    def f(x, y, w, h, lines, bad=True):
        fill = FB if bad else OB
        boxes.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="{fill}" stroke="{FBD if bad else OBD}" stroke-width="1.5"/>')
        for i, ln in enumerate(lines):
            boxes.append(f'<text x="{x + w / 2}" y="{y + 17 + i * 14}" font-size="11.5" text-anchor="middle" fill="{FT if bad else OT}">{ln}</text>')

    # 竖向主干 + 右侧故障叶
    for y in (16, 122, 228, 334):
        boxes.append(f'<line x1="230" y1="{y + 44}" x2="230" y2="{y + 74}" stroke="{QB}" stroke-width="1.5"/>')
    q(80, 16, 300, 44, ["R1 = (T满载−T过渡)/(P满载−P过渡) ≥ 0.8 K/W ?"])
    f(430, 16, 230, 44, ["● N1 TIM/压装断链", "（换硅脂级，软件只能降功率）"])
    boxes.append(f'<line x1="380" y1="38" x2="430" y2="38" stroke="{FBD}" stroke-width="1.5"/><text x="398" y="32" font-size="10" fill="#b91c1c">是</text>')

    q(80, 122, 300, 44, ["R2 ≥ 0.7 K/W 且 ΔT(风扇60→100%) &lt; 6℃ ?"])
    f(430, 122, 230, 44, ["● N2 风扇/鳍片/风道", "（清灰 / 换风扇 / 疏通风道）"])
    boxes.append(f'<line x1="380" y1="144" x2="430" y2="144" stroke="{FBD}" stroke-width="1.5"/><text x="398" y="138" font-size="10" fill="#b91c1c">是</text>')

    q(80, 228, 300, 44, ["稳态窗 Tmax ≥ 95℃ 占比 ≥ 50% ?"])
    f(430, 228, 230, 44, ["● N3 整体散热不足", "（温度墙，必须压功率）"])
    boxes.append(f'<line x1="380" y1="250" x2="430" y2="250" stroke="{FBD}" stroke-width="1.5"/><text x="398" y="244" font-size="10" fill="#b91c1c">是</text>')

    q(80, 334, 300, 44, ["P稳态 ≤ 0.85×设计功率 且 不贴墙 ?"])
    f(430, 334, 230, 44, ["● N4 电气功率墙", "（PL/供电限制，散热无恙）"])
    boxes.append(f'<line x1="380" y1="356" x2="430" y2="356" stroke="{FBD}" stroke-width="1.5"/><text x="398" y="350" font-size="10" fill="#b91c1c">是</text>')

    f(130, 428, 200, 46, ["○ 健康", "T = T_amb + (R1 + R2(风扇))·P"], bad=False)
    boxes.append('<text x="240" y="84" font-size="10" fill="#64748b">否</text>')
    boxes.append('<text x="240" y="190" font-size="10" fill="#64748b">否</text>')
    boxes.append('<text x="240" y="296" font-size="10" fill="#64748b">否</text>')
    boxes.append('<text x="240" y="402" font-size="10" fill="#64748b">否</text>')

    return ('<svg viewBox="0 0 680 490" xmlns="http://www.w3.org/2000/svg" '
            'style="width:100%;background:#fff;border:1px solid #cbd5e1;border-radius:8px">'
            + "".join(boxes) + "</svg>")


def traj_svg(rows, height=110):
    ts = [fnum(r["elapsed_s"]) or i for i, r in enumerate(rows)]
    Ts = [fnum(r["cpu_temp_c"]) for r in rows]
    Ps = [fnum(r["cpu_power_w"]) for r in rows]
    t0, t1 = ts[0], ts[-1]
    tmin = min(Ts) - 2
    tmax = max(Ts) + 2
    pmax = max(Ps) * 1.15
    W, H = 640, height
    margin = 26

    def X(t):
        return margin + (t - t0) / max(1e-9, t1 - t0) * (W - margin - 8)

    def Y1(v):
        return H - 14 - (v - tmin) / max(1e-9, tmax - tmin) * (H - 26)

    def Y2(v):
        return H - 14 - v / pmax * (H - 26)

    out = [f'<svg viewBox="0 0 {W} {H}" xmlns="http://www.w3.org/2000/svg" style="width:100%;background:#fff;border:1px solid #e2e8f0;border-radius:6px">']
    # 段边界
    order, g = stages_of(rows)
    kinds = {s: stage_kind(g[s]) for s in order}
    for i, s in enumerate(order):
        t = fnum(g[s][0]["elapsed_s"]) or 0
        col = "#cbd5e1" if kinds[s] == "load" else "#e2e8f0"
        out.append(f'<rect x="{X(t)}" y="10" width="{max(1, X(fnum(g[s][-1]["elapsed_s"]) or t) - X(t))}" height="{H - 24}" fill="{col}" opacity="0.35"/>')
        out.append(f'<text x="{X(t) + 3}" y="20" font-size="9" fill="#64748b">{esc(s)}</text>')
    pt = " ".join(f"{X(t):.1f},{Y1(v):.1f}" for t, v in zip(ts, Ts) if v is not None)
    pp = " ".join(f"{X(t):.1f},{Y2(v):.1f}" for t, v in zip(ts, Ps) if v is not None)
    out.append(f'<polyline points="{pt}" fill="none" stroke="#dc2626" stroke-width="1.4"/>')
    out.append(f'<polyline points="{pp}" fill="none" stroke="#d97706" stroke-width="1.2" stroke-dasharray="3,2"/>')
    out.append(f'<text x="4" y="16" font-size="10" fill="#dc2626">T {tmin:.0f}~{tmax:.0f}℃</text>')
    out.append(f'<text x="4" y="{H - 4}" font-size="10" fill="#d97706">P 0~{pmax:.0f}W</text>')
    out.append("</svg>")
    return "".join(out)


def fmt(x, nd=2, unit=""):
    return "-" if x is None else f"{x:.{nd}f}{unit}"


def case_card(case):
    F = case["F"]
    finds = case["findings"]
    verdict = case["verdict"]
    bad = any(f["ok"] is False and f["node"] in ("N1", "N2", "N3") for f in finds)
    badge = ('b-bad' if bad else 'b-warn' if any(f["ok"] is False for f in finds)
             else 'b-ok')
    pol = case["policy"]
    fp = F["fan_pair"]

    h = ['<div class="card">']
    h.append(f'<div><b>{esc(case["title"])}</b> '
             f'<span class="muted">({esc(case["src"])})</span> '
             f'<span class="badge {badge}">{esc(verdict[:38])}</span></div>')
    h.append(f'<div class="muted">{esc(case.get("note") or "")}</div>')
    h.append(traj_svg(case["rows"]))
    h.append('<table><tr><th>特征</th><th>值</th><th>判读</th></tr>')
    rows = [
        ("R1（die↔散热器）", f"{fmt(F['R1'])} K/W" + (f"（IQR ±{F['R1_iqr'] / 2:.2f}）" if F['R1_iqr'] else ""),
         "正常 0.1~0.35 / 断链 ≥0.8"),
        ("R2（散热器↔环境）", f"{fmt(F['R2'])} K/W @T_amb={F['t_amb']:.0f}℃",
         "风扇侧；正常 0.15~0.4"),
        ("稳态", f"{fmt(F['T_ss'], 1, '℃')} @ {fmt(F['P_ss'], 1, 'W')} @ {fmt(F['f_ss'], 0, 'MHz')}", ""),
        ("贴墙占比", f"{F['pin'] * 100:.0f}% (Tmax≥{PIN_T:.0f}℃)", "≥50% = 温度墙"),
        ("早期→稳态功率", f"{fmt(F['P_early'], 1, 'W')} → {fmt(F['P_ss'], 1, 'W')}"
                       + (f"（回落 {F['P_sag']:.1f}W）" if F['P_sag'] else ""),
         "贴墙时回落 = 热的墙"),
        ("风扇杠杆 ΔT(60→100)", f"{fmt(fp['dT'], 1, '℃')}" if fp else "未测",
         f"ΔP {fmt(fp['dP'], 1, 'W')}" if fp else "计划含风扇段可测"),
        ("τ 散热器", f"{fmt(F['dec_tau'], 0, ' s')}" if F["dec_tau"] else "-", "80~150 s 正常"),
    ]
    for k, v, note in rows:
        h.append(f"<tr><td>{k}</td><td>{v}</td><td class='muted'>{note}</td></tr>")
    h.append("</table>")
    h.append("<div style='margin-top:8px'>")
    for f in finds:
        cls = "f-bad" if f["ok"] is False else ("f-ok" if f["ok"] else "f-info")
        h.append(f'<div class="find {cls}"><b>{esc(f["node"])} · {esc(f["title"])}</b>'
                 f'<div class="muted">{esc(f.get("text", ""))}</div></div>')
    h.append("</div>")

    # 策略表
    h.append('<table style="margin-top:6px"><tr><th>目标温度</th><th>风扇100% 预算</th>'
             '<th>风扇60% 预算</th><th>当前功率预期温度</th></tr>')
    for r in pol["rows"]:
        h.append(f"<tr><td>≤ {r['target']:.0f}℃</td><td>{r['p100']:.1f} W</td>"
                 f"<td>{r['p60']:.1f} W</td><td>{fmt(r['t_now'], 1, '℃')}</td></tr>")
    h.append("</table>")
    cf = pol["cf"]
    h.append(f'<div class="find f-info"><b>反事实（重涂硅脂 R1→{R1_FIXED}）</b>：'
             f'当前 {fmt(F["P_ss"], 1, "W")} 下结温 ≈ <b>{fmt(cf["t_at_p"], 1, "℃")}</b>；'
             f'90℃ 预算升到 {cf["p90"]:.1f} W；设计 {F["design_w"]:.0f}W 满载 ≈ '
             f'{fmt(cf["t_design"], 1, "℃")}。'
             f'<span class="muted">（T_amb 按输入 {F["t_amb"]:.0f}℃，±3℃ 影响预算 ±1.5W 级）</span></div>')
    h.append("</div>")
    return "".join(h)


def build_report(cases, out_html, out_dir):
    h = ['<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">'
         '<title>热链路诊断报告</title><style>' + CSS + "</style></head><body><div class='wrap'>"]
    h.append("<h1>热链路诊断 · 5 分钟压测 → 故障环节定位 → 降级策略</h1>")
    h.append('<div class="sub">模型：T = T_amb + (R1 + R2(风扇))·P，R1=硅脂/压装、R2=鳍片/风量；'
             'R1 由「掉载瞬间温度塌落」无歧义分离，不依赖环境温度假设。'
             f'生成于 {__import__("time").strftime("%Y-%m-%d %H:%M")}</div>')
    h.append("<h2>决策树</h2>")
    h.append(tree_svg())
    h.append('<div class="muted">按序判定，先命中先报告；N4b（功率被温度墙压住）作为伴随发现单独列出。'
             '阈值来自 45W 级笔记本工程常值，全部可复核。</div>')

    h.append("<h2>案例矩阵</h2>")
    h.append('<table><tr><th>案例</th><th>R1</th><th>R2</th><th>稳态</th><th>贴墙</th>'
             '<th>ΔT风扇</th><th>结论</th></tr>')
    for c in cases:
        F = c["F"]
        fp = F["fan_pair"]
        h.append(f"<tr><td>{esc(c['title'])}</td><td>{fmt(F['R1'])}</td><td>{fmt(F['R2'])}</td>"
                 f"<td>{fmt(F['T_ss'], 1, '℃')}@{fmt(F['P_ss'], 1, 'W')}</td>"
                 f"<td>{F['pin'] * 100:.0f}%</td>"
                 f"<td>{fmt(fp['dT'], 1) if fp else '-'}</td>"
                 f"<td style='text-align:left'>{esc(c['verdict'])}</td></tr>")
    h.append("</table>")

    h.append("<h2>案例详情</h2>")
    for c in cases:
        h.append(case_card(c))

    h.append("<h2>在一台新机器上怎么跑</h2>")
    h.append('<div class="card"><div>管理员双击 <code>lab\\run-diag5.cmd</code>（约 5.3 分钟，'
             '空闲→满载风扇100→满载风扇60→满载风扇100→空闲冷却，采样 400ms）。'
             '跑完把生成的 <code>lab-data\\lab-diag5-&lt;时间戳&gt;.csv</code> 喂给：'
             '<code>python tools/lab_diagnose.py &lt;csv&gt; --tamb 室温 --design-w 设计功率</code></div>'
             '<div class="muted" style="margin-top:6px">已知局限：① T_amb 需输入（只影响功率预算绝对值，'
             '不影响故障定位）；② 风扇 60% 段 120s 未完全热平衡，ΔT 读数偏保守（阈值已按此校准）；'
             '③ powercfg cap 无效的机器上功率不可程序控制，降功率要靠关睿频（PERFBOOSTMODE）或工作负载自身。</div></div>')
    h.append("</div></body></html>")
    with open(out_html, "w", encoding="utf-8") as fh:
        fh.write("".join(h))


# ---------------------------------------------------------------- main

def analyze(rows, title, src, design_w, t_amb, note=None):
    F = extract_features(rows, design_w, t_amb)
    if F is None:
        return None
    finds, verdict = decide(F)
    pol = policy_of(F)
    return dict(title=title, src=src, note=note, rows=rows, F=F,
                findings=finds, verdict=verdict, policy=pol)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csvs", nargs="*", help="实测 lab CSV")
    ap.add_argument("--selftest", action="store_true", help="生成 4 个仿真场景自检")
    ap.add_argument("--real", help="附带分析的实测 CSV")
    ap.add_argument("--tamb", type=float, default=25.0, help="环境温度（默认 25）")
    ap.add_argument("--design-w", type=float, default=45.0, help="机器设计功率")
    ap.add_argument("--out-dir", default="lab-data")
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    demo_dir = os.path.join(args.out_dir, "_diagdemo")
    cases = []

    if args.selftest:
        os.makedirs(demo_dir, exist_ok=True)
        for name in SCENARIOS:
            rows, cfg = sim_case(name)
            csvp = os.path.join(demo_dir, f"{name}.csv")
            with open(csvp, "w", encoding="utf-8-sig", newline="") as fh:
                w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
                w.writeheader()
                w.writerows(rows)
            c = analyze(rows, name, "仿真", cfg["design"], 24.0, cfg["note"])
            if c:
                cases.append(c)
                print(f"[selftest] {name:14s} R1={c['F']['R1']:.2f} R2={c['F']['R2']:.2f} "
                      f"ΔT={fmt((c['F']['fan_pair'] or {}).get('dT'), 1)} -> {c['verdict'][:40]}")

    for path in ([args.real] if args.real else []) + list(args.csvs):
        rows = load_csv(path)
        tag = os.path.splitext(os.path.basename(path))[0]
        c = analyze(rows, tag, os.path.basename(path), args.design_w, args.tamb)
        if c:
            cases.append(c)
            F = c["F"]
            print(f"[real] {tag[:40]:40s} R1={fmt(F['R1'])} R2={fmt(F['R2'])} "
                  f"pin={F['pin'] * 100:.0f}% -> {c['verdict'][:50]}")
            with open(os.path.join(args.out_dir, f"diag-{tag[:24]}.json"), "w",
                      encoding="utf-8") as fh:
                json.dump(dict(tag=tag, source=path, features={
                    k: v for k, v in F.items() if isinstance(v, (int, float, str, type(None)))},
                    findings=c["findings"], verdict=c["verdict"],
                    policy=c["policy"]), fh, ensure_ascii=False, indent=1)

    if not cases:
        print("没有可分析的数据。--selftest 或传入 CSV。")
        return 1

    out_html = os.path.join(args.out_dir, "report-diag.html")
    build_report(cases, out_html, args.out_dir)
    print(f"\n报告: {out_html}  ({len(cases)} 个案例)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
