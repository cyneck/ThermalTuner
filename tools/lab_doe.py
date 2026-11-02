#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lab_doe.py —— 可辨识性预判与序贯实验设计（adaptive / sequential DOE）

和 lab_model.py 的分工：
  lab_model.py  回答「我拟合出了什么」—— 跑完全部阶段后，比模型、出策略表。
  lab_doe.py    回答「我还能知道什么」—— **在实验中途**，判断当前数据够不够、
                再跑一档能换来多少精度、值不值得跑、什么时候可以停。

三个问题：
  1) 现在这套数据，在我关心的目标温度附近能预测多准？
     —— 不是全局 CV-RMSE，而是**该点上的 95% 预测区间**（含单次观测噪声）。
  2) 再跑一档，那个区间能收窄多少？哪一档性价比最高？
  3) 什么时候停？

方法（标准统计工具，没有自创算法）：
  · 线性基底模型 y = Φθ，Fisher 信息矩阵 M = ΦᵀΦ/σ²
  · 预测方差在 x* 处：Var = σ²·(1 + φ(x*)ᵀ(ΦᵀΦ)⁻¹φ(x*))
  · 新增一点用 Sherman–Morrison 增量更新 (ΦᵀΦ)⁻¹，不必重算
  · 评分 = 目标温度集合上「预测区间半宽的降低量之和」，除以该档耗时
    （这是 c-/I-optimality 的工程化：我们真正关心的不是参数准，而是**反解准**）
  · 学生 t 分布：小样本自由度可能只有 3–6，用正态近似会把区间低估一半以上

用法：
    python tools/lab_doe.py lab-data/lab-doe1-xxx.csv
    python tools/lab_doe.py lab-data                      # 取目录里最新的 lab-*.csv
    python tools/lab_doe.py lab-data/lab-x.csv --targets 75,80,85 --budget 600
    python tools/lab_doe.py lab-data/lab-x.csv --json out.json

