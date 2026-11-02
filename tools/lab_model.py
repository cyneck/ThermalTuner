#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lab_model.py —— 数据挖掘 / 建模 / 策略导出

流程：
  1) 读 CSV（实验台产物）→ 分阶段稳态特征
  2) 特征工程：P、f、V、fan、T_prev、dP/dt、交互项，并处理共线性
  3) 稳态模型家族：均值 / 线性 / 二次 / **阶梯离散表** / 风扇线性(2D) / 阶梯网格(2D) / **MLP 神经网络**
     —— 全部用「按阶段留一交叉验证」(leave-one-stage-out) 评分，杜绝自相关泄漏
  4) 动态模型：一阶惯性（拟合 τ）/ ARX / MLP 动力学，用**整段轨迹仿真**验证
  5) 按 CV 误差选模型 → 反解出「目标温度 → cap / fan」策略（阶梯表）→ 导出 JSON
  6) 生成对比报告（MD + HTML）

用法：
    python tools/lab_model.py lab-data                     # 自动取最新 lab-*.csv
    python tools/lab_model.py lab-data/lab-doe1-xxx.csv    # 指定文件
"""

from __future__ import annotations

import csv
import glob
import json
import math
import os
import random
import sys
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lab_analyze import linfit, mean, stdev, quadfit  # noqa: E402


# =============================================================== 数据
def load_rows(path):
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for d in csv.DictReader(f):
            def g(k, dflt=0.0):
                v = d.get(k, "")
                try:
                    return float(v) if v not in ("", None) else dflt
                except ValueError:
                    return dflt

            t = g("cpu_temp_c")
            if t <= 0:
                continue
            rows.append({
                "t": g("elapsed_s"), "stage": (d.get("stage") or "").strip(),
                "load": (d.get("load") or "").strip().lower(),
                "T": t, "Tcore": g("cpu_core_max_c"), "V": g("cpu_volt_v"),
                "P": g("cpu_power_w"), "f": g("cpu_mhz"), "load_pct": g("cpu_load_pct"),
                "fan": g("fan_out_pct"), "rpm": g("fan_rpm"),
                "cap": g("proc_cap"), "boost": g("boost", -1),
            })
    rows.sort(key=lambda r: r["t"])
    return rows


def stage_features(rows, tail=0.30):
    """每个阶段取后 45% 窗口做稳态特征。"""
    stages = {}
    for r in rows:
        stages.setdefault(r["stage"], []).append(r)
    out = []
    for sid, rs in stages.items():
        n = max(8, int(len(rs) * tail)) if len(rs) >= 8 else len(rs)
        w = rs[-n:]
        Ts = [r["T"] for r in w]
        out.append({
            "stage": sid, "load": w[-1]["load"], "cap": w[-1]["cap"], "boost": w[-1]["boost"],
            "fan": mean([r["fan"] for r in w]),
            "T": mean(Ts), "T_std": stdev(Ts), "steady": stdev(Ts) <= 1.5,
            "P": mean([r["P"] for r in w]), "f": mean([r["f"] for r in w]),
            "V": mean([r["V"] for r in w]), "n": len(w),
            "T0": rs[0]["T"],
        })
    return out


# =============================================================== 特征工程
def features(s, use_fan):
    """把一行稳态特征变成模型输入。

    关键决策：**不把 P、f、V 同时丢进回归**——它们由 P≈C·V²·f 强约束，方差膨胀会让
    系数失去物理意义。这里把功耗 P 作为"热源强度"的唯一摘要特征，fan 作为"散热能力"特征。
    """
    x = [s["P"]]
    if use_fan:
        x.append(s["fan"] / 100.0)
    return x


FEATURE_NAMES_1D = ["功耗 P (W)"]
FEATURE_NAMES_2D = ["功耗 P (W)", "风扇占空比 fan"]


# =============================================================== 模型
class Model:
    name = "base"
    needs_fan = False

    def fit(self, data):
        raise NotImplementedError

    def predict(self, x):
        raise NotImplementedError

    def to_json(self):
        return {"type": self.name}


class FeatureModel(Model):
    """通用线性基函数模型：y = b0 + Σ b_k·φ_k(features)。

    关键工程点：求解前对每一列做 **z-score 标准化**。
    温控实验里 P² / P·fan 这类项的量级差几个数量级，不标准化会让正规方程病态，
    系数爆炸（曾经出现过 CV-RMSE 9 万 ℃ 的假结果）。标准化后求解、预测时再还原。
    """

    def __init__(self, name, terms, needs_fan=False):
        self.name = name
        self.terms = terms          # [(名称, lambda P, fan01 -> float)]
        self.needs_fan = needs_fan
        self.coef = None

    def _vec(self, x):
        p = x[0]
        f = x[1] if len(x) > 1 else 0.0
        return [t(p, f) for _, t in self.terms]

    def fit(self, data):
        rows = [self._vec(features(s, self.needs_fan)) + [s["T"]] for s in data]
        d = len(rows[0]) - 1
        if len(rows) < d + 1:
            self.coef = None
            self.fallback = mean([s["T"] for s in data])
            return self
        self.mu = [mean([r[i] for r in rows]) for i in range(d)]
        self.sd = []
        for i in range(d):
            sd = stdev([r[i] for r in rows])
            self.sd.append(sd if sd > 1e-9 else 1.0)
        norm = [[(r[i] - self.mu[i]) / self.sd[i] for i in range(d)] + [r[-1]] for r in rows]
        # 共线性守卫：两列几乎完全相关时（例如全部样本 P 相同），正规方程奇异，
        # 解出来的系数会把预测放大到几百摄氏度。宁可退化为均值，也不给假精度。
        collinear = False
        for i in range(d):
            for j in range(i + 1, d):
                xi = [r[i] for r in norm]
                xj = [r[j] for r in norm]
                si, sj = stdev(xi), stdev(xj)
                if si < 1e-9 or sj < 1e-9:
                    collinear = True
                    break
                r = mean([(a - mean(xi)) * (b - mean(xj)) for a, b in zip(xi, xj)]) / (si * sj)
                if abs(r) > 0.999:
                    collinear = True
                    break
            if collinear:
                break
        coef = None
        if not collinear:
            coef, _ = solve_constrained([[1.0] + n[:-1] + [n[-1]] for n in norm],
                                        [n for n, _ in self.terms])
        # 条件数守卫：标准化后系数仍离谱 → 退化为均值
        if coef is None or any(abs(c) > 200 for c in coef):
            self.coef = None
            self.fallback = mean([s["T"] for s in data])
            return self
        self.coef = coef
        return self

    def predict(self, x):
        if self.coef is None:
            return self.fallback
        z = [(v - self.mu[i]) / self.sd[i] for i, v in enumerate(self._vec(x))]
        return self.coef[0] + sum(self.coef[i + 1] * z[i] for i in range(len(z)))

    def to_json(self):
        if self.coef is None:
            return {"type": "linear_basis", "fallback": "mean", "value": round(self.fallback, 2)}
        return {"type": "linear_basis", "intercept": round(self.coef[0], 4),
                "mu": [round(v, 4) for v in self.mu], "sd": [round(v, 6) for v in self.sd],
                "terms": [{"name": n, "coef": round(self.coef[i + 1], 6)} for i, (n, _) in enumerate(self.terms)]}


# 基函数表：(名称, 需要的特征, 项)
TERM_SETS = {
    "const": ("均值", False, [("1", lambda p, f: 1.0)]),
    "P": ("T=a+b·P", False, [("P", lambda p, f: p)]),
    "P2": ("T=a+b·P+c·P²", False, [("P", lambda p, f: p), ("P²", lambda p, f: p * p)]),
    "P_fan": ("T=a+(b+c·fan)·P", True, [("P", lambda p, f: p), ("P·fan", lambda p, f: p * f)]),
    "P_fan2": ("T=a+(b+c·fan+d·fan²)·P", True,
               [("P", lambda p, f: p), ("P·fan", lambda p, f: p * f), ("P·fan²", lambda p, f: p * f * f)]),
    "fan": ("T=a+b·fan（P 变化小，只用风扇项）", True, [("fan", lambda p, f: f)]),
    "fan2": ("T=a+b·fan+c·fan²", True,
             [("fan", lambda p, f: f), ("fan²", lambda p, f: f * f)]),
}


def make_feature_model(key):
    name, needs_fan, terms = TERM_SETS[key]
    return FeatureModel(name, terms, needs_fan)


class StepModel(Model):
    """阶梯离散表：把 P 分箱，每箱取均值温度，并强制单调不减。

    这是"阶梯式离散数列"的直接实现，也是最适合塞进固件/策略表的形式：
    查表 + 比较，无浮点乘除。
    """
    name = "阶梯表（分箱查表）"

    def __init__(self, bins=4):
        self.k = bins

    def fit(self, data):
        ds = sorted(data, key=lambda s: s["P"])
        k = max(2, min(self.k, len(ds) // 2))
        self.k = k
        groups = [ds[i * len(ds) // k:(i + 1) * len(ds) // k] for i in range(k)]
        groups = [g for g in groups if g]
        table = []
        for g in groups:
            table.append([min(s["P"] for s in g), max(s["P"] for s in g), mean([s["T"] for s in g])])
        for i in range(1, len(table)):
            table[i][2] = max(table[i][2], table[i - 1][2])
        table[0][0] = 0.0
        table[-1][1] = 1e9
        for i in range(len(table) - 1):
            table[i][1] = table[i + 1][0] = (table[i][1] + table[i + 1][0]) / 2
        table[-1][1] = 1e9
        self.table = table
        return self

    def predict(self, x):
        p = x[0]
        for lo, hi, t in self.table:
            if lo <= p < hi:
                return t
        return self.table[-1][2]

    def to_json(self):
        return {"type": "stepwise",
                "bins": [[round(lo, 2), (None if hi > 1e8 else round(hi, 2)), round(t, 2)]
                         for lo, hi, t in self.table]}


class StepGridModel(Model):
    """二维阶梯网格：(功耗箱, 风扇箱) → 温度。可直接烧进固件做查表控制器。"""
    name = "二维阶梯网格"
    needs_fan = True

    def __init__(self):
        self.grid = None

    def fit(self, data):
        p_edges = _edges([s["P"] for s in data])
        f_edges = _edges([s["fan"] for s in data])
        if not p_edges or not f_edges:
            self.grid = None
            self.fallback = mean([s["T"] for s in data])
            return self
        grid = [[None] * (len(f_edges) + 1) for _ in range(len(p_edges) + 1)]
        acc = {}
        for s in data:
            i = _bin(p_edges, s["P"])
            j = _bin(f_edges, s["fan"])
            acc.setdefault((i, j), []).append(s["T"])
        for (i, j), v in acc.items():
            grid[i][j] = mean(v)
        for i in range(len(grid)):                      # 同一功耗下，按最近已知风扇格补
            known = [(j, grid[i][j]) for j in range(len(grid[i])) if grid[i][j] is not None]
            if known:
                for j in range(len(grid[i])):
                    if grid[i][j] is None:
                        grid[i][j] = min(known, key=lambda kv: abs(kv[0] - j))[1]
        for j in range(len(grid[0])):                   # 再按功耗方向补
            col = [(i, grid[i][j]) for i in range(len(grid)) if grid[i][j] is not None]
            for i in range(len(grid)):
                if grid[i][j] is None and col:
                    grid[i][j] = min(col, key=lambda kv: abs(kv[0] - i))[1]
        self.p_edges, self.f_edges, self.grid = p_edges, f_edges, grid
        return self

    def predict(self, x):
        if self.grid is None:
            return self.fallback
        i = _bin(self.p_edges, x[0])
        j = _bin(self.f_edges, x[1] * 100)
        v = self.grid[i][j]
        return v if v is not None else mean([c for row in self.grid for c in row if c is not None])

    def to_json(self):
        if self.grid is None:
            return {"type": "step_grid", "fallback": "mean"}
        return {"type": "step_grid",
                "p_edges": [round(e, 1) for e in self.p_edges],
                "f_edges": [round(e, 1) for e in self.f_edges],
                "grid": [[None if c is None else round(c, 2) for c in row] for row in self.grid]}


class MLP(Model):
    """小型多层感知机（纯标准库实现）：1 隐藏层 tanh，输出线性，L2 正则 + 动量。"""
    name = "神经网络 MLP(1 隐藏层)"
    needs_fan = False

    def __init__(self, hidden=6, epochs=2500, lr=0.06, l2=2e-3, seed=20261028):
        self.hidden, self.epochs, self.lr, self.l2, self.seed = hidden, epochs, lr, l2, seed

    def _x(self, x):
        return [(x[i] - self.mu[i]) / self.sd[i] for i in range(len(x))]

    def fit(self, data):
        X = [features(s, self.needs_fan) for s in data]
        Y = [s["T"] for s in data]
        d = len(X[0])
        self.mu = [mean([x[i] for x in X]) for i in range(d)]
        self.sd = [max(1e-6, stdev([x[i] for x in X])) for i in range(d)]
        self.ymu = mean(Y)
        rnd = random.Random(self.seed)
        H = self.hidden
        W1 = [[rnd.uniform(-0.5, 0.5) for _ in range(d)] for _ in range(H)]
        b1 = [0.0] * H
        W2 = [rnd.uniform(-0.5, 0.5) for _ in range(H)]
        b2 = 0.0
        vW1 = [[0.0] * d for _ in range(H)]
        vb1 = [0.0] * H
        vW2 = [0.0] * H
        vb2 = 0.0
        mom = 0.9
        n = len(X)
        for _ in range(self.epochs):
            gW1 = [[0.0] * d for _ in range(H)]
            gb1 = [0.0] * H
            gW2 = [0.0] * H
            gb2 = 0.0
            for x, y in zip(X, Y):
                z = self._x(x)
                h = [math.tanh(b1[k] + sum(W1[k][i] * z[i] for i in range(d))) for k in range(H)]
                o = b2 + sum(W2[k] * h[k] for k in range(H))
                e = o - (y - self.ymu)          # 残差（预测均值中心化）
                for k in range(H):
                    g = e * h[k]
                    gW2[k] += g
                    gb2 += e
                dh = [e * W2[k] * (1 - h[k] * h[k]) for k in range(H)]
                for k in range(H):
                    for i in range(d):
                        gW1[k][i] += dh[k] * z[i]
                    gb1[k] += dh[k]
            for k in range(H):
                for i in range(d):
                    gr = gW1[k][i] / n + self.l2 * W1[k][i]
                    vW1[k][i] = mom * vW1[k][i] - self.lr * gr
                    W1[k][i] += vW1[k][i]
                gb1[k] /= n
                vb1[k] = mom * vb1[k] - self.lr * gb1[k]
                b1[k] += vb1[k]
                gr = gW2[k] / n + self.l2 * W2[k]
                vW2[k] = mom * vW2[k] - self.lr * gr
                W2[k] += vW2[k]
            gb2 /= n
            vb2 = mom * vb2 - self.lr * gb2
            b2 += vb2
        self.W1, self.b1, self.W2, self.b2 = W1, b1, W2, b2
        return self

    def predict(self, x):
        z = self._x(x)
        h = [math.tanh(self.b1[k] + sum(self.W1[k][i] * z[i] for i in range(len(z))))
             for k in range(self.hidden)]
        return self.ymu + self.b2 + sum(self.W2[k] * h[k] for k in range(self.hidden))

    def to_json(self):
        return {"type": "mlp", "hidden": self.hidden,
                "mu": [round(v, 4) for v in self.mu], "sd": [round(v, 6) for v in self.sd],
                "ymu": round(self.ymu, 4), "b2": round(self.b2, 6),
                "W1": [[round(v, 6) for v in row] for row in self.W1], "b1": [round(v, 6) for v in self.b1],
                "W2": [round(v, 6) for v in self.W2]}


class MLP2D(MLP):
    name = "神经网络 MLP2D(P, fan)"
    needs_fan = True


# =============================================================== 工具
def _edges(vals):
    s = sorted(vals)
    n = len(s)
    k = max(2, min(3, n // 2))
    return [s[i * n // k] for i in range(1, k)]


def _bin(edges, v):
    i = 0
    while i < len(edges) and v >= edges[i]:
        i += 1
    return i


# 物理符号约束：功耗项必须为正（功耗↑→温度↑），风扇项必须为负（风量↑→温度↓）。
# 纯数据拟合会在样本少时学到"风扇越大越热"这种反物理系数，反解就会推荐"降低风扇"。
SIGN_RULES = {
    "P": +1,
    "P·fan": -1,
    "P·fan²": -1,
    "fan": -1,
    "fan²": -1,
    "1": 0,
    "P²": 0,
}


def solve_constrained(rows, names):
    """带符号约束的最小二乘：先解无约束，违反符号的项剔除后重解（小规模 NNLS）。"""
    signs = [SIGN_RULES.get(n, 0) for n in names]
    active = list(range(len(names)))
    while active:
        sub = [[1.0] + [r[i + 1] for i in active] + [r[-1]] for r in rows]
        coef = solve_normal(sub)
        if coef is None:
            return None, []
        bad = [k for k, i in enumerate(active)
               if (signs[i] > 0 and coef[k + 1] < 0) or (signs[i] < 0 and coef[k + 1] > 0)]
        if not bad:
            full = [coef[0]] + [0.0] * len(names)
            for k, i in enumerate(active):
                full[i + 1] = coef[k + 1]
            return full, active
        active = [i for k, i in enumerate(active) if k not in bad]
    return [mean([r[-1] for r in rows])] + [0.0] * len(names), []


def solve_normal(rows):
    """最小二乘（正规方程 + 高斯消元），rows = [(x0..xd, y)]。"""
    d = len(rows[0]) - 1
    A = [[0.0] * d for _ in range(d)]
    b = [0.0] * d
    for r in rows:
        for i in range(d):
            b[i] += r[i] * r[-1]
            for j in range(d):
                A[i][j] += r[i] * r[j]
    for i in range(d):
        p = max(range(i, d), key=lambda r: abs(A[r][i]))
        if abs(A[p][i]) < 1e-12:
            return None
        A[i], A[p] = A[p], A[i]
        b[i], b[p] = b[p], b[i]
        for r in range(i + 1, d):
            f = A[r][i] / A[i][i]
            for c in range(i, d):
                A[r][c] -= f * A[i][c]
            b[r] -= f * b[i]
    x = [0.0] * d
    for i in range(d - 1, -1, -1):
        x[i] = (b[i] - sum(A[i][j] * x[j] for j in range(i + 1, d))) / A[i][i]
    return x


# =============================================================== 交叉验证
def loo_cv(factory, data, use_fan):
    """按阶段留一交叉验证：每次留出一个阶段，用其余训练，预测留出的那个。

    必须按**阶段**分组而不是按样本随机划分——同一阶段的样本高度自相关，
    随机划分会让模型"见过答案"，误差虚低（数据挖掘里最典型的泄漏）。
    """
    errs, preds = [], []
    for k in range(len(data)):
        train = data[:k] + data[k + 1:]
        test = data[k]
        try:
            m = factory().fit(train)
            p = m.predict(features(test, use_fan))
        except Exception:
            p = mean([s["T"] for s in train])
        errs.append(p - test["T"])
        preds.append({"stage": test["stage"], "actual": round(test["T"], 2),
                      "pred": round(p, 2), "err": round(p - test["T"], 2)})
    rmse = math.sqrt(mean([e * e for e in errs]))
    mae = mean([abs(e) for e in errs])
    ss_res = sum(e * e for e in errs)
    my = mean([s["T"] for s in data])
    ss_tot = sum((s["T"] - my) ** 2 for s in data)
    r2 = 1 - ss_res / ss_tot if ss_tot > 1e-9 else 0.0
    return {"rmse": rmse, "mae": mae, "r2": max(-1.0, r2), "preds": preds}


# =============================================================== 反解控制律
# =============================================================== 主流程
def run(csv_path):
    rows = load_rows(csv_path)
    stages = stage_features(rows)
    cpu_all = [s for s in stages if s["load"] == "cpu" and s["P"] > 0]
    cpu = [s for s in cpu_all if s["steady"]]
    dropped = [s["stage"] for s in cpu_all if not s["steady"]]
    if len(cpu) < 4:                       # 稳态点不够时退回全部，但要在报告里说明
        cpu, dropped = cpu_all, []
    if len(cpu) < 4:
        raise SystemExit(f"可用满载阶段只有 {len(cpu)} 个，样本不足（至少要 4 个）。")

    fan_varied = stdev([s["fan"] for s in cpu]) > 3 if all(s["fan"] > 0 for s in cpu) else False
    use_fan = bool(fan_varied)
    mode = "nbfc" if use_fan else "bios"

    # ---- 模型家族 + 留一交叉验证
    narrow_p = stdev([s["P"] for s in cpu]) / max(1e-6, mean([s["P"] for s in cpu])) < 0.15
    family = [("均值基线", lambda: make_feature_model("const"), False),
              ("线性 T=a+b·P", lambda: make_feature_model("P"), False),
              ("二次 T=a+b·P+c·P²", lambda: make_feature_model("P2"), False),
              ("阶梯表（4 箱）", lambda: StepModel(4), False),
              ("神经网络 MLP(h=6)", lambda: MLP(hidden=6), False)]
    if use_fan:
        family += [("风扇线性 T=a+(b+c·fan)·P", lambda: make_feature_model("P_fan"), True),
                   ("风扇二次 T=a+(b+c·fan+d·fan²)·P", lambda: make_feature_model("P_fan2"), True),
                   ("风扇专用 T=a+b·fan+c·fan²", lambda: make_feature_model("fan2"), True),
                   ("二维阶梯网格", StepGridModel, True),
                   ("神经网络 MLP2D(h=8)", lambda: MLP2D(hidden=8), True)]
        if narrow_p:
            family.append(("风扇一次 T=a+b·fan", lambda: make_feature_model("fan"), True))

    def build(factory, nf):
        m = factory()
        m.needs_fan = nf
        return m

    results = []
    for name, factory, nf in family:
        cv = loo_cv(lambda f=factory, n=nf: build(f, n), cpu, use_fan)
        full = build(factory, nf).fit(cpu)
        results.append({"name": name, "cv": cv, "model": full})

    results.sort(key=lambda r: r["cv"]["rmse"])
    best = results[0]
    # 反解用"连续模型"：阶梯表虽然可能 CV 最好，但它是分段常数，
    # 反解时会出现半格跳变（5% cap 换来 17℃ 的假落差）。阶梯表另存为固件查表用。
    continuous = [r for r in results if not r["name"].startswith("阶梯") and not r["name"].startswith("二维阶梯")]
    invert_model = continuous[0] if continuous else best
    discrete = next((r for r in results if r["name"].startswith("阶梯")), None)

    # ---- 动态：一阶惯性 τ（用整段轨迹仿真误差定，避免一步预测把 τ 系统性地拉小）
    dt = _median_dt(rows)
    tau, alpha = _fit_tau(rows, best["model"], use_fan)
    dyn = {"tau_s": round(tau, 1), "alpha": round(alpha, 4), "dt": dt,
           "arx": _fit_arx(rows)}

    # ---- cap → 功耗：用**实测点分段插值**，不要用一条直线去拟合
    #     cap=100% 在 Turbo 开/关下功耗差一倍（本机 40W vs 29W），
    #     用一个 cap→P 的线性函数会把两个物理状态混成一个，反解必然错。
    turbo_on = [s for s in cpu if s["boost"] != 0] or cpu
    turbo_off = [s for s in cpu if s["boost"] == 0]
    pairs = sorted({(s["cap"], s["P"]) for s in turbo_on})
    caps_on = [p0 for p0, _ in pairs]
    ps_on = [p1 for _, p1 in pairs]
    for i in range(1, len(ps_on)):
        ps_on[i] = max(ps_on[i], ps_on[i - 1] + 1e-3)

    def p_of_cap(cap):
        """Turbo 开启时，cap% 对应的实测功耗（分段线性 + 两端幂律外推）。"""
        if not caps_on:
            return None
        if cap <= caps_on[0]:
            return max(1.0, ps_on[0] * (cap / max(caps_on[0], 1)) ** 1.3)
        if cap >= caps_on[-1]:
            return ps_on[-1] * (cap / caps_on[-1]) ** 1.6
        for i in range(1, len(caps_on)):
            if cap <= caps_on[i]:
                r = (cap - caps_on[i - 1]) / max(1e-6, caps_on[i] - caps_on[i - 1])
                return ps_on[i - 1] + r * (ps_on[i] - ps_on[i - 1])
        return ps_on[-1]

    f_lo = min(s["f"] for s in cpu)
    f_hi = max(s["f"] for s in cpu)
    f_hi_nt = max([s["f"] for s in turbo_off], default=None)

    def f_of_cap(cap, turbo=True):
        top = f_hi if turbo else (f_hi_nt if f_hi_nt else f_hi * 0.82)
        return top if cap >= 99 else f_lo + (cap / 100.0) * (top - f_lo)

    # ---- 反解：候选集合里挑"满足目标温度、且性能损失最小"的那一档
    #     性能用频率 f 衡量（比 cap 更有物理意义：cap 100% + Turbo 关 vs cap 85% + Turbo 开）
    cands = []
    for s in cpu:
        cands.append({"cap": s["cap"], "boost": s["boost"], "f": s["f"], "P": s["P"], "src": "实测"})
    for cap in range(5, 101, 5):
        pc = p_of_cap(cap)
        if pc:
            cands.append({"cap": cap, "boost": 2, "f": f_of_cap(cap, True), "P": pc, "src": "插值"})

    targets = [95, 90, 88, 85, 82, 80, 78, 75, 72, 70]

    # ---- 反解：对每个目标温度列出**Pareto 三档**（静音 / 折中 / 性能）
    #      这才是"温度与噪声收敛"的正确表达：不是给一个点，而是给一条前沿上的几个取值。
    ladder = []
    for tgt in targets:
        feas = []
        fans = range(30, 101, 5) if use_fan else [None]
        for fan in fans:
            for cap in range(100, 4, -5):
                pc = p_of_cap(cap)
                if not pc:
                    continue
                x = [pc, fan / 100.0] if use_fan else [pc]
                t = invert_model["model"].predict(x)
                if t <= tgt:
                    feas.append({"cap": cap, "fan": fan, "P": round(pc, 1),
                                 "f": int(f_of_cap(cap, True)), "pred_T": round(t, 1),
                                 "spl_delta_db": round(50 * math.log10((fan or 100) / 100.0), 1)})
        if not feas:
            ladder.append({"T_hi": tgt, "options": [], "achievable": False})
            continue
        if use_fan:
            picks = {
                "性能优先": max(feas, key=lambda c: (c["f"], -c["fan"])),
                "静音优先": min(feas, key=lambda c: (c["fan"], -c["f"])),
                "折中": min(feas, key=lambda c: 0.5 * (1 - c["cap"] / 100.0) + 0.5 * (c["fan"] / 100.0)),
            }
        else:
            picks = {"唯一（风扇由固件控制）": max(feas, key=lambda c: c["f"])}
        seen, opts = set(), []
        for name, c in picks.items():
            key = (c["cap"], c["fan"])
            if key in seen:
                continue
            seen.add(key)
            o = dict(c)
            o["mode"] = name
            o["turbo"] = True
            opts.append(o)
        ladder.append({"T_hi": tgt, "options": opts, "achievable": True})

    policy = {
        "version": 1,
        "generated": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "source_csv": os.path.basename(csv_path),
        "mode": mode,
        "fan_controllable": use_fan,
        "features": FEATURE_NAMES_2D if use_fan else FEATURE_NAMES_1D,
        "steady_model": best["model"].to_json(),
        "inversion_model": invert_model["name"],
        "discrete_table": discrete["model"].to_json() if discrete else None,
        "dynamic": dyn,
        "reference": {
            "T_base": round(min(s["T"] for s in stages), 1),
            "rth_closed_loop": round(linfit([s["P"] for s in cpu], [s["T"] for s in cpu])[1], 3),
        },
        "control_ladder": ladder,
        "cap_power_map": {"points": [[int(c), round(p, 1)] for c, p in zip(caps_on, ps_on)],
                          "note": "Turbo 开启时的实测 cap→功耗分段线性映射"},
        "quality": {
            "stages": len(cpu), "samples": len(rows), "dropped_unsteady": dropped,
            "loo_rmse": round(best["cv"]["rmse"], 2),
            "loo_mae": round(best["cv"]["mae"], 2),
            "loo_r2": round(best["cv"]["r2"], 3),
            "chosen": best["name"],
            "leaderboard": [{"model": r["name"], "rmse": round(r["cv"]["rmse"], 2),
                             "mae": round(r["cv"]["mae"], 2), "r2": round(r["cv"]["r2"], 3)}
                            for r in results],
        },
    }
    return policy, stages, results, rows


def _median_dt(rows):
    ds = [b["t"] - a["t"] for a, b in zip(rows, rows[1:]) if 0 < b["t"] - a["t"] < 10]
    return sorted(ds)[len(ds) // 2] if ds else 1.0


def _fit_tau(rows, model, use_fan):
    """网格搜索 τ，评分用**整段轨迹**的仿真误差（一步预测会把 τ 系统性偏小）。"""
    by_stage = {}
    for r in rows:
        by_stage.setdefault(r["stage"], []).append(r)
    # 预算每个样本的稳态温度（模型是静态的，与 τ 无关）
    tss = {}
    for sid, rs in by_stage.items():
        tss[sid] = [model.predict([r["P"], r["fan"] / 100.0] if use_fan else [r["P"]]) for r in rs]

    best = (60.0, 1e18)
    for tau in [20 + 4 * i for i in range(70)]:
        sse, n = 0.0, 0
        for sid, rs in by_stage.items():
            T = rs[0]["T"]
            arr = tss[sid]
            for k in range(1, len(rs)):
                dt = max(0.2, rs[k]["t"] - rs[k - 1]["t"])
                a = 1 - math.exp(-dt / tau)
                T = T + (arr[k - 1] - T) * a
                if k > 5:                      # 丢掉起始几秒的初值影响
                    sse += (T - rs[k]["T"]) ** 2
                    n += 1
        if n and sse / n < best[1]:
            best = (tau, sse / n)
    tau = best[0]
    return tau, 1 - math.exp(-1.0 / tau)


def _fit_arx(rows):
    """T_{k+1} = a·T_k + b·P_k + c  （线性 ARX，动态建模的经典基线）"""
    pts = []
    for a, b in zip(rows, rows[1:]):
        if b["t"] - a["t"] > 10:
            continue
        pts.append([a["T"], a["P"], 1.0, b["T"]])
    if len(pts) < 30:
        return None
    coef = solve_normal(pts)
    if not coef:
        return None
    sse = 0.0
    for p in pts:
        sse += (coef[0] * p[0] + coef[1] * p[1] + coef[2] - p[3]) ** 2
    return {"a": round(coef[0], 4), "b": round(coef[1], 4), "c": round(coef[2], 3),
            "rmse": round(math.sqrt(sse / len(pts)), 3), "n": len(pts)}


# =============================================================== 输出
def render_md(policy, stages):
    q = policy["quality"]
    L = [f"# 模型选择与策略导出（{policy['source_csv']}）", ""]
    L.append(f"- 模式：**{policy['mode']}**（风扇{'可控' if policy['fan_controllable'] else '不可控 / BIOS 固定策略'}）")
    L.append(f"- 稳态样本：{q['stages']} 个阶段；时间序列样本：{q['samples']}")
    if q.get("dropped_unsteady"):
        L.append(f"- ⚠ 因未达稳态被剔除的阶段：{', '.join(q['dropped_unsteady'])}（该段温度仍在漂移，不能当稳态用）")
    L.append(f"- 采用模型：**{q['chosen']}**，留一交叉验证 RMSE **{q['loo_rmse']} ℃**（MAE {q['loo_mae']} ℃，R² {q['loo_r2']}）")
    L.append(f"- 动态：τ = {policy['dynamic']['tau_s']} s" +
             (f"；ARX 一步 RMSE {policy['dynamic']['arx']['rmse']} ℃" if policy["dynamic"].get("arx") else ""))
    L.append(f"- 闭环等效热阻：{policy['reference']['rth_closed_loop']} ℃/W")
    L += ["", "## 模型排行榜（按阶段留一交叉验证）", "",
          "| 模型 | RMSE ℃ | MAE ℃ | R² |", "|---|---|---|---|"]
    for r in q["leaderboard"]:
        L.append(f"| {r['model']} | {r['rmse']} | {r['mae']} | {r['r2']} |")
    L += ["", "## 控制律阶梯表（离散，可直接查表）", "",
          "| 目标温度 | 档位 | 频率上限 | 风扇 | 频率 MHz | 功耗 W | 预测温度 | 噪声相对满转 |",
          "|---|---|---|---|---|---|---|---|"]
    for b in policy["control_ladder"]:
        if not b["options"]:
            L.append(f"| ≤{b['T_hi']}℃ | — | 5% | 100% | — | — | 达不到 | — |")
            continue
        for o in b["options"]:
            fan_txt = f"{o['fan']}%" if o["fan"] is not None else "BIOS"
            L.append(f"| ≤{b['T_hi']}℃ | {o['mode']} | {o['cap']}% | {fan_txt} | {o['f']} | {o['P']} | "
                     f"{o['pred_T']} ℃ | {o['spl_delta_db']:+.1f} dB |")
    L += ["", "## 稳态特征样本", "",
          "| 阶段 | cap | P (W) | fan % | T (℃) | σ |", "|---|---|---|---|---|---|"]
    for s in stages:
        L.append(f"| {s['stage']} | {s['cap']:.0f} | {s['P']:.1f} | {s['fan']:.0f} | {s['T']:.1f} | {s['T_std']:.2f} |")
    return "\n".join(L)


def render_html(policy, stages, out_path):
    q = policy["quality"]
    def esc(s):
        return str(s).replace("&", "&amp;").replace("<", "&lt;")

    lb = "".join(f"<tr><td>{esc(r['model'])}</td><td class='n'>{r['rmse']}</td>"
                 f"<td class='n'>{r['mae']}</td><td class='n'>{r['r2']}</td></tr>"
                 for r in q["leaderboard"])
    rows_html = []
    for b in policy["control_ladder"]:
        if not b["options"]:
            rows_html.append(f"<tr><td class='n'>≤ {b['T_hi']} ℃</td><td colspan=6>该目标达不到</td></tr>")
            continue
        for o in b["options"]:
            fan_txt = f"{o['fan']}%" if o["fan"] is not None else "BIOS"
            rows_html.append(f"<tr><td class='n'>≤ {b['T_hi']} ℃</td><td>{esc(o['mode'])}</td>"
                             f"<td class='n'>{o['cap']}%</td><td class='n'>{fan_txt}</td>"
                             f"<td class='n'>{o['f']}</td><td class='n'>{o['P']}</td>"
                             f"<td class='n'>{o['pred_T']}</td><td class='n'>{o['spl_delta_db']:+.1f}</td></tr>")
    lad = "".join(rows_html)
    st = "".join(f"<tr><td>{esc(s['stage'])}</td><td class='n'>{s['cap']:.0f}%</td>"
                 f"<td class='n'>{s['P']:.1f}</td><td class='n'>{s['fan']:.0f}</td>"
                 f"<td class='n'>{s['T']:.1f}</td><td class='n'>{s['T_std']:.2f}</td></tr>"
                 for s in stages)
    html = f"""<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
