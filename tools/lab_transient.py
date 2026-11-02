#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lab_transient.py —— 短时实验（3/5/10 分钟）的瞬态辨识

问题：稳态法要求每档至少 3τ（τ 是热时间常数），笔记本上通常 60–120s，
      所以 8 档 DOE 要 18 分钟。3–5 分钟根本到不了稳态。

解法：不等稳态，改从**升温/降温轨迹**里辨识一阶惯性的渐近值：

    τ·dT/dt = (T_amb + R·P) − T
    T(t)    = A + (T₀ − A)·exp(−(t−t₀)/τ)        A = T_amb + R·P（段稳态）

对固定 τ，模型对 (A, T₀) 是**线性**的，所以：
    外层 1 维搜索 τ  →  内层解 2 参数线性最小二乘
不需要非线性优化器，也不会陷局部极小。

产出：
    1. 每段的渐近温度 A_k + 95% 区间（含 τ 不确定度的 profile 传播）
    2. τ 是否可辨识、是否跨段一致（一阶假设是否成立）
    3. A_k 对 P_k 的加权回归 → T_amb、R_th，**残差即模型是否合适的证据**
    4. 与"直接取段尾均值"的对比 → 量化"短实验到底偏了多少"
    5. 合成稳态 CSV，交给 lab_model.py / lab_doe.py 复用下游链路

只用标准库。

用法：
    python tools/lab_transient.py <csv 或目录> [--target-s 300]