退出码：0 = 建议继续跑；2 = 可以停止（数据已够）；3 = 数据不足无法评估。
"""

from __future__ import annotations

import csv
import glob
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lab_model import (  # noqa: E402
    TERM_SETS, load_rows, mean, stage_features, stdev,
)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# =============================================================== 统计工具
def _betacf(a, b, x):
    """不完全 Beta 函数的连分式（Numerical Recipes 6.4）。"""
    MAXIT, EPS, FPMIN = 300, 3e-14, 1e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
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
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    lbeta = math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
    bt = math.exp(lbeta + a * math.log(x) + b * math.log(1.0 - x))
    if x < (a + 1.0) / (a + b + 2.0):
        return bt * _betacf(a, b, x) / a
    return 1.0 - bt * _betacf(b, a, 1.0 - x) / b


def t_cdf(t, df):
    if df <= 0:
        return float("nan")
    x = df / (df + t * t)
    p = 0.5 * _betai(df / 2.0, 0.5, x)
    return 1.0 - p if t > 0 else p


def t_ppf(p, df):
    """t 分布分位数（二分求逆，足够快且稳）。"""
    if df <= 0:
        return float("nan")
    if df > 400:
        # 自由度很大时 t→正态，直接反查正态更快也更准
        return _norm_ppf(p)
    lo, hi = -1e4, 1e4
    for _ in range(200):
        mid = 0.5 * (lo + hi)
        if t_cdf(mid, df) < p:
            lo = mid
        else:
            hi = mid
        if hi - lo < 1e-10:
            break
    return 0.5 * (lo + hi)


def _norm_ppf(p):
    """Acklam 有理逼近，误差 < 1.2e-9。"""
    a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
         1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00]
    b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
         6.680131188771972e+01, -1.328068155288572e+01]
    c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
         -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00]
    d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
         3.754408661907416e+00]
    plow, phigh = 0.02425, 1 - 0.02425
    if p < plow:
        q = math.sqrt(-2 * math.log(p))
        return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / \
               ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1)
    if p > phigh:
        q = math.sqrt(-2 * math.log(1 - p))
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / \
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1)
    q = p - 0.5
    r = q * q
    return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / \
           (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1)


def m_inv(A):
    """Gauss-Jordan 求逆，带部分主元。奇异返回 None。"""
    n = len(A)
    M = [list(row) + [1.0 if i == j else 0.0 for j in range(n)] for i, row in enumerate(A)]
    for i in range(n):
        p = max(range(i, n), key=lambda r: abs(M[r][i]))
        if abs(M[p][i]) < 1e-13:
            return None
        M[i], M[p] = M[p], M[i]
        pv = M[i][i]
        M[i] = [v / pv for v in M[i]]
        for r in range(n):
            if r != i and abs(M[r][i]) > 1e-18:
                f = M[r][i]
                M[r] = [a - f * b for a, b in zip(M[r], M[i])]
    return [row[n:] for row in M]


def cond_number(A):
    """设计矩阵 X 的条件数 κ(X) = sqrt(λmax/λmin)，其中 A = XᵀX。

    为什么用 κ(X) 而不是 λmax/λmin：回归诊断的行业阈值（Belsley–Kuh–Welsch）
    是对 κ(X) 说的——>30 中等共线、>100 严重。对 XᵀX 直接报比值会把数字平方，
    让"其实还好"的情况看着很吓人。
    """
    n = len(A)

    def mv(v):
        return [sum(A[i][j] * v[j] for j in range(n)) for i in range(n)]

    def power(mat_vec, v, iters=400):
        lam = 0.0
        for _ in range(iters):
            w = mat_vec(v)
            nrm = math.sqrt(sum(x * x for x in w))
            if nrm < 1e-300:
                return 0.0, v
            w = [x / nrm for x in w]
            lam = sum(w[i] * mat_vec(w)[i] for i in range(n))
            done = all(abs(w[i] - v[i]) < 1e-13 for i in range(n))
            v = w
            if done:
                break
        return lam, v

    lmax, _ = power(mv, [1.0] * n)
    if lmax <= 0:
        return float("inf")

    Ai = m_inv(A)
    if Ai is None:
        return float("inf")

    def mvi(v):
        return [sum(Ai[i][j] * v[j] for j in range(n)) for i in range(n)]

    # 对 A⁻¹ 做主特征值迭代：得到的 λ 是 1/λmin(A)。
    # 起点必须用与 A 的主特征向量无关的向量（比如全 1），否则会卡在同一个方向上。
    lam_inv, _ = power(mvi, [1.0 + 0.01 * i for i in range(n)])
    if lam_inv <= 1e-18:
        return float("inf")
    # κ(X) = β/α，其中 β²=λmax, α²=λmin=1/lam_inv
    return math.sqrt(lmax * lam_inv)


# =============================================================== cap → P 映射
class PowerMap:
    """把「处理器上限 cap(%)」映射成「稳态功耗 W」。

    必须用实测点分段插值，不能一条直线贯穿：同一个 cap 在 Turbo 开/关下功耗差一大截
    （实测 cap100 是 40W vs 29W），直线拟合会让反解系统性偏移。
    """

    def __init__(self, stages):
        pts = {}
        for s in stages:
            if s["cap"] > 0 and s["P"] > 0:
                pts.setdefault(round(s["cap"]), []).append(s["P"])
        self.caps = sorted(pts)
        self.ps = [mean(pts[c]) for c in self.caps]
        self.spread = {c: (min(pts[c]), max(pts[c])) for c in self.caps}
        self.ambiguous = [c for c in self.caps
                          if len(self.spread[c]) > 1
                          and self.spread[c][1] > self.spread[c][0] * 1.20]

    def at(self, cap):
        if not self.caps:
            return None
        caps, ps = self.caps, self.ps
        if cap <= caps[0]:
            if len(caps) == 1:
                return ps[0] * max(0.2, cap / caps[0])
            k = (ps[1] - ps[0]) / max(1e-6, caps[1] - caps[0])
            return max(1.0, ps[0] + k * (cap - caps[0]))
        if cap >= caps[-1]:
            if len(caps) == 1:
                return ps[0]
            k = (ps[-1] - ps[-2]) / max(1e-6, caps[-1] - caps[-2])
            return max(1.0, ps[-1] + k * (cap - caps[-1]))
        for i in range(len(caps) - 1):
            if caps[i] <= cap <= caps[i + 1]:
                w = (cap - caps[i]) / max(1e-6, caps[i + 1] - caps[i])
                return ps[i] + w * (ps[i + 1] - ps[i])
        return ps[-1]


# =============================================================== 可辨识性分析
class Ident:
    """线性基底模型的可辨识性。

    所有计算都在 **z-score 标准化空间** 进行：P 的量级是几十、fan 是零点几，
    不标准化的话 (ΦᵀΦ) 的条件数没有物理意义，增量更新也会被大数吞掉小数。
    """

    def __init__(self, term_key, stages, use_fan, conf=0.95):
        self.term_key = term_key
        self.label, self.needs_fan, self.terms = TERM_SETS[term_key]
        self.use_fan = use_fan
        self.conf = conf
        self.names = ["1"] + [n for n, _ in self.terms]

        raw = [self._raw(s) for s in stages]
        p = len(self.names)
        self.ok = False
        self.reason = ""
        # 未成功建模时也要给齐字段，避免下游 KeyError
        self.dof = 0
        self.sigma = float("nan")
        self.cond = float("inf")
        self.theta = None
        self.XtX = None
        self.XtXi = None
        self.resid = []
        self.stages = stages
        self.n_used = len(stages)

        if len(stages) < p + 1:
            self.reason = f"阶段数 {len(stages)} ≤ 参数数 {p}，自由度不足，无法估计残差"
            self.phi = raw
            self.y = [s["T"] for s in stages]
            self.mu = [0.0] * (p - 1)
            self.sd = [1.0] * (p - 1)
            return

        self.mu = [mean([r[i] for r in raw]) for i in range(p - 1)]
        self.sd = []
        for i in range(p - 1):
            sd = stdev([r[i] for r in raw])
            self.sd.append(sd if sd > 1e-9 else 1.0)

        self.phi = [[1.0] + [(r[i] - self.mu[i]) / self.sd[i] for i in range(p - 1)] for r in raw]
        self.y = [s["T"] for s in stages]
        self.n_used = n = len(self.y)

        # XtX 与其逆
        self.XtX = [[sum(self.phi[k][i] * self.phi[k][j] for k in range(n)) for j in range(p)]
                    for i in range(p)]
        self.XtXi = m_inv(self.XtX)
        if self.XtXi is None:
            self.reason = "设计矩阵奇异（存在完全共线的项），无法估计"
            return

        # OLS
        Xty = [sum(self.phi[k][i] * self.y[k] for k in range(n)) for i in range(p)]
        self.theta = [sum(self.XtXi[i][j] * Xty[j] for j in range(p)) for i in range(p)]
        resid = [self.y[k] - sum(self.phi[k][i] * self.theta[i] for i in range(p))
                 for k in range(n)]
        self.dof = n - p
        self.rss = sum(r * r for r in resid)
        self.sigma = math.sqrt(self.rss / self.dof) if self.dof > 0 else float("nan")
        self.cond = cond_number(self.XtX)
        self.resid = resid
        self.ok = True

    def _raw(self, s):
        p = s["P"]
        f = s["fan"] / 100.0 if self.use_fan else 0.0
        return [t(p, f) for _, t in self.terms]

    def phi_of(self, P, fan01):
        v = [t(P, fan01) for _, t in self.terms]
        return [1.0] + [(v[i] - self.mu[i]) / self.sd[i] for i in range(len(v))]

    def predict(self, P, fan01):
        ph = self.phi_of(P, fan01)
        return sum(ph[i] * self.theta[i] for i in range(len(ph)))

    def leverage(self, ph, XtXi=None):
        A = XtXi if XtXi is not None else self.XtXi
        tmp = [sum(ph[j] * A[j][i] for j in range(len(ph))) for i in range(len(ph))]
        return sum(ph[i] * tmp[i] for i in range(len(ph)))

    def interval(self, P, fan01, dof=None, sigma=None, XtXi=None):
        """95% **预测区间**半宽（不是置信区间——用户问的是"下一次实测会不会超"）。"""
        dof = self.dof if dof is None else dof
        sigma = self.sigma if sigma is None else sigma
        if not self.ok or dof <= 0 or not (sigma > 0):
            return None
        ph = self.phi_of(P, fan01)
        h = self.leverage(ph, XtXi)
        h = max(h, 0.0)
        t = t_ppf(1 - (1 - self.conf) / 2, dof)
        return t * sigma * math.sqrt(1.0 + h)

    def p_exceed(self, P, fan01, Tstar):
        """P(实测稳态温度 > Tstar)，按 t 分布预测分布算。"""
        if not self.ok or self.dof <= 0 or not (self.sigma > 0):
            return float("nan")
        ph = self.phi_of(P, fan01)
        h = max(self.leverage(ph), 0.0)
        s = self.sigma * math.sqrt(1.0 + h)
        if s < 1e-9:
            return 1.0 if self.predict(P, fan01) > Tstar else 0.0
        t = (Tstar - self.predict(P, fan01)) / s
        return 1.0 - t_cdf(t, self.dof)

    # ---- Sherman–Morrison：加入一个候选点后的 (ΦᵀΦ)⁻¹
    def with_point(self, ph, XtXi=None):
        A = XtXi if XtXi is not None else self.XtXi
        u = ph
        Au = [sum(A[i][j] * u[j] for j in range(len(u))) for i in range(len(u))]
        denom = 1.0 + sum(u[i] * Au[i] for i in range(len(u)))
        if abs(denom) < 1e-12:
            return None
        return [[A[i][j] - Au[i] * Au[j] / denom for j in range(len(A))] for i in range(len(A))]


# =============================================================== 候选与评分
def make_candidates(power_map, use_fan, taken, given_fan=None):
    """生成候选设计点，剔除已经跑过的。"""
    out = []
    caps = list(range(30, 101, 5))
    fans = sorted({30, 40, 50, 60, 70, 80, 90, 100}) if use_fan else [given_fan or 0]
    for c in caps:
        P = power_map.at(c)
        if P is None:
            continue
        for f in fans:
            dup = any(abs(s["cap"] - c) < 4 and
                      (not use_fan or abs(s["fan"] - f) < 6) for s in taken)
            if dup:
                continue
            out.append({"cap": c, "fan": f, "P": P})
    return out


def plan_path(ident, power_map, targets, given_fan01, cost_s, cands,
              max_steps=6, min_rel=0.02):
    """贪心序贯规划：从当前数据出发连续推荐 N 档，给出累积效果与耗时。

    为什么需要它：单档收益（8%）和总余量（34%）并列显示会让人困惑。
    正确答案是「还差几档」——把 greedy 路径走一遍就知道：
    "再跑 4 档、约 11 分钟，能把 85℃ 处的区间从 ±6.4 收到 ±4.3"。

    注意每一步加入新点后，(ΦᵀΦ)⁻¹ 用 Sherman–Morrison 增量更新、自由度 +1，
    所以第 2 步的收益是在「第 1 步已跑」的前提下算的——这才是真实边际收益递减。
    """
    XtXi = ident.XtXi
    dof = ident.dof
    used = set()
    pts = []
    for T in targets:
        inv = _invert(ident, power_map, T, given_fan01)
        if inv:
            pts.append((T, inv[1]))
    if not pts:
        return []

    steps = []
    for _ in range(max_steps):
        cur = []
        for T, Pw in pts:
            hw = ident.interval(Pw, given_fan01, dof=dof, XtXi=XtXi)
            if hw:
                cur.append((T, Pw, hw))
        if not cur:
            break
        focus_T, focus_Pw, focus_hw = max(cur, key=lambda x: x[2])

        best = None
        for c in cands:
            key = (c["cap"], c["fan"])
            if key in used:
                continue
            ph = ident.phi_of(c["P"], c["fan"] / 100.0 if ident.use_fan else 0.0)
            X2 = ident.with_point(ph, XtXi)
            if X2 is None:
                continue
            hw2 = ident.interval(focus_Pw, given_fan01, dof=dof + 1, XtXi=X2)
            if hw2 is None:
                continue
            g = focus_hw - hw2
            if best is None or g > best["gain"]:
                best = {"cand": c, "X2": X2, "after": hw2, "gain": g}
        if best is None or best["gain"] <= 0:
            break
        rel = best["gain"] / focus_hw if focus_hw > 1e-9 else 0.0
        if rel < min_rel:
            break
        used.add((best["cand"]["cap"], best["cand"]["fan"]))
        XtXi, dof = best["X2"], dof + 1
        steps.append({"n": len(steps) + 1,
                      "cap": best["cand"]["cap"], "fan": best["cand"]["fan"],
                      "P": round(best["cand"]["P"], 1),
                      "before": round(focus_hw, 2), "after": round(best["after"], 2),
                      "gain": round(best["gain"], 2), "rel": round(rel, 3),
                      "focus_target": focus_T,
                      "cum_s": int(cost_s * (len(steps) + 1))})
    return steps


def _invert(ident, power_map, Tstar, fan01):
    """反解：满足「预测稳态 ≤ T*」的最大 cap（性能优先）。不可达返回 None。"""
    best = None
    for cap in range(30, 101, 5):
        P = power_map.at(cap)
        if P is None:
            continue
        if ident.predict(P, fan01) <= Tstar:
            best = (cap, P)
    return best


def score_candidate(ident, cand, targets, power_map, cost_s, given_fan01, conf):
    """该候选加入后，**实际使用工况**下最差目标点的预测区间能收窄多少（℃）／耗时。

    两个必须分开的概念（搞混会得出荒谬结论）：
      · 实验点 = 这一档施加的 (cap, fan)，它只负责**提供信息**；
      · 工况点 = 程序实际会跑到的 (cap, fan)，它才是我们关心精度的位置。
    所以评分是「在候选点采一条数据 → 看工况点的区间收窄多少」，
    而不是「看候选点自己的区间」——后者会让结论变成"多跑正好等于工况的点"，
    等于把实验设计退化成"重复采样"。

    另外焦点取**最差目标点**而非平均：安全相关的是瓶颈那一个。
    """
    ph = ident.phi_of(cand["P"], cand["fan"] / 100.0 if ident.use_fan else 0.0)
    XtXi2 = ident.with_point(ph)
    if XtXi2 is None:
        return None
    dof2 = ident.dof + 1

    detail = []
    for Tstar in targets:
        inv = _invert(ident, power_map, Tstar, given_fan01)
        if inv is None:
            continue
        capb, Pb = inv
        h1 = ident.interval(Pb, given_fan01)
        h2 = ident.interval(Pb, given_fan01, dof=dof2, XtXi=XtXi2)
        if h1 is None or h2 is None:
            continue
        detail.append({"target": Tstar, "cap": capb,
                       "before": round(h1, 2), "after": round(h2, 2),
                       "gain": round(h1 - h2, 2)})
    if not detail:
        return None

    focus = max(detail, key=lambda d: d["before"])
    worst_before = focus["before"]
    worst_after = focus["after"]
    rel = (worst_before - worst_after) / worst_before if worst_before > 1e-6 else 0.0
    return {"cap": cand["cap"], "fan": cand["fan"], "P": round(cand["P"], 1),
            "cost_s": cost_s,
            "worst_before": worst_before, "worst_after": worst_after,
            "worst_target": focus["target"],
            "gain": round(worst_before - worst_after, 2),
            "rel": round(rel, 3),
            "per100s": round((worst_before - worst_after) / max(1.0, cost_s) * 100, 3),
            "detail": detail}


# =============================================================== 主分析
def _fmt_set(cand, mode):
    if mode == "nbfc":
        return f"cap{cand['cap']}%/fan{cand['fan']}%"
    return f"cap{cand['cap']}%"


def analyze(csv_path, targets=(70, 75, 80, 85, 90), budget_s=None,
            min_gain=0.50, min_rel=0.20, conf=0.95):
    rows = load_rows(csv_path)
    if not rows:
        raise SystemExit(f"CSV 里没有有效数据：{csv_path}")
    stages_all = stage_features(rows)

    cpu_all = [s for s in stages_all if s["load"] == "cpu" and s["P"] > 0]
    cpu = [s for s in cpu_all if s["steady"]]
    unsteady = [s for s in cpu_all if not s["steady"]]
    if len(cpu) < 2:
        cpu = cpu_all
        unsteady = []

    if not cpu:
        raise SystemExit("没有满载阶段（load=cpu 且功耗>0），无法做热辨识。先跑 lab\\run-lab.cmd。")

    fan_varied = (all(s["fan"] > 0 for s in cpu)
                  and stdev([s["fan"] for s in cpu]) > 3)
    use_fan = bool(fan_varied)
    mode = "nbfc" if use_fan else "bios"

    # 选模型族：按自由度能支撑的最大复杂度。
    # 留余量（要求 n ≥ 参数数 + 2），否则 4 点拟 4 参数会得到 σ≈0.01 的**假精度**，
    # 进而让决策误判为"已经够准了，可以停"。
    n = len(cpu)
    p_span = max(s["P"] for s in cpu) - min(s["P"] for s in cpu)
    narrow_p = stdev([s["P"] for s in cpu]) / max(1e-6, mean([s["P"] for s in cpu])) < 0.15
    if use_fan:
        key = "P_fan" if n >= 5 else "P"      # P_fan 有 3 参数，至少要 5 个点
    else:
        key = "P2" if (n >= 6 and not narrow_p) else "P"
    ident = Ident(key, cpu, use_fan, conf=conf)

    power_map = PowerMap(cpu)
    given_fan = round(mean([s["fan"] for s in cpu])) if cpu else 50
    candidates = make_candidates(power_map, use_fan, cpu, given_fan=given_fan)

    # 每档耗时：优先从 plan 读，否则用实测 stage 平均时长
    cost_s = _stage_cost(csv_path, rows, stages_all)

    scored = []
    given_fan01 = given_fan / 100.0 if use_fan else 0.0
    if ident.ok and candidates:
        for c in candidates:
            r = score_candidate(ident, c, targets, power_map, cost_s,
                                given_fan01, conf)
            if r:
                scored.append(r)
        scored.sort(key=lambda d: -d["per100s"])

    # 序贯路线图：连续追加 N 档的累积效果（边际收益递减在这里体现）
    path = []
    if ident.ok and candidates and ident.dof >= 1:
        try:
            path = plan_path(ident, power_map, targets, given_fan01, cost_s, candidates)
        except Exception:
            path = []

    # 当前状态：在**实际使用工况**（平均风扇）下，各目标温度的反解与精度
    now = {}
    if ident.ok:
        for Tstar in targets:
            inv = _invert(ident, power_map, Tstar, given_fan01)
            if inv is None:
                preds = [ident.predict(power_map.at(c), given_fan01)
                         for c in range(30, 101, 5) if power_map.at(c)]
                now[Tstar] = {"reachable": False,
                              "pred_max": round(max(preds), 1) if preds else 0.0}
                continue
            capb, Pb = inv
            hw = ident.interval(Pb, given_fan01)
            pe = ident.p_exceed(Pb, given_fan01, Tstar)
            in_range = (min(s["cap"] for s in cpu) <= capb <= max(s["cap"] for s in cpu))
            h = max(ident.leverage(ident.phi_of(Pb, given_fan01)), 0.0) if ident.ok else None
            now[Tstar] = {
                "reachable": True, "cap": capb,
                "pred": round(ident.predict(Pb, given_fan01), 1),
                "halfwidth": round(hw, 2) if hw else None,
                "p_exceed": round(pe, 3) if pe == pe else None,
                "extrapolated": not in_range,
                "h": round(h, 4) if h is not None else None,
            }

    # 设计余量：当前区间里，「再加实验点能消掉的」占多少？
    #   区间 = t·σ·sqrt(1+h)。h 是设计带来的那部分（杠杆值），1 是单次观测噪声。
    #   h→0 时区间退到 z·σ —— 这是**无论怎么设计都降不下去的地板**（只能降噪）。
    #   这个比例小，说明瓶颈是噪声而不是设计，那就该去改稳态窗口/负载稳定性，
    #   而不是继续加档。很多"再做几组实验"的冲动，其实用这个数就能否掉。
    headroom = None
    h_worst = None
    worst_T = None
    if ident.ok and ident.dof > 0 and ident.sigma > 0:
        cands_h = [(v["halfwidth"], T, v.get("h"))
                   for T, v in now.items() if v.get("reachable") and v.get("halfwidth")]
        if cands_h:
            hw_max, T_w, h_w = max(cands_h, key=lambda x: x[0])
            if h_w is not None:
                floor = _norm_ppf(1 - (1 - conf) / 2) * ident.sigma
                headroom = max(0.0, (hw_max - floor) / hw_max)
                h_worst, worst_T = h_w, T_w

    # ---- 保守性检查：反解只保证「**预测值** ≤ 目标」，完全没管区间宽度。
    # 区间宽的时候，实际会以相当高的概率冲破目标——这才是用户真正会遇到的事。
    # 所以除了"性能 cap"（点估计刚好达标的最激进设置），还要给一个
    # "保守 cap"（留出不确定性裕量后，超温概率 ≤10% 的最激进设置）。
    # 两者的差 = **不确定性税**：模型越准，这段白白让出的性能就越少。
    risk = []
    for T, v in now.items():
        if v.get("reachable") and v.get("p_exceed") is not None:
            risk.append((v["p_exceed"], T, v["cap"]))
    max_pe, pe_T, pe_cap = max(risk) if risk else (0.0, None, None)

    if ident.ok and pe_T is not None:
        for T, v in now.items():
            if not v.get("reachable"):
                continue
            safe = None
            for c in range(30, 101, 5):
                P = power_map.at(c)
                if P is None:
                    continue
                pe = ident.p_exceed(P, given_fan01, T)
                if pe == pe and pe <= 0.10:
                    safe = (c, round(ident.predict(P, given_fan01), 1))
            v["safe_cap"] = safe[0] if safe else None
            v["safe_pred"] = safe[1] if safe else None

    # ---- 决策
    # 优先级（安全 > 精度 > 效率）：
    #   ① 模型不成立 / 自由度 <3 → 必须继续（此刻任何精度判断都可能是假精度）
    #   ② 有目标解落在已测 cap 之外 → 外推，必须继续
    #   ③ 单档收益同时过绝对与相对门槛 → 继续
    #   ④ 设计余量 <15% → 停：瓶颈是观测噪声，加点无效，要降噪
    #   ⑤ 超温概率仍 >25% → 不是继续跑，而是**收紧策略**（加档改善不了这点）
    #   ⑥ 其余 → 停
    best = scored[0] if scored else None
    if not ident.ok:
        decision = "continue"
        decision_reason = ident.reason + " → 必须继续采集"
    elif ident.dof < 3:
        decision = "continue"
        decision_reason = (f"残差自由度只有 {ident.dof}（<3），σ 本身不可靠，"
                           "此时任何精度判断都可能是假精度 → 必须继续")
    elif best is None:
        decision = "stop"
        decision_reason = "候选网格已全部覆盖，没有可再提供信息的设计点"
    elif any(v.get("extrapolated") for v in now.values() if v.get("reachable")):
        decision = "continue"
        decision_reason = ("有目标温度的解落在已测 cap 范围之外（外推区，不可信）；"
                           f"跑 {_fmt_set(best, mode)} 可把它变成内插")
    elif best["gain"] >= min_gain and best["rel"] >= min_rel and (headroom or 0) >= 0.15:
        decision = "continue"
        decision_reason = (f"最差目标({best['worst_target']}℃)区间能从 "
                           f"±{best['worst_before']:.2f}℃ 收到 ±{best['worst_after']:.2f}℃"
                           f"（−{best['rel'] * 100:.0f}%），值得跑")
    elif headroom is not None and headroom < 0.15:
        decision = "stop"
        decision_reason = (
            f"精度瓶颈是**观测噪声**（σ={ident.sigma:.2f}℃），不是实验设计："
            f"{worst_T}℃ 处区间 ±{now[worst_T]['halfwidth']:.2f}℃ 里只有 "
            f"{headroom * 100:.0f}% 是加点能消掉的，其余是 z·σ 的地板。"
            "再加档没用——要更准只能压噪声（加长稳态窗口、重复采样、稳住压测负载）")
    elif max_pe > 0.25 and now[pe_T].get("safe_cap"):
        decision = "tighten"
        tax = pe_cap - now[pe_T]["safe_cap"]
        decision_reason = (
            f"精度改善已经很有限，但 **{pe_T}℃ 目标目前不安全**："
            f"cap {pe_cap}% 的预测是 {now[pe_T]['pred']}℃，"
            f"区间 ±{now[pe_T]['halfwidth']:.2f}℃，超温概率 {max_pe * 100:.0f}%。"
            f"建议改为 cap {now[pe_T]['safe_cap']}%"
            f"（预测 {now[pe_T]['safe_pred']}℃，超温 ≤10%）"
            f"——这 {tax}% 的性能就是模型不确定性收的税。"
            "想把这税拿回来，得靠更准的模型（或压噪声），不是继续加档")
    else:
        decision = "stop"
        tail = ""
        if path:
            last = path[-1]
            tail = (f"；即使连续追加 {len(path)} 档（额外 {last['cum_s'] // 60} 分钟）"
                    f"也只到 ±{last['after']:.2f}℃")
            floor = _norm_ppf(1 - (1 - conf) / 2) * ident.sigma
            if last["after"] > floor * 1.08:
                tail += f"，而 z·σ 地板是 ±{floor:.2f}℃"
        decision_reason = (
            f"最佳候选只能把最差区间从 ±{best['worst_before']:.2f}℃ 收到 "
            f"±{best['worst_after']:.2f}℃（−{best['rel'] * 100:.0f}%，"
            f"绝对 {best['gain']:.2f}℃），低于继续采集的门槛 "
            f"(≥{min_gain:.1f}℃ 且 ≥{min_rel * 100:.0f}%){tail}——再跑不划算")

    return {
        "csv": os.path.abspath(csv_path),
        "mode": mode,
        "n_stages": len(stages_all),
        "n_cpu": len(cpu),
        "unsteady": [s["stage"] for s in unsteady],
        "model": {"key": key, "label": ident.label, "params": len(ident.names),
                  "dof": ident.dof, "sigma": round(ident.sigma, 3) if ident.ok else None,
                  "cond": round(ident.cond, 1) if ident.ok else None},
        "targets": list(targets),
        "now": {str(k): v for k, v in now.items()},
        "power_map": [{"cap": c, "P": round(p, 1), "amb": c in power_map.ambiguous}
                      for c, p in zip(power_map.caps, power_map.ps)],
        "stage_cost_s": cost_s,
        "candidates": scored[:8],
        "path": path,
        "decision": decision,
        "decision_reason": decision_reason,
        "min_gain": min_gain,
        "min_rel": min_rel,
        "dof_warning": ident.ok and ident.dof < 3,
        "headroom": round(headroom, 3) if headroom is not None else None,
        "h_worst": h_worst,
        "worst_target_point": worst_T,
        "max_p_exceed": round(max_pe, 3),
        "pe_target": pe_T,
    }


def _stage_cost(csv_path, rows, stages_all):
    """一档多久：先看有没有 plan 文件能对上，否则用实测阶段时长中位数。"""
    durs = {}
    by_stage = {}
    for r in rows:
        by_stage.setdefault(r["stage"], []).append(r["t"])
    for sid, ts in by_stage.items():
        if len(ts) >= 2:
            durs[sid] = max(ts) - min(ts)
    if durs:
        import statistics
        return round(statistics.median([v for v in durs.values() if v > 5]) if any(
            v > 5 for v in durs.values()) else statistics.median(list(durs.values())), 0)
    return 150.0


# =============================================================== 渲染
def _stars(per100s, best):
    if best <= 0:
        return "-"
    rel = per100s / best
    if rel > 0.85:
        return "★★★★"
    if rel > 0.6:
        return "★★★"
    if rel > 0.35:
        return "★★"
    if rel > 0.15:
        return "★"
    return "-"


def render_console(res):
    L = []
    m = res["model"]
    L.append("=" * 68)
    L.append(f" 可辨识性预判 · {res['mode'].upper()} 模式 · {os.path.basename(res['csv'])}")
    L.append("=" * 68)
    sig = m["sigma"]
    cond = m["cond"]
    if cond is None or cond == float("inf") or cond > 1e6:
        cnote = "—（模型未成立或已奇异）"
    elif cond < 30:
        cnote = f"{cond:.1f}（<30，良态，各项可辨识）"
    elif cond < 100:
        cnote = f"{cond:.1f}（30–100，轻度共线）"
    else:
        cnote = f"{cond:.1f}（>100，严重共线，部分项已不可辨识）"
    L.append(f" 满载阶段 {res['n_cpu']} 个（共 {res['n_stages']} 阶段）"
             + (f"，剔除未稳态：{', '.join(res['unsteady'])}" if res["unsteady"] else ""))
    L.append(f" 模型 {m['label']}  参数 {m['params']}  残差自由度 {m['dof']}"
             + (f"  残差 σ = {sig:.2f}℃" if sig is not None else "  σ 无法估计"))
    L.append(f" 设计矩阵条件数 {cnote}")
    if res.get("headroom") is not None:
        hr = res["headroom"]
        hw = res["now"][str(res["worst_target_point"])]["halfwidth"]
        note = "设计主导，加点有效" if hr >= 0.30 else (
            "设计/噪声各半" if hr >= 0.15 else "**噪声主导，加点几乎无效**")
        L.append(f" 设计余量 {hr * 100:.0f}%（{res['worst_target_point']}℃ 处 ±{hw:.2f}℃ 中"
                 f"可被实验设计消掉的比例，杠杆值 h={res['h_worst']:.2f}）—— {note}")
    if res.get("dof_warning"):
        L.append(f" ⚠ 自由度 {m['dof']}<3：σ 由极少数残差估出，区间宽度不可当真，"
                 "下面的收益数字只看趋势。")
    L.append("")

    if res["now"]:
        L.append(" 目标温度处的预测能力")
        L.append("   性能cap = 点估计刚好达标的最激进设置；保守cap = 留出不确定性裕量后超温≤10%")
        L.append(" ┌──────┬────────┬────────┬──────────────┬──────────┬──────────┐")
        L.append(" │ 目标 │ 性能cap│ 保守cap│ 预测稳态温度 │ 95%区间  │ 超温概率 │")
        L.append(" ├──────┼────────┼────────┼──────────────┼──────────┼──────────┤")
        for T in res["targets"]:
            v = res["now"].get(str(T))
            if not v:
                continue
            if not v.get("reachable"):
                L.append(f" │ {T:>4}℃│   —    │   —    │ 最高只到 {v['pred_max']:>4.1f}℃"
                         f" │    —     │    —     │")
                continue
            hw = f"±{v['halfwidth']:.2f}℃" if v["halfwidth"] is not None else "   ?    "
            pe = f"{v['p_exceed'] * 100:>5.1f}%" if v["p_exceed"] is not None else "   ?   "
            sc = f"{v['safe_cap']:>5}%" if v.get("safe_cap") else "   —  "
            flag = " ←外推" if v["extrapolated"] else ""
            L.append(f" │ {T:>4}℃│ {v['cap']:>5}% │ {sc} │   {v['pred']:>6.1f}℃  "
                     f"│  {hw:<7} │  {pe}  │{flag}")
        L.append(" └──────┴────────┴────────┴──────────────┴──────────┴──────────┘")
        taxed = [(T, v) for T, v in res["now"].items()
                 if v.get("safe_cap") and v.get("cap") and v["safe_cap"] < v["cap"]]
        if taxed:
            worst_tax = max(taxed, key=lambda x: x[1]["cap"] - x[1]["safe_cap"])
            Tt, vt = worst_tax
            L.append(f" 不确定性税：{Tt}℃ 目标要让出 {vt['cap'] - vt['safe_cap']}% 的 cap"
                     f"（{vt['cap']}% → {vt['safe_cap']}%），纯粹是模型不够准造成的性能损失。")
        ex = [f"{k}℃" for k, v in res["now"].items() if v.get("extrapolated")]
        if ex:
            L.append(f" ⚠ {'/'.join(ex)} 的目标解落在已测 cap 区间之外 —— 属外推，不可信。")
        L.append("")

    if res["power_map"]:
        amb = [d for d in res["power_map"] if d["amb"]]
        if amb:
            L.append(" ⚠ 同一 cap 对应差异 >20% 的功耗（Turbo 状态未固定）："
                     + ", ".join(f"cap{d['cap']}%≈{d['P']}W" for d in amb))
            L.append("   请在实验里固定 boost 设置，否则 cap→功耗 映射不可靠。")
            L.append("")

    cands = res["candidates"]
    if cands:
        best = cands[0]["per100s"]
        L.append(f" 下一档推荐（每档约 {res['stage_cost_s']:.0f} 秒，按性价比排序）")
        L.append(" 收益 = 最差目标点的 95% 预测区间收窄量（瓶颈，不是平均值）")
        L.append(" ┌────┬───────────┬────────────┬──────────────────┬────────┐")
        L.append(" │ 序 │ 设定      │ 稳态功耗   │ 最差区间收窄     │ 性价比 │")
        L.append(" ├────┼───────────┼────────────┼──────────────────┼────────┤")
        for i, c in enumerate(cands[:5], 1):
            if res["mode"] == "nbfc":
                setx = f"cap{c['cap']:>3}% f{c['fan']:>3}%"
            else:
                setx = f"cap{c['cap']:>3}%      "
            L.append(f" │ {i:>2} │ {setx} │  {c['P']:>5.1f} W  │ "
                     f"±{c['worst_before']:.1f}→±{c['worst_after']:.1f}℃ "
                     f"({c['rel'] * 100:>3.0f}%) │ {_stars(c['per100s'], best):<6} │")
        L.append(" └────┴───────────┴────────────┴──────────────────┴────────┘")
        top = cands[0]
        L.append(f" 首选 {_fmt_set(top, res['mode'])}："
                 f"瓶颈在 {top['worst_target']}℃ 目标，区间 "
                 f"±{top['worst_before']:.2f} → ±{top['worst_after']:.2f}℃")
        L.append("")

    path = res.get("path") or []
    if len(path) >= 2:
        L.append(f" 序贯路线图（连续追加，边际收益递减；瓶颈点 "
                 f"{path[0]['focus_target']}℃）")
        L.append(" ┌────┬───────────┬────────────┬──────────────────┬────────┐")
        L.append(" │ 步 │ 设定      │ 累计耗时   │ 瓶颈区间          │ 相对   │")
        L.append(" ├────┼───────────┼────────────┼──────────────────┼────────┤")
        for st in path:
            if res["mode"] == "nbfc":
                setx = f"cap{st['cap']:>3}% f{st['fan']:>3}%"
            else:
                setx = f"cap{st['cap']:>3}%      "
            mm = f"{st['cum_s'] // 60}m{st['cum_s'] % 60:02d}s"
            L.append(f" │ {st['n']:>2} │ {setx} │  {mm:>7}   │ "
                     f"±{st['before']:.2f}→±{st['after']:.2f}℃ "
                     f"({st['rel'] * 100:>3.0f}%) │")
        L.append(" └────┴───────────┴────────────┴──────────────────┴────────┘")
        L.append("")

    tag = {"continue": "▶ 继续跑", "stop": "■ 可以停",
           "tighten": "▼ 收紧策略"}[res["decision"]]
    L.append(f" 建议：{tag}")
    L.append(f"       {res['decision_reason']}")
    L.append("=" * 68)
    return "\n".join(L)


def render_svg(res):
    """预测区间带 + 已测点：一眼看出「哪段区间是外推」。"""
    W, H = 660, 300
    ml, mr, mt, mb = 56, 16, 22, 40
    pw, ph = W - ml - mr, H - mt - mb
    pm = res["power_map"]
    if not pm:
        return ""
    caps = [d["cap"] for d in pm]
    # 两侧留白，否则 cap=30/100 的数据点和标签会贴边被裁掉
    cmin, cmax = 24, 110
    tlo = min([v["pred"] - (v["halfwidth"] or 0) for v in res["now"].values()
               if v.get("reachable") and v.get("halfwidth")] or [60]) - 2
    thi = max([v["pred"] + (v["halfwidth"] or 0) for v in res["now"].values()
               if v.get("reachable") and v.get("halfwidth")] or [90]) + 2

    def X(c):
        return ml + (c - cmin) / (cmax - cmin) * pw

    def Y(t):
        return mt + ph - (t - tlo) / max(1e-6, thi - tlo) * ph

    s = [f'<svg viewBox="0 0 {W} {H}" xmlns="http://www.w3.org/2000/svg" '
         f'style="font-family:-apple-system,Segoe UI,Microsoft YaHei,sans-serif;width:100%;height:auto">',
         f'<rect x="0" y="0" width="{W}" height="{H}" fill="#0f1420" rx="10"/>']
    # 网格
    for t in range(int(tlo // 5 * 5), int(thi) + 5, 5):
        if tlo <= t <= thi:
            s.append(f'<line x1="{ml}" y1="{Y(t):.1f}" x2="{ml + pw}" y2="{Y(t):.1f}" '
                     f'stroke="#232b3d" stroke-width="1"/>')
            s.append(f'<text x="{ml - 8}" y="{Y(t) + 4:.1f}" fill="#8b95a8" font-size="11" '
                     f'text-anchor="end">{t}℃</text>')
    for c in range(30, 101, 10):
        s.append(f'<text x="{X(c):.1f}" y="{mt + ph + 18}" fill="#8b95a8" font-size="11" '
                 f'text-anchor="middle">{c}%</text>')

    # 误差棒：只有反解落点有区间，画成误差棒比画连续带更诚实
    s.append(f'<text x="{ml}" y="{mt - 6}" fill="#7b879c" font-size="11">'
             f'误差棒 = 该点 95% 预测区间（反解落点）</text>')
    # 多个目标可能反解到同一个 (cap, 预测值)——合并成一个点、标签并用，避免重叠
    grouped = {}
    for T, v in res["now"].items():
        if not (v.get("reachable") and v.get("halfwidth")):
            continue
        key = (v["cap"], v["pred"])
        if key not in grouped:
            grouped[key] = {"v": v, "Ts": []}
        grouped[key]["Ts"].append(int(float(T)))

    for (capv, predv), g in sorted(grouped.items()):
        v = g["v"]
        cx, cy = X(capv), Y(predv)
        hw = v["halfwidth"]
        col = "#e0a45c" if v.get("extrapolated") else "#4fb0c6"
        s.append(f'<line x1="{cx:.1f}" y1="{Y(predv - hw):.1f}" x2="{cx:.1f}" '
                 f'y2="{Y(predv + hw):.1f}" stroke="{col}" stroke-width="2"/>')
        for yy in (Y(predv - hw), Y(predv + hw)):
            s.append(f'<line x1="{cx - 6:.1f}" y1="{yy:.1f}" x2="{cx + 6:.1f}" '
                     f'y2="{yy:.1f}" stroke="{col}" stroke-width="2"/>')
        s.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="3.5" fill="{col}"/>')
        label = "≤" + "/".join(str(t) for t in g["Ts"]) + "℃"
        s.append(f'<text x="{cx:.1f}" y="{cy - 10:.1f}" fill="{col}" font-size="10.5" '
                 f'text-anchor="middle">{label}</text>')

    # 已测范围底纹
    if caps:
        s.append(f'<rect x="{X(min(caps)):.1f}" y="{mt}" width="{X(max(caps)) - X(min(caps)):.1f}" '
                 f'height="{ph}" fill="#4fb0c6" opacity="0.06"/>')
        s.append(f'<text x="{X(min(caps)) + 4:.1f}" y="{mt + ph - 8}" fill="#4a5568" '
                 f'font-size="10">已测范围</text>')

    s.append(f'<line x1="{ml}" y1="{mt + ph}" x2="{ml + pw}" y2="{mt + ph}" stroke="#33405c"/>')
    s.append(f'<line x1="{ml}" y1="{mt}" x2="{ml}" y2="{mt + ph}" stroke="#33405c"/>')
    s.append(f'<text x="{ml + pw / 2:.1f}" y="{H - 6}" fill="#8b95a8" font-size="11" '
             f'text-anchor="middle">处理器上限 cap (%)</text>')
    s.append("</svg>")
    return "\n".join(s)


def render_md(res):
    L = [f"# 可辨识性预判 · {res['mode'].upper()} 模式", ""]
    m = res["model"]
    L.append(f"- 数据：`{os.path.basename(res['csv'])}`，满载 {res['n_cpu']} 阶段"
             + (f"（剔除未稳态 {', '.join(res['unsteady'])}）" if res["unsteady"] else ""))
    L.append(f"- 模型：{m['label']}，参数 {m['params']}，自由度 {m['dof']}，"
             f"σ = {m['sigma']}℃，条件数 {m['cond']}")
    L.append("")
    L.append("## 目标温度处的预测能力")
    L.append("")
    L.append("| 目标 | 推荐 cap | 预测稳态 | 95% 预测区间 | 超温概率 | 备注 |")
    L.append("|---|---|---|---|---|---|")
    for T in res["targets"]:
        v = res["now"].get(str(T))
        if not v:
            continue
        if not v.get("reachable"):
            L.append(f"| {T}℃ | — | 最高 {v['pred_max']}℃ | — | — | 不可达 |")
            continue
        hw = f"±{v['halfwidth']:.2f}℃" if v["halfwidth"] is not None else "—"
        pe = f"{v['p_exceed'] * 100:.1f}%" if v["p_exceed"] is not None else "—"
        note = "**外推，不可信**" if v["extrapolated"] else "内插"
        L.append(f"| {T}℃ | {v['cap']}% | {v['pred']}℃ | {hw} | {pe} | {note} |")
    L.append("")
    if res["candidates"]:
        L.append("## 下一档推荐")
        L.append("")
        L.append(f"每档约 {res['stage_cost_s']:.0f} 秒。收益 = **最差目标点**的 95% 预测区间收窄量。")
        L.append("")
        L.append("| 序 | cap | fan | 稳态功耗 | 最差区间 | 收窄 | 相对 | 每 100s |")
        L.append("|---|---|---|---|---|---|---|---|")
        for i, c in enumerate(res["candidates"][:6], 1):
            L.append(f"| {i} | {c['cap']}% | {c['fan']}% | {c['P']} W | "
                     f"±{c['worst_before']:.2f}℃ ({c['worst_target']}℃目标) | "
                     f"±{c['worst_after']:.2f}℃ | −{c['rel'] * 100:.0f}% | "
                     f"{c['per100s']:.2f}℃ |")
        L.append("")
    L.append(f"## 决策：{'继续采集' if res['decision'] == 'continue' else '可以停止'}")
    L.append("")
    L.append(res["decision_reason"])
    return "\n".join(L)


def render_html(res, out_path):
    svg = render_svg(res)
    rows_now = []
    for T in res["targets"]:
        v = res["now"].get(str(T))
        if not v:
            continue
        if not v.get("reachable"):
            rows_now.append(f"<tr><td>{T}℃</td><td colspan=5 class='dim'>不可达（最高 {v['pred_max']}℃）</td></tr>")
            continue
        hw = f"±{v['halfwidth']:.2f}℃" if v["halfwidth"] is not None else "—"
        pe = f"{v['p_exceed']*100:.1f}%" if v["p_exceed"] is not None else "—"
        cls = "warn" if v.get("extrapolated") else ""
        note = "<span class='warn'>外推·不可信</span>" if v.get("extrapolated") else "内插"
        sc = f"{v['safe_cap']}%" if v.get("safe_cap") else "—"
        tax = ""
        if v.get("safe_cap") and v["safe_cap"] < v["cap"]:
            tax = f" <span class='warn'>−{v['cap'] - v['safe_cap']}%</span>"
        rows_now.append(f"<tr class='{cls}'><td>{T}℃</td><td>{v['cap']}%</td>"
                        f"<td>{sc}{tax}</td>"
                        f"<td>{v['pred']}℃</td><td>{hw}</td><td>{pe}</td></tr>")
    rows_c = []
    for i, c in enumerate(res["candidates"][:6], 1):
        rows_c.append(f"<tr><td>{i}</td><td>{c['cap']}%</td><td>{c['fan']}%</td>"
                      f"<td>{c['P']} W</td>"
                      f"<td>±{c['worst_before']:.2f}℃ <span class='dim'>({c['worst_target']}℃)</span></td>"
                      f"<td>±{c['worst_after']:.2f}℃</td><td>−{c['rel'] * 100:.0f}%</td>"
                      f"<td>{c['per100s']:.2f}</td></tr>")
    rows_p = []
    for st in (res.get("path") or []):
        rows_p.append(f"<tr><td>{st['n']}</td><td>{st['cap']}%</td><td>{st['fan']}%</td>"
                      f"<td>{st['cum_s'] // 60}m{st['cum_s'] % 60:02d}s</td>"
                      f"<td>±{st['before']:.2f} → ±{st['after']:.2f}℃</td>"
                      f"<td>{st['rel'] * 100:.0f}%</td></tr>")
    m = res["model"]
    dec = {"continue": "继续采集", "stop": "可以停止", "tighten": "先收紧策略"}[res["decision"]]
    dcls = {"continue": "go", "stop": "stop", "tighten": "tax"}[res["decision"]]
    html = f"""<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