<title>模型选择与策略导出</title><style>
:root{{--ink:#141c26;--dim:#6b7889;--line:#e3e9f1;--bg:#f6f8fb;--acc:#2f7ed8}}
*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--ink);
font-family:"Segoe UI","Microsoft YaHei",system-ui,sans-serif;line-height:1.6}}
.wrap{{max-width:1000px;margin:0 auto;padding:28px 20px 60px}}
h1{{font-size:21px;margin:0 0 6px}}.sub{{color:var(--dim);font-size:13px;margin-bottom:20px}}
.card{{background:#fff;border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:16px}}
h2{{font-size:15px;margin:0 0 12px;padding-left:9px;border-left:3px solid var(--acc)}}
table{{width:100%;border-collapse:collapse;font-size:13px}}
th,td{{padding:7px 8px;border-bottom:1px solid var(--line);text-align:left}}
th{{color:var(--dim);font-weight:600;font-size:12px}}td.n{{text-align:right;font-variant-numeric:tabular-nums}}
.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px}}
.kv{{background:#f8fafc;border:1px solid var(--line);border-radius:9px;padding:9px 11px}}
.kv span{{display:block;font-size:11px;color:var(--dim)}}.kv b{{font-size:15px}}
.best{{background:#eef7ee;font-weight:600}}
</style></head><body><div class="wrap">
<h1>模型选择与策略导出</h1>
<div class="sub">{esc(policy['source_csv'])} · {policy['mode']} 模式 · 生成于 {policy['generated']}</div>

<div class="card"><h2>结论</h2><div class="grid">
<div class="kv"><span>采用模型</span><b>{esc(q['chosen'])}</b></div>
<div class="kv"><span>留一 CV RMSE</span><b>{q['loo_rmse']} ℃</b></div>
<div class="kv"><span>MAE</span><b>{q['loo_mae']} ℃</b></div>
<div class="kv"><span>热时间常数 τ</span><b>{policy['dynamic']['tau_s']} s</b></div>
<div class="kv"><span>闭环热阻</span><b>{policy['reference']['rth_closed_loop']} ℃/W</b></div>
<div class="kv"><span>稳态阶段数</span><b>{q['stages']}</b></div>
</div></div>

<div class="card"><h2>模型排行榜（按阶段留一交叉验证，越低越好）</h2>
<table><thead><tr><th>模型</th><th>RMSE ℃</th><th>MAE ℃</th><th>R²</th></tr></thead>
<tbody>{lb}</tbody></table>
<p class="sub" style="margin:10px 0 0">交叉验证按<strong>阶段</strong>分组留一：同一阶段的相邻样本高度自相关，若按样本随机划分会造成"见过答案"的误差虚低。</p></div>

<div class="card"><h2>控制律阶梯表（Pareto 三档：静音 / 折中 / 性能）</h2>
<table><thead><tr><th>目标温度</th><th>档位</th><th>频率上限</th><th>风扇</th><th>频率 MHz</th><th>功耗 W</th><th>预测温度</th><th>噪声 ΔdB</th></tr></thead>
<tbody>{lad}</tbody></table></div>

<div class="card"><h2>稳态特征样本</h2>
<table><thead><tr><th>阶段</th><th>cap</th><th>P (W)</th><th>fan %</th><th>T ℃</th><th>σ</th></tr></thead>
<tbody>{st}</tbody></table></div>
</div></body></html>"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(html)
    return out_path


def main():
    arg = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "lab-data")
    if os.path.isdir(arg):
        csvs = sorted(glob.glob(os.path.join(arg, "lab-*.csv")))
        if not csvs:
            raise SystemExit(f"{arg} 下没有 lab-*.csv")
    else:
        csvs = [arg]

    for path in csvs:
        policy, stages, results, rows = run(path)
        out_dir = os.path.dirname(os.path.abspath(path))
        tag = os.path.basename(path).replace("lab-", "").replace(".csv", "")
        jp = os.path.join(out_dir, f"model-{tag}.json")
        with open(jp, "w", encoding="utf-8") as f:
            json.dump(policy, f, ensure_ascii=False, indent=2)
        md = render_md(policy, stages)
        with open(os.path.join(out_dir, f"model-{tag}.md"), "w", encoding="utf-8") as f:
            f.write(md)
        html = render_html(policy, stages, os.path.join(out_dir, f"model-{tag}.html"))
        print("=" * 60)
        print("CSV :", path)
        print(md)
        print("\n策略文件:", jp)
        print("报告:", html)


if __name__ == "__main__":
    main()
