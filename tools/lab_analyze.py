#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lab_analyze.py —— 把压测 CSV 变成「可解释的热模型 + 控制律 + 报告」

用法：
    python tools/lab_analyze.py [lab-data 目录或 csv 路径]

产出（与输入 CSV 同目录）：
    report-<tag>.html   自带 SVG 图表的分析报告（可直接发人 / 存档）
    analysis-<tag>.json 结构化结果（给程序反解曲线用）
    analysis-<tag>.md   纯文本摘要

只用标准库，不依赖 numpy/pandas，方便在用户机上直接跑。
"""

from __future__ import annotations

import csv
import glob
import json
import math
import os
import sys
from dataclasses import dataclass, field
from datetime import datetime


# --------------------------------------------------------------------------- 基础统计
def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def stdev(xs):
    if len(xs) < 2:
        return 0.0
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def linfit(xs, ys):
    """最小二乘 y = a + b·x，返回 a, b, r2, se_b。"""
    n = len(xs)
    if n < 2:
        return 0.0, 0.0, 0.0, float("inf")
    mx, my = mean(xs), mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    syy = sum((y - my) ** 2 for y in ys)
    if sxx < 1e-12:
        return my, 0.0, 0.0, float("inf")
    b = sxy / sxx
    a = my - b * mx
    r2 = 0.0 if syy < 1e-12 else (sxy * sxy) / (sxx * syy)
    resid = [y - (a + b * x) for x, y in zip(xs, ys)]
    dof = max(1, n - 2)
    s2 = sum(r * r for r in resid) / dof
    se_b = math.sqrt(s2 / sxx) if sxx > 1e-12 else float("inf")
    return a, b, max(0.0, min(1.0, r2)), se_b


def quadfit(xs, ys):
    """二次拟合 y = a + b·x + c·x²（3 参数，样本 ≥4 才用）。"""
    n = len(xs)
    if n < 4:
        return None
    # 正规方程 3x3
    s = [sum(x ** k for x in xs) for k in range(5)]
    t = [sum((x ** k) * y for x, y in zip(xs, ys)) for k in range(3)]
    a = [[s[0], s[1], s[2]], [s[1], s[2], s[3]], [s[2], s[3], s[4]]]
    b = [t[0], t[1], t[2]]
    # 高斯消元
    for i in range(3):
        p = max(range(i, 3), key=lambda r: abs(a[r][i]))
        if abs(a[p][i]) < 1e-12:
            return None
        a[i], a[p] = a[p], a[i]
        b[i], b[p] = b[p], b[i]
        for r in range(i + 1, 3):
            f = a[r][i] / a[i][i]
            for c in range(i, 3):
                a[r][c] -= f * a[i][c]
            b[r] -= f * b[i]
    x = [0.0, 0.0, 0.0]
    for i in range(2, -1, -1):
        x[i] = (b[i] - sum(a[i][j] * x[j] for j in range(i + 1, 3))) / a[i][i]
    a0, a1, a2 = x
    my = mean(ys)
    syy = sum((y - my) ** 2 for y in ys)
    sse = sum((y - (a0 + a1 * v + a2 * v * v)) ** 2 for v, y in zip(xs, ys))
    r2 = 0.0 if syy < 1e-12 else 1 - sse / syy
    return a0, a1, a2, max(0.0, min(1.0, r2))


# --------------------------------------------------------------------------- 读取
@dataclass
class Row:
    t: float
    stage: str
    load: str
    cpu_t: float
    core_t: float
    volt: float
    load_pct: float
    mhz: float
    watt: float
    gpu_t: float
    fan_rpm: float
    cap: float
    boost: float


NUM = {
    "cpu_temp_c": "cpu_t", "cpu_core_max_c": "core_t", "cpu_volt_v": "volt",
    "cpu_load_pct": "load_pct", "cpu_mhz": "mhz", "cpu_power_w": "watt",
    "gpu_temp_c": "gpu_t", "fan_rpm": "fan_rpm", "proc_cap": "cap", "boost": "boost",
}


def load_csv(path):
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for d in csv.DictReader(f):
            def g(k, dflt=0.0):
                v = d.get(k, "")
                try:
                    return float(v) if v not in ("", None) else dflt
                except ValueError:
                    return dflt

            r = Row(t=g("elapsed_s"), stage=(d.get("stage") or "").strip(),
                    load=(d.get("load") or "").strip().lower(),
                    cpu_t=g("cpu_temp_c"), core_t=g("cpu_core_max_c"), volt=g("cpu_volt_v"),
                    load_pct=g("cpu_load_pct"), mhz=g("cpu_mhz"), watt=g("cpu_power_w"),
                    gpu_t=g("gpu_temp_c"), fan_rpm=g("fan_rpm"), cap=g("proc_cap"), boost=g("boost", -1))
            if r.cpu_t > 0:
                rows.append(r)
    rows.sort(key=lambda r: r.t)
    return rows


@dataclass
class StageStat:
    stage: str
    load: str
    cap: float
    boost: float
    n: int = 0
    t_mean: float = 0.0
    t_std: float = 0.0
    t_max: float = 0.0
    core_mean: float = 0.0
    watt: float = 0.0
    mhz: float = 0.0
    volt: float = 0.0
    load_pct: float = 0.0
    rpm: float = 0.0
    steady: bool = False
    rows: list = field(default_factory=list)          # 稳态窗口
    rows_all: list = field(default_factory=list)      # 整段（冷却曲线用）


def steady_stats(rows, tail=0.45, min_n=8):
    """取后 45% 窗口做稳态估计；返回 StageStat。"""
    if not rows:
        return None
    n_tail = max(min_n, int(len(rows) * tail)) if len(rows) >= min_n else len(rows)
    win = rows[-n_tail:]
    st = StageStat(stage=rows[0].stage, load=rows[0].load, cap=rows[-1].cap, boost=rows[-1].boost)
    st.n = len(win)
    ts = [r.cpu_t for r in win]
    st.t_mean, st.t_std, st.t_max = mean(ts), stdev(ts), max(ts)
    st.core_mean = mean([r.core_t or r.cpu_t for r in win])
    st.watt = mean([r.watt for r in win])
    st.mhz = mean([r.mhz for r in win])
    st.volt = mean([r.volt for r in win])
    st.load_pct = mean([r.load_pct for r in win])
    st.rpm = mean([r.fan_rpm for r in win])
    st.steady = st.t_std <= 1.2
    st.rows = win
    st.rows_all = rows
    return st


# --------------------------------------------------------------------------- SVG 图
def svg_scatter(points, xlabel, ylabel, title, fit=None, w=560, h=300, unit_y="°C"):
    """points: [(x,y,label)]；fit: (a,b) 直线。"""
    if not points:
        return ""
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    x0, x1 = min(xs), max(xs)
    y0, y1 = min(ys), max(ys)
    padx = (x1 - x0) * 0.15 or 1
    pady = (y1 - y0) * 0.25 or 1
    x0, x1 = x0 - padx, x1 + padx
    y0, y1 = y0 - pady, y1 + pady
    L, R, T, B = 56, 16, 34, 44

    def px(x):
        return L + (x - x0) / (x1 - x0) * (w - L - R)

    def py(y):
        return h - B - (y - y0) / (y1 - y0) * (h - T - B)

    parts = [f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px" xmlns="http://www.w3.org/2000/svg">',
             f'<text x="{L}" y="20" font-size="13" font-weight="600" fill="#1f2a37">{title}</text>']
    # 网格
    for i in range(5):
        gy = T + (h - T - B) * i / 4
        val = y1 - (y1 - y0) * i / 4
        parts.append(f'<line x1="{L}" y1="{gy:.1f}" x2="{w - R}" y2="{gy:.1f}" stroke="#e6ebf2" stroke-width="1"/>')
        parts.append(f'<text x="{L - 8}" y="{gy + 4:.1f}" font-size="10" fill="#7b8794" text-anchor="end">{val:.0f}</text>')
    # 轴
    parts.append(f'<line x1="{L}" y1="{h - B}" x2="{w - R}" y2="{h - B}" stroke="#c7d0dc"/>')
    parts.append(f'<text x="{(L + w - R) / 2}" y="{h - 12}" font-size="11" fill="#5a6675" text-anchor="middle">{xlabel}</text>')
    parts.append(f'<text x="14" y="{(T + h - B) / 2}" font-size="11" fill="#5a6675" '
                 f'transform="rotate(-90 14 {(T + h - B) / 2})" text-anchor="middle">{ylabel}</text>')
    # 拟合线
    if fit:
        a, b = fit
        parts.append(f'<line x1="{px(x0):.1f}" y1="{py(a + b * x0):.1f}" x2="{px(x1):.1f}" '
                     f'y2="{py(a + b * x1):.1f}" stroke="#2f7ed8" stroke-width="2" opacity="0.7"/>')
    # 点
    for i, (x, y, lab) in enumerate(points):
        parts.append(f'<circle cx="{px(x):.1f}" cy="{py(y):.1f}" r="5" fill="#e2574c" '
                     f'stroke="#fff" stroke-width="1.5"><title>{lab}: ({x:.1f}, {y:.1f}{unit_y})</title></circle>')
        parts.append(f'<text x="{px(x):.1f}" y="{py(y) - 9:.1f}" font-size="9.5" fill="#33404d" '
                     f'text-anchor="middle">{lab}</text>')
    parts.append("</svg>")
    return "".join(parts)


def svg_series(series, xlabel, ylabel, title, w=560, h=260):
    """series: [(name, [(x,y)], color)]"""
    allx = [p[0] for _, pts, _ in series for p in pts]
    ally = [p[1] for _, pts, _ in series for p in pts]
    if not allx:
        return ""
    x0, x1 = min(allx), max(allx)
    y0, y1 = min(ally), max(ally)
    padx = (x1 - x0) * 0.04 or 1
    pady = (y1 - y0) * 0.15 or 1
    x0, x1, y0, y1 = x0 - padx, x1 + padx, y0 - pady, y1 + pady
    L, R, T, B = 56, 100, 34, 40

    def px(x):
        return L + (x - x0) / (x1 - x0) * (w - L - R)

    def py(y):
        return h - B - (y - y0) / (y1 - y0) * (h - T - B)

    parts = [f'<svg viewBox="0 0 {w} {h}" width="100%" style="max-width:{w}px" xmlns="http://www.w3.org/2000/svg">',
             f'<text x="{L}" y="20" font-size="13" font-weight="600" fill="#1f2a37">{title}</text>']
    for i in range(5):
        gy = T + (h - T - B) * i / 4
        val = y1 - (y1 - y0) * i / 4
        parts.append(f'<line x1="{L}" y1="{gy:.1f}" x2="{w - R}" y2="{gy:.1f}" stroke="#e6ebf2"/>')
        parts.append(f'<text x="{L - 8}" y="{gy + 4:.1f}" font-size="10" fill="#7b8794" text-anchor="end">{val:.0f}</text>')
    parts.append(f'<line x1="{L}" y1="{h - B}" x2="{w - R}" y2="{h - B}" stroke="#c7d0dc"/>')
    parts.append(f'<text x="{(L + w - R) / 2}" y="{h - 10}" font-size="11" fill="#5a6675" text-anchor="middle">{xlabel}</text>')
    parts.append(f'<text x="14" y="{(T + h - B) / 2}" font-size="11" fill="#5a6675" '
                 f'transform="rotate(-90 14 {(T + h - B) / 2})" text-anchor="middle">{ylabel}</text>')
    ly = T + 4
    for name, pts, color in series:
        d = " ".join(f'{"M" if i == 0 else "L"}{px(p[0]):.1f},{py(p[1]):.1f}' for i, p in enumerate(pts))
        parts.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="2"/>')
        parts.append(f'<rect x="{w - R + 8}" y="{ly - 8}" width="10" height="10" fill="{color}" rx="2"/>')
        parts.append(f'<text x="{w - R + 22}" y="{ly + 1}" font-size="10.5" fill="#33404d">{name}</text>')
        ly += 18
    parts.append("</svg>")
    return "".join(parts)


# --------------------------------------------------------------------------- 主分析
def analyze(csv_path):
    rows = load_csv(csv_path)
    if not rows:
        raise SystemExit(f"没有可用样本（CPU 温度全为 0）：{csv_path}\n请确认以管理员身份运行压测。")

    # 分阶段
    stages = {}
    for r in rows:
        stages.setdefault(r.stage, []).append(r)
    order = list(stages.keys())
    stats = [steady_stats(stages[k]) for k in order]
    stats = [s for s in stats if s]

    load_stages = [s for s in stats if s.load == "cpu" and s.watt > 0]
    idle_stages = [s for s in stats if s.load != "cpu"]

    warnings = []
    for s in load_stages:
        if s.load_pct < 85:
            warnings.append(f"阶段 {s.stage} 满载期间 CPU 使用率仅 {s.load_pct:.0f}%，温度被低估（后台有负载抢占）。")
        if not s.steady:
            warnings.append(f"阶段 {s.stage} 未达稳态（温度波动 ±{s.t_std:.1f}°C），拟合权重应降低。")

    res = {
        "csv": os.path.basename(csv_path),
        "generated": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "samples": len(rows),
        "stages": [{
            "stage": s.stage, "load": s.load, "cap": s.cap, "boost": s.boost, "n": s.n,
            "T": round(s.t_mean, 2), "T_std": round(s.t_std, 2), "T_max": round(s.t_max, 1),
            "Tcore": round(s.core_mean, 1), "P": round(s.watt, 2), "f": round(s.mhz, 0),
            "V": round(s.volt, 3), "load": round(s.load_pct, 0), "rpm": round(s.rpm, 0),
            "steady": s.steady,
        } for s in stats],
        "warnings": warnings,
    }

    if len(load_stages) < 3:
        res["error"] = "可用满载稳态阶段不足 3 个，无法建模。"
        return res, stats, rows

    # ---- 模型 1：T = a + b·P（闭环热阻，℃/W）
    xs = [s.watt for s in load_stages]
    ys = [s.t_mean for s in load_stages]
    a1, b1, r2_1, se1 = linfit(xs, ys)
    res["model_T_from_P"] = {"a": round(a1, 2), "b": round(b1, 3), "r2": round(r2_1, 3),
                             "se_b": round(se1, 3) if math.isfinite(se1) else None,
                             "note": "b = 闭环热阻（BIOS 风扇曲线参与）℃，每瓦升温度数"}

    # ---- 模型 2：P = c·f^α（功耗随频率超线性）
    lx = [math.log(s.mhz) for s in load_stages if s.mhz > 200]
    ly = [math.log(s.watt) for s in load_stages if s.mhz > 200]
    if len(lx) >= 3:
        la, lb, lr2, _ = linfit(lx, ly)
        res["model_P_from_f"] = {"c": round(math.exp(la), 6), "alpha": round(lb, 3),
                                 "r2": round(lr2, 3),
                                 "note": "P=c·f^α；α>1 说明降频对功耗/温度是超线性的（越降越赚）"}

    # ---- 模型 3：P = C·V²·f（动态功耗物理式）→ 有效开关电容系数
    ceff = [(s.watt / (s.volt ** 2 * s.mhz)) * 1e6 for s in load_stages if s.volt > 0.2 and s.mhz > 200]
    if ceff:
        res["model_power_physics"] = {
            "C_eff_mean": round(mean(ceff), 3), "C_eff_cv": round(stdev(ceff) / mean(ceff), 3) if mean(ceff) else None,
            "note": "P/(V²·f) 近似常数则说明 P=C·V²·f 成立；CV 越小模型越可信"}

    # ---- 模型 4：T 与 cap 的关系（控制律直接用这个）
    caps = [s.cap for s in load_stages]
    Ts = [s.t_mean for s in load_stages]
    a4, b4, r2_4, _ = linfit(caps, Ts)
    q = quadfit(caps, Ts)
    res["model_T_from_cap"] = {
        "linear": {"a": round(a4, 2), "b": round(b4, 4), "r2": round(r2_4, 3)},
        "quadratic": ({"a": round(q[0], 2), "b": round(q[1], 4), "c": round(q[2], 6), "r2": round(q[3], 3)}
                      if q else None),
        "note": "b 为每 1% 频率上限的温度变化，负值表示降频降温",
    }

    # ---- 冷却时间常数 τ：T(t) = A + B·exp(-t/τ)，对 τ 做网格搜索 + 线性最小二乘
    cool = [s for s in stats if s.load == "idle" and s.stage == order[-1]]
    if cool:
        cool_rows = cool[0].rows_all or cool[0].rows
        if len(cool_rows) >= 20:
            t0 = cool_rows[0].t
            ts = [r.t - t0 for r in cool_rows]
            Tvals = [r.cpu_t for r in cool_rows]
            if max(Tvals) - min(Tvals) > 2.0:      # 需要有明显降温过程
                best = None
                for tau in range(5, 601):
                    e = [math.exp(-t / tau) for t in ts]
                    a, b, r2, _ = linfit(e, Tvals)
                    if best is None or r2 > best[0]:
                        best = (r2, tau, a, b)
                if best and best[0] > 0.5:
                    r2, tau, A, B = best
                    res["cooldown"] = {"tau_s": float(tau), "r2": round(r2, 3),
                                       "asymptote": round(A, 1),
                                       "note": "热时间常数；控制周期/斜率限速应与之匹配（约 τ/4）"}

    # ---- Turbo 单独效应
    turbo_on = [s for s in load_stages if s.boost != 0 and s.cap >= 99]
    turbo_off = [s for s in load_stages if s.boost == 0 and s.cap >= 99]
    if turbo_on and turbo_off:
        d = turbo_off[0].t_mean - turbo_on[0].t_mean
        dp = turbo_off[0].watt - turbo_on[0].watt
        res["turbo_effect"] = {"delta_T": round(d, 2), "delta_P": round(dp, 2),
                               "note": "关闭 Turbo 后满载温度/功耗变化（同 cap=100%）"}

    # ---- 因素影响力排序
    effects = []
    if len(load_stages) >= 3:
        lo = min(load_stages, key=lambda s: s.cap)
        hi = max(load_stages, key=lambda s: s.cap)
        span = hi.cap - lo.cap
        if span > 1:
            dt = lo.t_mean - hi.t_mean
            effects.append({
                "factor": "CPU 频率上限（cap）", "range": f"{hi.cap:.0f}% → {lo.cap:.0f}%",
                "delta_T": round(dt, 2), "per_unit": round(dt / span, 3), "unit": "℃ / 1% cap",
                "measured": True})
    if "turbo_effect" in res:
        effects.append({"factor": "Turbo Boost（关 vs 开）", "range": "cap=100% 下切换",
                        "delta_T": res["turbo_effect"]["delta_T"],
                        "per_unit": res["turbo_effect"]["delta_T"], "unit": "℃", "measured": True})
    effects.append({"factor": "风扇转速/占空比", "range": "本机不可控（EC 固件）",
                    "delta_T": None, "per_unit": None, "unit": "℃", "measured": False})
    effects.append({"factor": "核心电压（降压 UV）", "range": "本轮未纳入",
                    "delta_T": None, "per_unit": None, "unit": "℃", "measured": False})
    ranked = [e for e in effects if e["measured"]]
    ranked.sort(key=lambda e: abs(e["delta_T"]), reverse=True)
    res["effects"] = ranked + [e for e in effects if not e["measured"]]

    # ---- 控制律反解：给定目标温度 → 需要的 cap
    law = []
    for target in (85, 80, 75, 70):
        entry = {"target": target}
        if q:
            a0, a1c, a2c, _ = q
            t_at_100 = a0 + a1c * 100 + a2c * 10000
            if t_at_100 <= target:
                entry["cap_needed"] = 100.0
                entry["est_T"] = round(t_at_100, 1)
                entry["note"] = f"满上限时模型预测仅 {t_at_100:.1f}℃，无需限频"
            else:
                # 解 a0 + a1*cap + a2*cap^2 = target，取 [5,100] 内较大根
                A, B, C = a2c, a1c, a0 - target
                cap = None
                if abs(A) < 1e-9:
                    if abs(B) > 1e-9:
                        cap = -C / B
                else:
                    disc = B * B - 4 * A * C
                    if disc >= 0:
                        r1 = (-B + math.sqrt(disc)) / (2 * A)
                        r2 = (-B - math.sqrt(disc)) / (2 * A)
                        cand = [r for r in (r1, r2) if 5 <= r <= 100]
                        if cand:
                            cap = max(cand)
                if cap is not None:
                    entry["cap_needed"] = round(min(100, max(5, cap)), 1)
                    entry["est_T"] = round(a0 + a1c * cap + a2c * cap * cap, 1)
                else:
                    entry["note"] = "模型外推：目标温度低于可达到的最低温度"
        law.append(entry)
    res["control_law"] = law

    # ---- 数据质量
    res["quality"] = {
        "steady_stages": sum(1 for s in load_stages if s.steady),
        "load_stages": len(load_stages),
        "rpm_available": any(s.rpm > 0 for s in stats),
    }
    return res, stats, rows


# --------------------------------------------------------------------------- 输出
def render_html(res, stats, rows, out_path, tag):
    def esc(s):
        return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    chart_T_P = ""
    load_stages = [s for s in stats if s.load == "cpu" and s.watt > 0]
    if load_stages and "model_T_from_P" in res:
        m = res["model_T_from_P"]
        chart_T_P = svg_scatter([(s.watt, s.t_mean, s.stage) for s in load_stages],
                                "封装功耗 P (W)", "稳态温度 (°C)",
                                f"温度 vs 功耗：T = {m['a']:.1f} + {m['b']:.2f}·P（R²={m['r2']:.2f}）",
                                fit=(m["a"], m["b"]))
    chart_T_cap = ""
    if load_stages and "model_T_from_cap" in res:
        m = res["model_T_from_cap"]["linear"]
        chart_T_cap = svg_scatter([(s.cap, s.t_mean, s.stage) for s in load_stages],
                                  "最大处理器状态 cap (%)", "稳态温度 (°C)",
                                  f"温度 vs 频率上限：每降 1% 约 {m['b']:+.3f}°C（R²={m['r2']:.2f}）",
                                  fit=(m["a"], m["b"]))
    chart_temp = svg_series([("CPU 温度", [(r.t, r.cpu_t) for r in rows], "#e2574c")],
                            "实验时间 (s)", "温度 (°C)", "全程温度曲线（含阶段切换）", w=1050, h=260)
    chart_pw = ""
    if any(r.watt > 0 for r in rows):
        chart_pw = svg_series([("封装功耗", [(r.t, r.watt) for r in rows], "#2f7ed8")],
                              "实验时间 (s)", "功耗 (W)", "全程功耗曲线", w=1050, h=220)

    rows_html = ""
    for s in stats:
        rows_html += (f"<tr><td>{esc(s.stage)}</td><td>{esc(s.load)}</td><td>{s.cap:.0f}%</td>"
                      f"<td class='n'>{s.t_mean:.1f}</td><td class='n'>{s.t_std:.1f}</td>"
                      f"<td class='n'>{s.watt:.1f}</td><td class='n'>{s.mhz:.0f}</td>"
                      f"<td class='n'>{s.volt:.3f}</td><td class='n'>{s.load_pct:.0f}%</td>"
                      f"<td class='n'>{s.rpm:.0f}</td>"
                      f"<td>{'✓' if s.steady else '⚠'}</td></tr>")

    eff_html = ""
    for e in res.get("effects", []):
        v = "—" if e["delta_T"] is None else f"{e['delta_T']:+.2f} ℃"
        pu = "—" if e.get("per_unit") is None else f"{e['per_unit']:+.3f}"
        eff_html += (f"<tr><td>{esc(e['factor'])}</td><td>{esc(e['range'])}</td>"
                     f"<td class='n'>{v}</td><td class='n'>{pu}</td><td>{esc(e['unit'])}</td>"
                     f"<td>{'实测' if e['measured'] else '<span class=dim>待实测</span>'}</td></tr>")

    law_html = ""
    for e in res.get("control_law", []):
        cap = e.get("cap_needed")
        law_html += (f"<tr><td class='n'>{e['target']} ℃</td>"
                     f"<td class='n'>{cap if cap is not None else '无解'}"
                     f"{'%' if cap is not None else ''}</td>"
                     f"<td class='n'>{e.get('est_T', '—')} ℃</td></tr>")

    warn_html = "".join(f"<li>{esc(w)}</li>" for w in res.get("warnings", [])) or "<li class='dim'>无</li>"

    def metric(label, value):
        return f'<div class="kv"><span>{esc(label)}</span><b>{esc(value)}</b></div>'

    metrics = []
    tp = res.get("model_T_from_P")
    metrics.append(metric("闭环热阻 b", f"{tp['b']:.2f} ℃/W" if tp else "—"))
    tc = res.get("model_T_from_cap", {}).get("linear")
    metrics.append(metric("每 1% cap", f"{tc['b']:+.3f} ℃" if tc else "—"))
    pf = res.get("model_P_from_f")
    metrics.append(metric("功耗指数 α", f"{pf['alpha']:.2f}" if pf else "—"))
    cd = res.get("cooldown")
    metrics.append(metric("热时间常数 τ", f"{cd['tau_s']:.0f} s" if cd else "—"))
    q0 = res.get("quality", {})
    metrics.append(metric("稳态阶段", f"{q0.get('steady_stages', 0)}/{q0.get('load_stages', 0)}"))
    metrics.append(metric("转速可用", "是" if q0.get("rpm_available") else "否"))

    html = f"""<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>ThermalPilot 热模型分析 · {esc(tag)}</title>