<title>可辨识性预判 · {res['mode']}</title>
<style>
body{{margin:0;padding:28px 22px;background:#0a0e16;color:#d7dce6;
 font-family:-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;line-height:1.6}}
h1{{font-size:20px;margin:0 0 4px;font-weight:600}}
h2{{font-size:14px;margin:26px 0 10px;color:#9aa6bb;font-weight:600;
 letter-spacing:.4px;text-transform:uppercase}}
.sub{{color:#6b7688;font-size:12.5px;margin-bottom:18px}}
.wrap{{max-width:760px;margin:0 auto}}
table{{border-collapse:collapse;width:100%;font-size:13px;margin-top:8px}}
th{{text-align:left;color:#7b879c;font-weight:500;font-size:11.5px;
 padding:6px 8px;border-bottom:1px solid #232b3d;text-transform:uppercase;letter-spacing:.3px}}
td{{padding:7px 8px;border-bottom:1px solid #171d29}}
tr.warn td{{background:#2a1f14}}
.warn{{color:#e0a45c}} .dim{{color:#5d6879}}
.go{{color:#5fb87a}} .stop{{color:#c9736a}} .tax{{color:#e0a45c}}
.box{{border-left:3px solid #4fb0c6;padding:10px 14px;background:#111823;
 border-radius:0 6px 6px 0;margin-top:12px;font-size:13px}}
.box.stop{{border-color:#c9736a}} .box.go{{border-color:#5fb87a}} .box.tax{{border-color:#e0a45c}}
.meta{{display:flex;gap:26px;flex-wrap:wrap;font-size:12.5px;color:#8b95a8;
 margin:14px 0 0;padding:12px 16px;background:#111823;border-radius:8px}}
.meta b{{color:#d7dce6;font-weight:600}}
</style></head><body><div class="wrap">
<h1>可辨识性预判 · {res['mode'].upper()} 模式</h1>
<div class="sub">{os.path.basename(res['csv'])} · 满载 {res['n_cpu']} 阶段
{f"· 剔除未稳态 {', '.join(res['unsteady'])}" if res['unsteady'] else ''}</div>
<div class="meta">
<div>模型 <b>{m['label']}</b></div>
<div>自由度为 <b>{m['dof']}</b></div>
<div>残差 σ <b>{m['sigma']}℃</b></div>
<div>条件数 <b>{m['cond']}</b></div>
<div>每档耗时 <b>{res['stage_cost_s']:.0f}s</b></div>
</div>
<h2>误差地形</h2>
{svg}
<h2>目标温度处的预测能力</h2>
<p class=dim style="font-size:12px;margin:0 0 4px">
性能cap = 点估计刚好达标的最激进设置；保守cap = 留出不确定性裕量后超温≤10% 的设置；
两者之差（红色）就是「模型不确定性收的税」。</p>
<table><tr><th>目标</th><th>性能 cap</th><th>保守 cap</th><th>预测稳态</th>
<th>95% 预测区间</th><th>超温概率</th></tr>{''.join(rows_now)}</table>
<h2>下一档推荐</h2>
<table><tr><th>序</th><th>cap</th><th>fan</th><th>稳态功耗</th>
<th>最差区间</th><th>收窄到</th><th>相对</th><th>每 100s</th></tr>{''.join(rows_c) or
'<tr><td colspan=8 class=dim>没有候选点了</td></tr>'}</table>
<h2>序贯路线图</h2>
<table><tr><th>步</th><th>cap</th><th>fan</th><th>累计耗时</th>
<th>瓶颈区间</th><th>相对</th></tr>{''.join(rows_p) or
'<tr><td colspan=6 class=dim>无需追加</td></tr>'}</table>
<div class="box {dcls}"><b>{dec}</b>：{res['decision_reason']}</div>
<p class="dim" style="font-size:11.5px;margin-top:22px">
方法：Fisher 信息矩阵 + Sherman–Morrison 增量更新；区间用学生 t 分布（小样本自由度不可省）；
评分 = 目标温度集合上预测区间半宽的降低量 / 该档耗时。σ 假定不随新增点变化（乐观假设）。
</p>
</div></body></html>"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(html)
    return out_path


# =============================================================== CLI
def main():
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    path = args[0]
    targets = (70, 75, 80, 85, 90)
    min_gain, min_rel = 0.50, 0.20
    json_out = None
    i = 1
    while i < len(args):
        a = args[i]
        if a == "--targets" and i + 1 < len(args):
            targets = tuple(float(x) for x in args[i + 1].split(","))
            i += 2
        elif a == "--min-gain" and i + 1 < len(args):
            min_gain = float(args[i + 1])
            i += 2
        elif a == "--min-rel" and i + 1 < len(args):
            min_rel = float(args[i + 1])
            i += 2
        elif a == "--json" and i + 1 < len(args):
            json_out = args[i + 1]
            i += 2
        else:
            i += 1

    if os.path.isdir(path):
        csvs = sorted(glob.glob(os.path.join(path, "lab-*.csv")))
        if not csvs:
            print(f"目录下没有 lab-*.csv：{path}")
            return 3
        path = csvs[-1]

    res = analyze(path, targets=targets, min_gain=min_gain, min_rel=min_rel)
    print(render_console(res))

    out_dir = os.path.dirname(os.path.abspath(path))
    tag = os.path.basename(path).replace("lab-", "").replace(".csv", "")
    hp = render_html(res, os.path.join(out_dir, f"doe-{tag}.html"))
    mp = os.path.join(out_dir, f"doe-{tag}.md")
    with open(mp, "w", encoding="utf-8") as f:
        f.write(render_md(res))
    if json_out:
        with open(json_out, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        print("JSON:", json_out)
    print("HTML:", hp)

    return {"continue": 0, "stop": 2, "tighten": 4}.get(res["decision"], 0)


if __name__ == "__main__":
    sys.exit(main())