"""

from __future__ import annotations

import glob
import json
import math
import os
import sys

# --------------------------------------------------------------------------- 基础
def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def stdev(xs):
    if len(xs) < 2:
        return 0.0
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


# --- 学生 t 分布（与 lab_doe 同一套实现，独立以免循环导入）
def _betacf(a, b, x):
    MAXIT, EPS, FPMIN = 200, 3e-12, 1e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c, d = 1.0, 1.0 - qab * x / qap
    if abs(d) < FPMIN:
        d = FPMIN
    d = 1.0 / d
    h = d
    for m in range(1, MAXIT + 1):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < FPMIN:
            d = FPMIN
        c = 1.0 + aa / c
        if abs(c) < FPMIN:
            c = FPMIN
        d = 1.0 / d
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < FPMIN:
            d = FPMIN
        c = 1.0 + aa / c
        if abs(c) < FPMIN:
            c = FPMIN
        d = 1.0 / d
        de = d * c
        h *= de
        if abs(de - 1.0) < EPS:
            break
    return h


def _betai(a, b, x):
    if x <= 0:
        return 0.0
    if x >= 1:
        return 1.0
    lbeta = math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
    bt = math.exp(lbeta + a * math.log(x) + b * math.log(1 - x))
    if x < (a + 1) / (a + b + 2):
        return bt * _betacf(a, b, x) / a
    return 1.0 - bt * _betacf(b, a, 1 - x) / b


def t_cdf(t, df):
    x = df / (df + t * t)
    p = 0.5 * _betai(df / 2.0, 0.5, x)
    return 1.0 - p if t > 0 else p


def t_ppf(p, df):
    """二分求 t 分位数（单调，稳）。"""
    if p <= 0 or p >= 1:
        return float("inf")
    lo, hi = -1e4, 1e4
    for _ in range(200):
        mid = 0.5 * (lo + hi)
        if t_cdf(mid, df) < p:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# --------------------------------------------------------------------------- 数据
def load_rows(path):
    """读 lab CSV；返回按时间排序的行，带段内相对时间。"""
    import csv
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for d in csv.DictReader(f):
            def g(k, dflt=0.0):
                v = d.get(k, "")
                try:
                    return float(v) if v not in ("", None) else dflt
                except ValueError:
                    return dflt

            T = g("cpu_temp_c")
            if T <= 0:                      # 温度传感器失效的样本直接丢
                continue
            rows.append({
                "t": g("elapsed_s"), "ts": g("stage_elapsed_s"), "stage": (d.get("stage") or "").strip(),
                "load": (d.get("load") or "").strip().lower(),
                "T": T, "P": g("cpu_power_w"), "V": g("cpu_volt_v"),
                "f": g("cpu_mhz"), "fan": g("fan_out_pct"), "cap": g("proc_cap"),
                "boost": g("boost", -1), "load_pct": g("cpu_load_pct"),
            })
    rows.sort(key=lambda r: r["t"])
    return rows


def split_segments(rows, min_n=8):
    """按 stage 切段。段内时间用 stage_elapsed_s（每段从 0 起，避免跨段漂移）。"""
    order, buckets = [], {}
    for r in rows:
        if r["stage"] not in buckets:
            buckets[r["stage"]] = []
            order.append(r["stage"])
        buckets[r["stage"]].append(r)
    segs = []
    for sid in order:
        rs = [r for r in buckets[sid] if r["ts"] >= 0]
        if len(rs) < min_n:
            continue
        t0 = rs[0]["ts"]
        segs.append({
            "stage": sid,
            "load": rs[-1]["load"],
            "cap": rs[-1]["cap"],
            "boost": rs[-1]["boost"],
            "rows": rs,
            "n": len(rs),
            "dur": rs[-1]["ts"] - t0,
            "t": [r["ts"] - t0 for r in rs],
            "T": [r["T"] for r in rs],
            # 功耗取 5s 之后（负载建立需要零点几秒，GPS 采样会混进上一档的值）
            "P": mean([r["P"] for r in rs if r["ts"] - t0 >= 5.0]) or mean([r["P"] for r in rs]),
            "fan_pct": mean([r["fan"] for r in rs if r["fan"] >= 0]) if any(r["fan"] >= 0 for r in rs) else None,
            "T0": rs[0]["T"],
        })
    return segs


# --------------------------------------------------------------------------- 单段指数拟合
def fit_exp(ts, Ts, tau_min=4.0, tau_max=900.0, n_grid=180):
    """拟合 T = A + (T₀−A)·exp(−t/τ)。

    对固定 τ 线性于 (A, T₀)：
        T = A·(1−u) + T₀·u,   u = exp(−t/τ)
    外层一维搜 τ 使 SSE 最小，内层闭式解 2 参数。

    返回 A、τ、线性协方差给出的 A 半宽、以及 τ 的 profile 置信集
    （τ 不确定度会通过 A(τ) 的漂移额外放大 A 的区间——必须传播，否则低估）。
    """
    n = len(ts)
    if n < 4:
        return None

    def solve(tau):
        u = [math.exp(-t / tau) for t in ts]
        # 正规方程：参数 [A, T0]，基函数 [(1-u), u]
        s11 = sum((1 - uu) ** 2 for uu in u)
        s12 = sum((1 - uu) * uu for uu in u)
        s22 = sum(uu * uu for uu in u)
        b1 = sum((1 - uu) * T for uu, T in zip(u, Ts))
        b2 = sum(uu * T for uu, T in zip(u, Ts))
        det = s11 * s22 - s12 * s12
        if abs(det) < 1e-12:
            return None
        A = (b1 * s22 - b2 * s12) / det
        T0 = (s11 * b2 - s12 * b1) / det
        resid = [T - (A * (1 - uu) + T0 * uu) for uu, T in zip(u, Ts)]
        sse = sum(r * r for r in resid)
        # (XᵀX)⁻¹
        i11 = s22 / det
        return {"tau": tau, "A": A, "T0": T0, "sse": sse, "i11": i11, "resid": resid}

    # 对数网格 + 细化
    best = None
    lo, hi = math.log(tau_min), math.log(tau_max)
    for k in range(n_grid + 1):
        r = solve(math.exp(lo + (hi - lo) * k / n_grid))
        if r and (best is None or r["sse"] < best["sse"]):
            best = r
    if best is None:
        return None

    # 在最佳点附近细化（τ 的相对步长二分）
    span = (hi - lo) / n_grid
    for _ in range(40):
        span *= 0.5
        for s in (-span, span):
            cand = solve(best["tau"] * math.exp(s))
            if cand and cand["sse"] < best["sse"]:
                best = cand
        if span < 1e-4:
            break

    n_p = 2
    dof = max(1, n - n_p)
    sigma2 = best["sse"] / dof
    sigma = math.sqrt(sigma2)
    tq = t_ppf(0.975, dof)
    lin_half = tq * math.sqrt(max(0.0, sigma2 * best["i11"]))

    # τ 的 profile 置信集：SSE(τ) ≤ SSE_min·(1 + F/(n−p))，F(1, dof, 0.95) ≈ 3.84~4.5
    f_crit = 3.84 + 4.0 / max(1, dof)
    sse_thr = best["sse"] * (1.0 + f_crit / dof)
    tol = 1e-9 * max(1.0, best["sse"])
    tau_lo, tau_hi = best["tau"], best["tau"]
    step = (hi - lo) / n_grid
    for direction in (-1, 1):
        k = 1
        while k < 300:
            tau_try = best["tau"] * math.exp(direction * step * k)
            if tau_try < tau_min or tau_try > tau_max:
                break
            r = solve(tau_try)
            if r is None or r["sse"] > sse_thr + tol:
                break
            if direction < 0:
                tau_lo = tau_try
            else:
                tau_hi = tau_try
            k += 1

    A_lo = min(solve(tau_lo)["A"] if solve(tau_lo) else best["A"], best["A"])
    A_hi = max(solve(tau_hi)["A"] if solve(tau_hi) else best["A"], best["A"])
    prof_half = max(abs(best["A"] - A_lo), abs(A_hi - best["A"]))
    half = math.sqrt(lin_half ** 2 + prof_half ** 2)

    at_upper = best["tau"] > tau_max * 0.98
    at_lower = best["tau"] < tau_min * 1.02

    # 段内净变化量：如果整段几乎没动（|A−T₀| 和噪声同量级），那它本来就已经在稳态，
    # τ 不可辨识完全无所谓 —— A 就是段内均值，这恰恰是对的。
    # 不加这个判断，会把"已经稳态的基线段"误报成"τ 不可辨识、结果不可信"。
    net_change = abs(best["A"] - best["T0"])
    flat = net_change < max(3.0 * sigma, 1.0)

    return {
        "A": best["A"], "tau": best["tau"], "T0": best["T0"],
        "sse": best["sse"], "sigma": sigma, "dof": dof, "n": n,
        "A_half": half, "A_lin_half": lin_half, "A_prof_half": prof_half,
        "tau_lo": tau_lo, "tau_hi": tau_hi,
        "at_upper": at_upper, "at_lower": at_lower,
        "at_bound_harmless": (at_upper or at_lower) and flat,
        "flat": flat, "net_change": net_change,
        # 段末实际走了几个 τ —— 小于 1 就意味着"远没到稳态"
        "tau_covered": (ts[-1] - ts[0]) / best["tau"] if best["tau"] > 0 else 0.0,
        "resid_std": sigma,
    }


# --------------------------------------------------------------------------- 联合拟合
def fit_joint(segs, shared_tau=True):
    """多段联合：分三段各自的渐近 A_k，共享或独立 τ。

    共享 τ 时目标函数是 Σ_k SSE_k(τ)，仍是 1 维搜索（因为给定 τ 后各段解耦）。
    这也顺带回答了"一阶惯性假设成不成立"：若独立 τ 能把 SSE 显著降下来，
    说明 τ 随工况变化，单 τ 模型只是近似。
    """
    fits = [fit_exp(s["t"], s["T"]) for s in segs]
    out = {"shared": None, "indep": None}

    if shared_tau:
        # 网格 + 细化搜共享 τ
        lo, hi = math.log(4.0), math.log(900.0)
        n_grid = 200

        def total(tau):
            tot = 0.0
            for s in segs:
                r = _solve_fixed_tau(s["t"], s["T"], tau)
                if r is None:
                    return float("inf")
                tot += r["sse"]
            return tot

        best_tau, best_sse = None, float("inf")
        for k in range(n_grid + 1):
            tau = math.exp(lo + (hi - lo) * k / n_grid)
            v = total(tau)
            if v < best_sse:
                best_tau, best_sse = tau, v
        span = (hi - lo) / n_grid
        for _ in range(50):
            span *= 0.5
            for s in (-span, span):
                tau = best_tau * math.exp(s)
                v = total(tau)
                if v < best_sse:
                    best_tau, best_sse = tau, v
            if span < 1e-4:
                break

        # 共享 τ 下的每段 A 及其协方差
        n_tot = sum(s["n"] for s in segs)
        p_tot = len(segs) + 1                      # 每段一个 A（T₀ 也算 1 个，下面按 2p 记）
        dof = max(1, n_tot - 2 * len(segs))
        sigma2 = best_sse / dof
        tq = t_ppf(0.975, dof)
        per = []
        for s in segs:
            r = _solve_fixed_tau(s["t"], s["T"], best_tau)
            half = tq * math.sqrt(max(0.0, sigma2 * r["i11"])) if r else float("nan")
            per.append({"stage": s["stage"], "A": r["A"], "T0": r["T0"],
                        "A_half_lin": half, "P": s["P"], "n": s["n"],
                        "dur": s["dur"], "cap": s["cap"], "load": s["load"],
                        "tau_covered": s["dur"] / best_tau if best_tau > 0 else 0.0})
        out["shared"] = {"tau": best_tau, "sse": best_sse, "sigma": math.sqrt(sigma2),
                         "dof": dof, "per": per}

    if any(f is None for f in fits):
        return out
    sse_indep = sum(f["sse"] for f in fits)
    dof_indep = sum(f["dof"] for f in fits)
    out["indep"] = {"sse": sse_indep, "dof": dof_indep,
                    "sigma": math.sqrt(sse_indep / max(1, dof_indep)),
                    "taus": [f["tau"] for f in fits]}
    if out["shared"]:
        # 共享 τ 多花的 SSE，是否值得（近似 F 检验）
        extra = out["shared"]["sse"] - sse_indep
        d_dof = max(1, out["shared"]["dof"] - dof_indep)
        f_stat = (extra / d_dof) / (sse_indep / max(1, dof_indep)) if sse_indep > 0 else 0.0
        out["f_shared_vs_indep"] = f_stat
        out["shared_ok"] = f_stat < 4.0        # F(1,~300,0.95)≈3.84
    return out


def _solve_fixed_tau(ts, Ts, tau):
    u = [math.exp(-t / tau) for t in ts]
    s11 = sum((1 - uu) ** 2 for uu in u)
    s12 = sum((1 - uu) * uu for uu in u)
    s22 = sum(uu * uu for uu in u)
    b1 = sum((1 - uu) * T for uu, T in zip(u, Ts))
    b2 = sum(uu * T for uu, T in zip(u, Ts))
    det = s11 * s22 - s12 * s12
    if abs(det) < 1e-12:
        return None
    A = (b1 * s22 - b2 * s12) / det
    T0 = (s11 * b2 - s12 * b1) / det
    resid = [T - (A * (1 - uu) + T0 * uu) for uu, T in zip(u, Ts)]
    return {"A": A, "T0": T0, "sse": sum(r * r for r in resid), "i11": s22 / det}


# --------------------------------------------------------------------------- 稳态模型
def regress_asymptotes(pts, conf=0.95):
    """A_k = T_amb + R_th·P_k 加权最小二乘（权重 = 1/Var(A_k)）。

    这个回归的**残差**就是"线性稳态模型是否成立"的直接证据：
      · 残差 ≈ 0（相对 A 的区间）→ 线性成立，可以放心外推
      · 残差显著             → 说明有 P 之外的因素（风扇随温度变、二阶级联），
                                线性模型只在该区间内近似可用
    """
    pts = [p for p in pts if p.get("A") is not None]
    if len(pts) < 2:
        return None
    ws = []
    for p in pts:
        h = p.get("A_half") or p.get("A_half_lin") or 0.0
        w = 1.0 / max(h * h, 1e-6)
        ws.append(w)
    sw = sum(ws)
    xbar = sum(w * p["P"] for w, p in zip(ws, pts)) / sw
    ybar = sum(w * p["A"] for w, p in zip(ws, pts)) / sw
    sxx = sum(w * (p["P"] - xbar) ** 2 for w, p in zip(ws, pts))
    if sxx < 1e-9:
        return None
    sxy = sum(w * (p["P"] - xbar) * (p["A"] - ybar) for w, p in zip(ws, pts))
    b = sxy / sxx
    a = ybar - b * xbar

    resid = [p["A"] - (a + b * p["P"]) for p in pts]
    chi2 = sum(w * r * r for w, r in zip(ws, resid))
    dof = len(pts) - 2

    # 线性性判断用**绝对温度残差**，不用 χ²。
    # 原因：短时外推把 A 的区间压得很小（±0.1℃），此时 CHI² 必然爆炸，
    # 但 1℃ 的非线性对控制律毫无影响。判据要跟"用途"对齐，不能跟"统计显著性"对齐。
    resid_max = max(abs(r) for r in resid)
    linear_ok = resid_max <= 1.0

    # 参数区间（近似：把权重当已知）
    se_b = math.sqrt(1.0 / sxx)
    se_a = math.sqrt(1.0 / sw + xbar ** 2 / sxx)
    tq = t_ppf(1 - (1 - conf) / 2, max(1, dof)) if dof > 0 else 1.96

    # 同时给出未加权 OLS（权重可能被少数低方差点主导，做个交叉检查）
    if len(pts) >= 2:
        mx = mean([p["P"] for p in pts])
        my = mean([p["A"] for p in pts])
        sxx_u = sum((p["P"] - mx) ** 2 for p in pts)
        sxy_u = sum((p["P"] - mx) * (p["A"] - my) for p in pts)
        b_u = sxy_u / sxx_u if sxx_u > 1e-9 else 0.0
        a_u = my - b_u * mx
    else:
        a_u = b_u = 0.0

    return {
        "Tamb": a, "R": b,
        "Tamb_ci": (a - tq * se_a, a + tq * se_a),
        "R_ci": (b - tq * se_b, b + tq * se_b),
        "chi2_per_dof": chi2 / dof if dof > 0 else float("nan"),
        "resid_max": resid_max,
        "linear_ok": linear_ok,
        "resid": [round(r, 2) for r in resid],
        "Tamb_ols": a_u, "R_ols": b_u,
        "points": [{"P": round(p["P"], 1), "A": round(p["A"], 2),
                    "half": round(p.get("A_half") or 0.0, 2), "stage": p["stage"]}
                   for p in pts],
    }


def naive_tail(seg, tail=0.35):
    """"直接取段尾均值"——短实验最常见（也是错误）的做法，用来量化偏差。"""
    n = max(3, int(len(seg["T"]) * tail))
    return mean(seg["T"][-n:])


# --------------------------------------------------------------------------- 主分析
def analyze(csv_path, target_s=None, conf=0.95):
    rows = load_rows(csv_path)
    if not rows:
        raise SystemExit(f"CSV 里没有有效数据：{csv_path}")
    segs = split_segments(rows)
    if len(segs) < 2:
        raise SystemExit("有效段数 < 2，无法做瞬态辨识（至少需要 2 个不同功率的段）")

    joint = fit_joint(segs, shared_tau=True)
    sh = joint["shared"]
    indep = joint["indep"]

    # 逐段独立拟合（拿各自的 τ 和 profile 放大后的 A 区间）
    per = []
    for s in segs:
        f = fit_exp(s["t"], s["T"])
        if not f:
            continue
        per.append({
            "stage": s["stage"], "load": s["load"], "cap": s["cap"],
            "dur": round(s["dur"], 1), "n": s["n"], "P": round(s["P"], 1),
            "T0": round(s["T0"], 2),
            "A": round(f["A"], 2), "A_half": round(f["A_half"], 2),
            "tau": round(f["tau"], 1),
            "tau_ci": (round(f["tau_lo"], 1), round(f["tau_hi"], 1)),
            "tau_covered": round(f["tau_covered"], 2),
            "sigma": round(f["sigma"], 3),
            "at_bound": bool((f["at_upper"] or f["at_lower"]) and not f["at_bound_harmless"]),
            "flat": f["flat"],
            "naive_tail": round(naive_tail(s), 2),
            # 直接取尾均值比外推低估多少 —— 这就是"5 分钟为什么不够"的数字
            "naive_bias": round(f["A"] - naive_tail(s), 2),
            "fan_pct": None if s["fan_pct"] is None else round(s["fan_pct"], 1),
        })

    # 用**逐段独立 τ** 的 A 做稳态回归。
    # 不用共享 τ 的 A：实测 τ 确实随工况变（风扇随温度加速，散热能力变），
    # 强行共享会把偏差压进残差里。共享 τ 只作为 τ 的汇总值报告。
    reg_pts = [{"stage": p["stage"], "P": p["P"], "A": p["A"],
                "A_half": p["A_half"], "load": p["load"], "cap": p["cap"]}
               for p in per]
    reg = regress_asymptotes(reg_pts, conf=conf)

    total_s = sum(s["dur"] for s in segs)
    res = {
        "csv": os.path.abspath(csv_path),
        "tag": os.path.basename(csv_path).replace("lab-", "").replace(".csv", ""),
        "total_s": round(total_s, 0),
        "target_s": target_s,
        "n_stages": len(segs),
        "segments": per,
        "shared_tau": None if not sh else {
            "tau": round(sh["tau"], 1), "sigma": round(sh["sigma"], 3),
            "dof": sh["dof"],
        },
        "indep_taus": None if not indep else [round(t, 1) for t in indep["taus"]],
        "f_shared_vs_indep": round(joint.get("f_shared_vs_indep", float("nan")), 2),
        "shared_ok": joint.get("shared_ok"),
        "steady_model": reg,
    }
    res["verdict"] = judge(res)
    return res, rows, segs


def judge(res):
    """回答两个问题：① 这段数据能外推吗 ② 线性稳态模型成立吗。"""
    per = res["segments"]
    issues, notes = [], []

    # 只统计"真的在动"的段：几乎不动的段本来就在稳态，τ 覆盖度对它没意义
    moving = [p for p in per if not p.get("flat")]
    covered = [p["tau_covered"] for p in moving]
    min_cov = min(covered) if covered else float("inf")
    max_cov = max(covered) if covered else float("inf")
    if not covered:
        min_cov = max_cov = 0.0
        notes.append("所有段都接近稳态（净变化 < 3σ），本次没有可用于辨识 τ 的升降温过程")

    if min_cov < 0.7:
        issues.append(f"最短的一段只走了 {min_cov:.2f}τ（<0.7τ），外推距离远，"
                      f"该段 A 的区间会被放大")
    if max_cov < 1.5:
        issues.append(f"最长的一段也只走 {max_cov:.2f}τ（<1.5τ），τ 主要靠曲率辨识，噪声大时可信度低")

    bounded = [p["stage"] for p in per if p["at_bound"]]
    if bounded:
        issues.append("以下段的 τ 不可辨识（段太短或几乎平直，τ 顶到搜索边界）："
                      + "、".join(bounded)
                      + " —— 它们的 A 取段内均值，若该段本来已接近稳态则无妨，"
                        "否则 A 会明显偏低")

    if res.get("shared_ok") is False:
        notes.append(f"逐段 τ 差异显著（共享 τ 的 F={res['f_shared_vs_indep']:.1f}），"
                     "说明 τ 随工况变化 —— 这是真的（风扇转速变→散热能力变），"
                     "所以 A 用的是逐段 τ 而不是共享 τ")

    sm = res.get("steady_model")
    if sm:
        if not sm["linear_ok"]:
            issues.append(f"A = T₀ + R·P 的最大残差 {sm['resid_max']:.2f}℃（>1℃），"
                          "线性稳态形式不足 —— 应改用二次或按风扇分档的 R_th")
        rng = sm["R_ci"][1] - sm["R_ci"][0]
        if abs(sm["R"]) > 1e-9 and rng > abs(sm["R"]) * 0.8:
            issues.append(f"R_th 的相对区间宽（±{(rng / 2) / abs(sm['R']) * 100:.0f}%），"
                          "外推温度的不确定度主要来自这里")
    else:
        issues.append("只有 1 个有效功率点，T₀ 与 R_th 无法分离（只能得到渐近温度本身）")

    biases = [p["naive_bias"] for p in per if p["naive_bias"] is not None]
    if biases:
        worst = max(biases, key=abs)
        notes.append(f"若直接取段尾均值（最常见做法），最大偏差 {worst:+.2f}℃ —— "
                     f"这就是短实验不做外推时引入的系统误差")

    return {
        "usable": len(issues) == 0,
        "issues": issues,
        "notes": notes,
        "min_tau_covered": round(min_cov, 2),
    }


# --------------------------------------------------------------------------- 合成稳态 CSV
SYNTH_NOTE = ("synthetic=1：温度列已被替换为瞬态外推的渐近值 A_k "
              "（非实测稳态），仅供下游 lab_model/lab_doe 估计参数")


def write_synth_csv(path, rows, segs, res):
    """把每段的温度替换为外推渐近值，让下游稳态链路（lab_model/lab_doe）能消费。

    必须清楚标记：这不是实测稳态。文件名带 -synth，manifest 里也有 synthetic=1。
    """
    A_of = {p["stage"]: p["A"] for p in res["segments"]}
    import csv
    hdr = ["timestamp", "elapsed_s", "stage", "stage_elapsed_s", "load",
           "cpu_temp_c", "cpu_core_max_c", "cpu_volt_v", "cpu_load_pct",
           "cpu_mhz", "cpu_power_w", "gpu_temp_c", "gpu_load_pct", "gpu_power_w",
           "fan_rpm", "fan_out_pct", "proc_cap", "boost"]
    out = path.replace(".csv", "-synth.csv")
    with open(out, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(hdr)
        for r in rows:
            A = A_of.get(r["stage"])
            if A is None:
                continue
            w.writerow([
                "", r["t"], r["stage"], r["ts"], r["load"],
                f"{A:.2f}", f"{A:.2f}", f"{r['V']:.3f}", f"{r['load_pct']:.0f}",
                f"{r['f']:.0f}", f"{r['P']:.2f}", "", "", "",
                "", "" if r["fan"] < 0 else f"{r['fan']:.0f}",
                f"{r['cap']:.0f}", f"{r['boost']:.0f}",
            ])
    return out


# --------------------------------------------------------------------------- 策略导出
def build_policy(res, conf=0.95):
    """由瞬态结果直接导出 C# PolicyModel 可读的策略文件。

    为什么短时数据要单独导出，而不是交给 lab_model.py：
      lab_model 要 ≥4 个满载阶段才能做交叉验证选模型族。5 分钟设计只有 2 个满载档，
      它必然拒绝。而 2 个功率点 + 1 个空闲点刚好能给出三档稳态温度。

    为什么用**阶梯表**而不是线性 T=T₀+R·P：
      三个点拟一条直线，中间点必然有残差（实测 cap60 偏 1.6℃，折到温度上 2.2℃）。
      而控制律只会在**实测过的 cap** 上跳档，那么每一步直接用该档实测的渐近温度
      就是零模型误差。这也正是"阶梯式离散数列"的本意。
      线性形式只在报告里保留，用来判断"是否需要二次项"，不参与控制。

    阶梯表**只覆盖实测 cap**（如 60%/100%），中间不插值：
      cap→功耗受 Turbo 台阶与 P≈C·V²·f 的影响强烈非线性，插值实测偏 5℃ 量级。
    """
    loaded = [p for p in res["segments"] if p["load"] == "cpu"]
    if len(loaded) < 2:
        return None
    loaded.sort(key=lambda p: p["P"])

    # 每个实测档：cap → (稳态功耗, 渐近温度)。档位只用满载段（空闲段没有 cap 语义）
    table = [{"cap": int(round(p["cap"])), "P": p["P"], "T": p["A"],
              "T_half": p["A_half"]} for p in loaded]

    def T_of_cap(cap):
        for r in table:
            if r["cap"] == cap:
                return r["T"]
        return None

    # 阶梯表（按 P 分箱，边界取相邻点中值）——与 lab_model.py 的 stepwise 格式一致。
    # 分箱必须用**全部段**（含空闲），否则低功耗区会被并到最低的满载档上，
    # PredictSteady(5W) 会回 55℃ 这种明显错的数。
    allp, seen_p = [], set()
    for p in sorted(res["segments"], key=lambda x: x["P"]):
        key = round(p["P"], 1)
        if key in seen_p:
            continue
        seen_p.add(key)
        allp.append(p)
    bins = []
    for i, p in enumerate(allp):
        lo = 0.0 if i == 0 else 0.5 * (allp[i - 1]["P"] + p["P"])
        hi = None if i == len(allp) - 1 else 0.5 * (p["P"] + allp[i + 1]["P"])
        bins.append([round(lo, 3), None if hi is None else round(hi, 3), round(p["A"], 2)])

    MARGIN = 1.0          # 留 1℃ 裕量：外推本身有 ~1℃ 误差，不该贴着目标去调
    levels = sorted({r["cap"] for r in table}, reverse=True)
    ladder = []
    for T_hi in range(95, 54, -5):
        best = None
        for cap in levels:                     # 从高到低，第一个满足即最高性能档
            pt = T_of_cap(cap)
            if pt is not None and pt + MARGIN <= T_hi:
                best = (cap, pt)
                break
        if best:
            cap, pt = best
            P = next(r["P"] for r in table if r["cap"] == cap)
            ladder.append({
                "T_hi": T_hi, "achievable": True,
                "options": [{"cap": int(cap), "fan": None, "P": round(P, 1),
                             "f": 0, "pred_T": round(pt, 1), "spl_delta_db": 0.0,
                             "mode": "唯一（风扇由固件控制）"}],
            })
        else:
            ladder.append({"T_hi": T_hi, "achievable": False, "options": []})

    sm = res.get("steady_model")
    tau = res["shared_tau"]["tau"] if res.get("shared_tau") else 60.0
    return {
        "version": 1,
        "source_csv": os.path.basename(res["csv"]),
        "mode": "bios",
        "fan_controllable": False,
        "origin": "transient_extrapolated",
        "features": ["功耗 P (W)"],
        "steady_model": {"type": "stepwise", "bins": bins},
        "inversion_model": "阶梯表（实测档位直接查表，档间不插值）",
        "dynamic": {"tau_s": round(tau, 2),
                    "alpha": round(1 - math.exp(-1.0 / max(tau, 1e-6)), 5)},
        "control_ladder": ladder,
        "reference": None if not sm else {
            "T_base": round(sm["Tamb"], 2), "rth_closed_loop": round(sm["R"], 4),
            "note": "线性近似仅作参考；实际控制用阶梯表。残差大说明需要二次项",
            "linear_resid_max_c": round(sm["resid_max"], 2),
        },
        "caps_measured": [r["cap"] for r in table],
        "cap_power_map": {
            "points": [[r["cap"], round(r["P"], 1)] for r in table],
            "note": ("仅实测 cap 有档位，中间不插值：cap→功耗受 Turbo 台阶与 "
                     "P≈C·V²·f 影响强烈非线性，插值会偏 5℃ 量级"),
        },
        "quality": {
            "stages": res["n_stages"],
            "total_s": res["total_s"],
            "tau_s": tau,
            "table": [{"cap": r["cap"], "P": round(r["P"], 1),
                       "T_ss": round(r["T"], 2), "half": round(r["T_half"], 2)}
                      for r in table],
            "note": "短时实验（未达稳态）：温度由升降温轨迹外推，误差约 1℃ 量级",
        },
    }


# --------------------------------------------------------------------------- 渲染
def _sig(x, nd=2):
    return "—" if x is None or x != x else f"{x:.{nd}f}"


def render_md(res):
    L = []
    sm = res.get("steady_model")
    st = res.get("shared_tau")
    L.append(f"# 瞬态辨识报告：{res['tag']}")
    L.append("")
    L.append(f"数据：{res['n_stages']} 段，共 {res['total_s'] / 60:.1f} 分钟（{res['total_s']:.0f} 秒）")
    if st:
        L.append(f"共享热时间常数 τ = **{st['tau']:.1f} s**（残差 σ = {st['sigma']:.2f}℃，dof = {st['dof']}）")
    if res.get("indep_taus"):
        L.append("逐段独立 τ：" + " / ".join(f"{t:.0f}s" for t in res["indep_taus"])
                 + f"（与共享 τ 的 F = {res['f_shared_vs_indep']:.1f}"
                 + ("，共享可接受" if res.get("shared_ok") else "，共享偏紧") + "）")
    L.append("")

    L.append("## 每段外推结果")
    L.append("")
    L.append("| 段 | cap | 负载 | 时长 | 稳态功耗 | 渐近温度 A | ±95% | τ | 走了几个τ | 尾均值 | 尾均值偏差 |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|")
    for p in res["segments"]:
        L.append(f"| {p['stage']} | {p['cap']:.0f}% | {p['load']} | {p['dur']:.0f}s | "
                 f"{p['P']:.1f}W | **{p['A']:.2f}℃** | ±{p['A_half']:.2f} | {p['tau']:.0f}s | "
                 f"{p['tau_covered']:.2f} | {p['naive_tail']:.2f}℃ | {p['naive_bias']:+.2f}℃ |")
    L.append("")

    if sm:
        L.append("## 稳态模型 A = T₀ + R_th·P")
        L.append("")
        L.append(f"- T₀（P=0 截距）= **{sm['Tamb']:.1f}℃**  "
                 f"[{sm['Tamb_ci'][0]:.1f}, {sm['Tamb_ci'][1]:.1f}]")
        L.append(f"- R_th = **{sm['R']:.3f} ℃/W**  "
                 f"[{sm['R_ci'][0]:.3f}, {sm['R_ci'][1]:.3f}]")
        L.append(f"- 最大残差 {sm['resid_max']:.2f}℃ → "
                 + ("线性模型够用（<1℃）" if sm["linear_ok"] else "**线性模型不足**（>1℃）"))
        L.append(f"- 未加权 OLS 交叉检查：T₀={sm['Tamb_ols']:.1f}℃，R={sm['R_ols']:.3f} ℃/W")
        L.append("")
        L.append("### 外推后的控制依据")
        L.append("")
        L.append("| 稳态功耗 | 预测稳态温度 |")
        L.append("|---|---|")
        seen = set()
        for p in sorted(sm["points"], key=lambda x: x["P"]):
            key = round(p["P"], 1)
            if key in seen:
                continue
            seen.add(key)
            L.append(f"| {p['P']:.1f} W | {p['A']:.1f}℃ |")
        L.append("")

    v = res["verdict"]
    L.append("## 判断")
    L.append("")
    L.append(f"- 可用性：{'**可以直接用**' if v['usable'] else '**需要谨慎**'}")
    L.append(f"- 最短段覆盖 {v['min_tau_covered']:.2f}τ")
    for s in v["issues"]:
        L.append(f"- ⚠ {s}")
    for s in v["notes"]:
        L.append(f"- ℹ {s}")
    L.append("")
    return "\n".join(L)


def render_html(res, md_path, out_path):
    sm = res.get("steady_model")
    st = res.get("shared_tau")
    v = res["verdict"]

    rows = []
    for p in res["segments"]:
        cls = "warn" if p["tau_covered"] < 0.7 or p["at_bound"] else ""
        rows.append(
            f"<tr class='{cls}'><td>{p['stage']}</td><td>{p['cap']:.0f}%</td><td>{p['load']}</td>"
            f"<td>{p['dur']:.0f}s</td><td>{p['P']:.1f}</td>"
            f"<td class='num'><b>{p['A']:.2f}</b></td><td class='num'>±{p['A_half']:.2f}</td>"
            f"<td class='num'>{p['tau']:.0f}</td><td class='num'>{p['tau_covered']:.2f}</td>"
            f"<td class='num'>{p['naive_tail']:.2f}</td>"
            f"<td class='num delt'>{p['naive_bias']:+.2f}</td></tr>")

    # 上升曲线示意：每段 A 与尾均值
    bar = []
    if sm:
        mx = max([p["A"] for p in sm["points"]] + [1])
        for p in sm["points"]:
            hgt = p["A"] / mx * 160
            bar.append(
                f"<div class='bar'><div class='bfill' style='height:{hgt:.0f}px'></div>"
                f"<div class='bval'>{p['A']:.1f}</div>"
                f"<div class='blab'>{p['P']:.0f}W</div></div>")
    bars = (f"<div class='chart'>"
            f"<div class='cline'></div>{''.join(bar)}</div>") if bar else ""

    issues = "".join(f"<li class='warn'>{s}</li>" for s in v["issues"]) or "<li class='ok'>无阻断问题</li>"
    notes = "".join(f"<li class='dim'>{s}</li>" for s in v["notes"])

    html = f"""<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>瞬态辨识 · {res['tag']}</title>