<style>
  :root {{ --ink:#141c26; --dim:#6b7889; --line:#e3e9f1; --bg:#f6f8fb; --card:#fff; --acc:#2f7ed8; }}
  * {{ box-sizing:border-box }}
  body {{ margin:0; background:var(--bg); color:var(--ink);
         font-family:"Segoe UI","Microsoft YaHei",system-ui,sans-serif; line-height:1.6 }}
  .wrap {{ max-width:1100px; margin:0 auto; padding:28px 20px 60px }}
  h1 {{ font-size:22px; margin:0 0 4px }}
  .sub {{ color:var(--dim); font-size:13px; margin-bottom:22px }}
  .card {{ background:var(--card); border:1px solid var(--line); border-radius:12px;
           padding:18px 20px; margin-bottom:16px }}
  h2 {{ font-size:15px; margin:0 0 12px; padding-left:9px; border-left:3px solid var(--acc) }}
  table {{ width:100%; border-collapse:collapse; font-size:13px }}
  th,td {{ padding:7px 8px; border-bottom:1px solid var(--line); text-align:left }}
  th {{ color:var(--dim); font-weight:600; font-size:12px }}
  td.n {{ text-align:right; font-variant-numeric:tabular-nums }}
  .dim {{ color:var(--dim) }}
  .grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px }}
  .kv {{ background:#f8fafc; border:1px solid var(--line); border-radius:9px; padding:9px 11px }}
  .kv span {{ display:block; font-size:11px; color:var(--dim) }}
  .kv b {{ font-size:15px }}
  .charts {{ display:grid; grid-template-columns:1fr 1fr; gap:14px }}
  @media (max-width:820px) {{ .charts {{ grid-template-columns:1fr }} }}
  ul {{ margin:6px 0 0 18px; padding:0; font-size:13px }}
  .tag {{ display:inline-block; font-size:11px; padding:1px 7px; border-radius:999px;
          background:#eaf2fb; color:#2f7ed8; margin-left:6px }}
</style></head><body><div class="wrap">
<h1>ThermalPilot 热模型分析报告 <span class="tag">{esc(tag)}</span></h1>
<div class="sub">数据源 {esc(res.get('csv'))} · {res.get('samples')} 个样本 · 生成于 {res.get('generated')}</div>

<div class="card"><h2>核心结论</h2><div class="grid">{''.join(metrics)}</div></div>

<div class="card"><h2>拟合曲线</h2><div class="charts">{chart_T_P}{chart_T_cap}</div></div>
<div class="card"><h2>全程曲线</h2>{chart_temp}{chart_pw}</div>

<div class="card"><h2>各阶段稳态统计</h2>
<table><thead><tr><th>阶段</th><th>类型</th><th>cap</th><th>温度℃</th><th>σ</th><th>功耗W</th>
<th>频率MHz</th><th>电压V</th><th>负载</th><th>RPM</th><th>稳态</th></tr></thead>
<tbody>{rows_html}</tbody></table></div>

<div class="card"><h2>因素影响力（实测）</h2>
<table><thead><tr><th>因素</th><th>取值区间</th><th>ΔT</th><th>单位效应</th><th>单位</th><th>来源</th></tr></thead>
<tbody>{eff_html}</tbody></table>
<p class="dim" style="font-size:12px;margin:10px 0 0">
注：ΔT 为该因素在测量区间两端的稳态温差。未实测项需在第二轮实验（可控风扇 / 降压）中补齐。</p></div>

<div class="card"><h2>控制律反解（由模型推"要几度该限到多少"）</h2>
<table><thead><tr><th>目标温度</th><th>建议 cap 上限</th><th>模型预测温度</th></tr></thead>
<tbody>{law_html}</tbody></table></div>

<div class="card"><h2>数据质量</h2><ul>{warn_html}</ul></div>
</div></body></html>"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(html)
    return out_path


def render_md(res):
    L = []
    L.append(f"# ThermalPilot 热模型分析（{res.get('csv')}）\n")
    L.append(f"- 样本数：{res.get('samples')}；生成：{res.get('generated')}")
    if "model_T_from_P" in res:
        m = res["model_T_from_P"]
        L.append(f"- 闭环热阻：**{m['b']:.2f} ℃/W**（R²={m['r2']:.2f}）→ T = {m['a']:.1f} + {m['b']:.2f}·P")
    if "model_P_from_f" in res:
        m = res["model_P_from_f"]
        L.append(f"- 功耗-频率：P = {m['c']:.2e}·f^{m['alpha']:.2f}（R²={m['r2']:.2f}）")
    if "model_T_from_cap" in res:
        m = res["model_T_from_cap"]["linear"]
        L.append(f"- 温度-cap：每 1% cap ≈ {m['b']:+.3f} ℃（R²={m['r2']:.2f}）")
    if "cooldown" in res:
        L.append(f"- 热时间常数 τ ≈ {res['cooldown']['tau_s']:.0f} s")
    L.append("\n## 因素影响力\n")
    L.append("| 因素 | 区间 | ΔT | 单位 | 来源 |")
    L.append("|---|---|---|---|---|")
    for e in res.get("effects", []):
        v = "—" if e["delta_T"] is None else f"{e['delta_T']:+.2f} ℃"
        L.append(f"| {e['factor']} | {e['range']} | {v} | {e['unit']} | {'实测' if e['measured'] else '待实测'} |")
    if res.get("control_law"):
        L.append("\n## 控制律反解\n")
        L.append("| 目标温度 | 建议 cap | 预测温度 |")
        L.append("|---|---|---|")
        for e in res["control_law"]:
            L.append(f"| {e['target']} ℃ | {e.get('cap_needed', '无解')}{'%' if e.get('cap_needed') else ''} | {e.get('est_T', '—')} |")
    if res.get("warnings"):
        L.append("\n## 数据质量提示\n")
        for w in res["warnings"]:
            L.append(f"- {w}")
    return "\n".join(L)


def main():
    arg = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lab-data")
    csvs = []
    if os.path.isdir(arg):
        csvs = sorted(glob.glob(os.path.join(arg, "lab-*.csv")))
    elif os.path.isfile(arg):
        csvs = [arg]
    if not csvs:
        raise SystemExit(f"没找到 CSV：{arg}（请先跑 lab\\run-lab.cmd）")

    for path in csvs:
        tag = os.path.basename(path).replace("lab-", "").replace(".csv", "")
        print("=" * 60)
        print("CSV :", path)

        res, stats, rows = analyze(path)
        out_dir = os.path.dirname(os.path.abspath(path))
        with open(os.path.join(out_dir, f"analysis-{tag}.json"), "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        md = render_md(res)
        with open(os.path.join(out_dir, f"analysis-{tag}.md"), "w", encoding="utf-8") as f:
            f.write(md)
        if stats:
            html = render_html(res, stats, rows, os.path.join(out_dir, f"report-{tag}.html"), tag)
            print("HTML :", html)
        print(md)


if __name__ == "__main__":
    main()