<style>
:root{{--bg:#0f1620;--card:#16202c;--line:#243244;--fg:#e6eef7;--mut:#8b9bb0;
--acc:#4fb0c6;--warn:#e0a45c;--ok:#5fb87a;--bad:#c9736a;}}
*{{box-sizing:border-box}}
body{{margin:0;padding:28px;background:var(--bg);color:var(--fg);
font:14px/1.65 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}}
h1{{font-size:22px;font-weight:600;margin:0 0 4px}}
h2{{font-size:15px;font-weight:600;margin:28px 0 12px;color:var(--acc);
letter-spacing:.02em;border-left:3px solid var(--acc);padding-left:9px}}
.sub{{color:var(--mut);font-size:12.5px;margin-bottom:22px}}
.kpi{{display:flex;gap:14px;flex-wrap:wrap;margin-bottom:8px}}
.k{{background:var(--card);border:1px solid var(--line);border-radius:9px;
padding:13px 17px;min-width:150px}}
.k .lb{{color:var(--mut);font-size:11.5px}}
.k .vl{{font-size:21px;font-weight:600;margin-top:3px}}
.k .vl small{{font-size:11.5px;color:var(--mut);font-weight:400}}
table{{border-collapse:collapse;width:100%;background:var(--card);
border:1px solid var(--line);border-radius:9px;overflow:hidden;font-size:12.5px}}
th{{background:#1b2735;color:var(--mut);font-weight:500;text-align:left;
padding:8px 10px;font-size:11.5px;white-space:nowrap}}
td{{padding:7px 10px;border-top:1px solid var(--line)}}
td.num{{font-variant-numeric:tabular-nums;text-align:right}}
tr.warn td{{background:rgba(224,164,92,.08)}}
tr.warn td.delt{{color:var(--warn)}}
ul{{margin:0;padding-left:20px}} li{{margin:4px 0}}
li.warn{{color:var(--warn)}} li.ok{{color:var(--ok)}} li.dim{{color:var(--mut);font-size:12.5px}}
.chart{{display:flex;align-items:flex-end;gap:26px;height:210px;
background:var(--card);border:1px solid var(--line);border-radius:9px;
padding:20px 24px 10px;margin-top:6px}}
.bar{{flex:1;display:flex;flex-direction:column;justify-content:flex-end;
align-items:center;height:100%}}
.bfill{{width:100%;max-width:64px;background:linear-gradient(180deg,#4fb0c6,#2b6f80);
border-radius:4px 4px 0 0;transition:height .3s}}
.bval{{font-size:12px;color:var(--fg);margin-bottom:3px;font-variant-numeric:tabular-nums}}
.blab{{font-size:11px;color:var(--mut);margin-top:6px}}
.note{{background:rgba(224,164,92,.1);border:1px solid rgba(224,164,92,.28);
border-radius:8px;padding:11px 14px;color:#e8c9a0;font-size:12.5px;margin-top:14px}}
</style></head><body>
<h1>瞬态辨识 · {res['tag']}</h1>
<div class="sub">{res['n_stages']} 段 / 共 {res['total_s'] / 60:.1f} 分钟 ·
温度列来自升降温轨迹外推，<b>不是实测稳态</b></div>

<div class="kpi">
  <div class="k"><div class="lb">热时间常数 τ</div>
    <div class="vl">{_sig(st['tau'], 0) if st else '—'} <small>s</small></div></div>
  <div class="k"><div class="lb">热阻 R_th</div>
    <div class="vl">{_sig(sm['R'], 3) if sm else '—'} <small>℃/W</small></div></div>
  <div class="k"><div class="lb">T₀（P=0 截距）</div>
    <div class="vl">{_sig(sm['Tamb'], 1) if sm else '—'} <small>℃</small></div></div>
  <div class="k"><div class="lb">线性性 最大残差</div>
    <div class="vl">{_sig(sm['resid_max'], 2) if sm else '—'}
    <small>{'够用' if sm and sm['linear_ok'] else '不足'}</small></div></div>
</div>

<h2>每段外推</h2>
<table><tr><th>段</th><th>cap</th><th>负载</th><th>时长</th><th>功耗 W</th>
<th>渐近温度 A</th><th>±95%</th><th>τ s</th><th>τ 覆盖</th><th>尾均值</th><th>偏差</th></tr>
{''.join(rows)}</table>
<div class="note">τ 覆盖 &lt; 0.7 的段（黄底）外推距离超过一个时间常数，
区间会被放大；"偏差"= 渐近值 − 段尾均值，即不做外推时的系统误差。</div>

<h2>外推后的稳态温度</h2>
{bars or '<div class="dim">功率点不足，无法给出</div>'}

<h2>判断</h2>
<ul>{issues}{notes}</ul>
</body></html>"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(html)
    return out_path


# --------------------------------------------------------------------------- CLI
def main():
    args = [a for a in sys.argv[1:]]
    if not args:
        args = [os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lab-data")]
    target_s = None
    paths = []
    i = 0
    while i < len(args):
        if args[i] == "--target-s" and i + 1 < len(args):
            target_s = float(args[i + 1])
            i += 2
        elif args[i] == "--no-synth":
            i += 1
        else:
            paths.append(args[i])
            i += 1

    base = paths[0] if paths else "."
    if os.path.isdir(base):
        csvs = sorted(glob.glob(os.path.join(base, "lab-*.csv")))
        csvs = [c for c in csvs if "-synth" not in c]
        if not csvs:
            raise SystemExit(f"目录下没有 lab-*.csv：{base}")
        targets = csvs
    else:
        targets = [base]

    rc = 0
    for path in targets:
        res, rows, segs = analyze(path, target_s=target_s)
        out_dir = os.path.dirname(os.path.abspath(path))
        tag = res["tag"]

        jp = os.path.join(out_dir, f"transient-{tag}.json")
        with open(jp, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        md = render_md(res)
        mp = os.path.join(out_dir, f"transient-{tag}.md")
        with open(mp, "w", encoding="utf-8") as f:
            f.write(md)
        hp = render_html(res, mp, os.path.join(out_dir, f"transient-{tag}.html"))

        synth = write_synth_csv(path, rows, segs, res)
        with open(synth.replace(".csv", ".json"), "w", encoding="utf-8") as f:
            json.dump({"synthetic": True, "note": SYNTH_NOTE,
                       "source_csv": os.path.abspath(path),
                       "asymptotes": {p["stage"]: p["A"] for p in res["segments"]},
                       "tau_s": res["shared_tau"]["tau"] if res.get("shared_tau") else None},
                      f, ensure_ascii=False, indent=2)

        # 策略文件用 model-*-transient.json：程序的「用最新结果」按 model-*.json 找，
        # 这样短时结果也能被界面直接载入，同时文件名里带 transient 以免与全量模型混淆。
        policy = build_policy(res)
        pol_path = None
        if policy:
            pol_path = os.path.join(out_dir, f"model-{tag}-transient.json")
            with open(pol_path, "w", encoding="utf-8") as f:
                json.dump(policy, f, ensure_ascii=False, indent=2)

        print(md)
        print(f"CSV      : {path}")
        print(f"合成稳态 : {synth}   ← lab_model 用这个")
        if pol_path:
            ok = sum(1 for r in policy["control_ladder"] if r["achievable"])
            print(f"策略文件 : {pol_path}   （{ok} 档可达，程序「用最新结果」可直接载入）")
        else:
            print("策略文件 : 未生成（功率点不足，无法定 T₀ 与 R_th）")
        print(f"报告     : {hp}")
        if not res["verdict"]["usable"]:
            rc = max(rc, 1)

    return rc


if __name__ == "__main__":
    sys.exit(main())
