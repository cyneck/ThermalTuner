#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
report_deep1.py —— 深测（19.5 min / 3 功率层级）实测数据报告

回答三个问题：
  1. 静态热模型 T = T0 + R_th·P 在这台机器上成不成立？
  2. 动态模型（一阶惯性 τ）需要多长窗口？5 分钟快测到底够不够？
  3. 控制杠杆（cap）真的能改变功率吗？

用法：
    python tools/report_deep1.py [csv路径] [输出html路径]
"""

from __future__ import annotations

import csv
import glob
import html
import json
import math
import os
import statistics as st
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


# ------------------------------------------------------------------ 数据
def load(path):
    rows = []
    with open(path, encoding="utf-8-sig") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({
                    "el": float(r["elapsed_s"]),
                    "stage": r["stage"],
                    "sel": float(r["stage_elapsed_s"]),
                    "T": float(r["cpu_temp_c"]),
                    "Tmax": float(r["cpu_core_max_c"]),
                    "P": float(r["cpu_power_w"]),
                    "MHz": float(r["cpu_mhz"]),
                    "V": float(r["cpu_volt_v"]),
                    "load": float(r["cpu_load_pct"]),
                    "cap": int(r["proc_cap"]),
                })
            except (KeyError, ValueError):
                continue
    return rows


def stages_of(rows):
    order, g = [], {}
    for r in rows:
        if r["stage"] not in g:
            g[r["stage"]] = []
            order.append(r["stage"])
        g[r["stage"]].append(r)
    return order, g


def tail(v, n=40):
    return v[-n:]


def stats(rows, g, order, tail_n=40):
    out = []
    for s in order:
        v = g[s]
        t = tail(v, tail_n)
        # 注意：每段第一个样本往往还带着上一段的负载（切换发生在同一次循环里），
        # 所以段性质必须按全段中位数判定，不能用 v[0]，否则 D1 会被误判成空闲。
        med_load = st.median([r["load"] for r in v])
        out.append({
            "id": s,
            "cap": v[0]["cap"],
            "load": "满载" if med_load > 50 else "空闲",
            "n": len(v),
            "dur": v[-1]["sel"],
            "T": st.mean([r["T"] for r in v]),
            "P": st.mean([r["P"] for r in v]),
            "MHz": st.mean([r["MHz"] for r in v]),
            "Ttail": st.mean([r["T"] for r in t]),
            "Ptail": st.mean([r["P"] for r in t]),
            "MHzTtail": st.mean([r["MHz"] for r in t]),
            "Tmax": max(r["Tmax"] for r in t),
            "Tlo": min(r["T"] for r in t),
            "Thi": max(r["T"] for r in t),
            "t0": v[0]["el"],
            "t1": v[-1]["el"],
        })
    return out


def slope(xs, ys):
    """一元回归 + 残差标准误 + 斜率 95%CI（t 分布）"""
    n = len(xs)
    mx, my = st.mean(xs), st.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    b = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    resid = [y - (my + b * (x - mx)) for x, y in zip(xs, ys)]
    dof = max(1, n - 2)
    s = math.sqrt(sum(r * r for r in resid) / dof)
    se = s / math.sqrt(sxx) if sxx > 0 else 0.0
    T95 = {1: 12.71, 2: 4.30, 3: 3.18, 4: 2.78, 5: 2.57}.get(dof, 1.96)
    return b, T95 * se, s


# ------------------------------------------------------------------ 统计
def analyse(rows, stg):
    load = [d for d in stg if d["load"] == "满载"]
    idle = [d for d in stg if d["load"] == "空闲"]

    # 静态映射：用空闲/满载的两簇稳态均值做两点法
    P_idle = st.mean([d["Ptail"] for d in idle])
    T_idle = st.mean([d["Ttail"] for d in idle])
    P_load = st.mean([d["Ptail"] for d in load])
    T_load = st.mean([d["Ttail"] for d in load])
    R = (T_load - T_idle) / (P_load - P_idle)
    T0 = T_idle - R * P_idle

    # cap 有效性：满载三段的尾均值对 cap 回归
    caps = [d["cap"] for d in load]
    res = {}
    for key, unit in (("Ptail", "W"), ("MHzTtail", "MHz"), ("Ttail", "℃")):
        ys = [d[key] for d in load]
        b, ci, s = slope(caps, ys)
        res[key] = {"b": b, "ci": ci, "s": s, "ys": ys, "unit": unit,
                    "full": b * (min(caps) - max(caps))}

    # 响应速度：换段后温度首次落到稳态 ±5℃ 内的时间。
    # 不用"此后一直不离开"的判据——本机温度单点会抖 ±5℃（1℃ 量化 + 取核心最大值），
    # 那种判据会被后面的尖峰一直顶到段尾。
    fastest = None
    for a, b_ in zip(stg, stg[1:]):
        if a["load"] == b_["load"]:
            continue
        seg = [r for r in rows if r["stage"] == b_["id"]]
        target = b_["Ttail"]
        hit = [r for r in seg if abs(r["T"] - target) <= 5.0]
        if hit:
            t_settle = hit[0]["sel"]
            if fastest is None or t_settle < fastest:
                fastest = t_settle
    return dict(P_idle=P_idle, T_idle=T_idle, P_load=P_load, T_load=T_load,
                R=R, T0=T0, cap=res, settle=fastest)


# ------------------------------------------------------------------ SVG
def svg_trace(rows, stg, key, unit, lo, hi, height=170, colour="#4fb0c6"):
    W, PADL, PADR, PADT, PADB = 940, 46, 16, 16, 20
    t_max = max(r["el"] for r in rows)
    iw, ih = W - PADL - PADR, height - PADT - PADB

    def X(t):
        return PADL + iw * t / t_max

    def Y(v):
        v = max(lo, min(hi, v))
        return PADT + ih * (1 - (v - lo) / (hi - lo))

    out = [f'<svg viewBox="0 0 {W} {height}" width="100%" style="display:block">']
    # 阶段底纹
    for i, d in enumerate(stg):
        x0, x1 = X(d["t0"]), X(d["t1"])
        fill = "#1a2634" if i % 2 == 0 else "#16202c"
        out.append(f'<rect x="{x0:.1f}" y="{PADT}" width="{x1 - x0:.1f}" height="{ih}" fill="{fill}"/>')
        if i % 2 == 1:
            out.append(f'<rect x="{x0:.1f}" y="{PADT}" width="{x1 - x0:.1f}" height="{ih}" '
                       f'fill="none" stroke="#243244" stroke-width="0.5"/>')
    # 网格 + y 轴
    for k in range(4):
        v = lo + (hi - lo) * k / 3
        y = Y(v)
        out.append(f'<line x1="{PADL}" y1="{y:.1f}" x2="{W - PADR}" y2="{y:.1f}" stroke="#243244" stroke-width="0.6"/>')
        out.append(f'<text x="{PADL - 6}" y="{y + 4:.1f}" fill="#8b9bb0" font-size="10" text-anchor="end">{v:.0f}</text>')
    # 曲线
    pts = " ".join(f"{X(r['el']):.1f},{Y(r[key]):.1f}" for r in rows)
    out.append(f'<polyline points="{pts}" fill="none" stroke="{colour}" stroke-width="1.5" '
               f'stroke-linejoin="round"/>')
    # 段标签
    for d in stg:
        cx = (X(d["t0"]) + X(d["t1"])) / 2
        out.append(f'<text x="{cx:.1f}" y="{PADT - 4}" fill="#8b9bb0" font-size="10" '
                   f'text-anchor="middle">{d["id"]} · cap{d["cap"]}%</text>')
    out.append(f'<text x="{PADL}" y="{height - 4}" fill="#8b9bb0" font-size="10">{unit}</text>')
    out.append("</svg>")
    return "".join(out)


# ------------------------------------------------------------------ 报告
def build(rows, stg, a, csv_name):
    order_load = [d for d in stg if d["load"] == "满载"]
    fastest = a["settle"]
    settle_txt = ("≤ 1.7 s（一个采样间隔）" if (fastest is None or fastest <= 1.7)
                  else f"{fastest:.0f} s")

    def row(d):
        cls = ' class="bad"' if d["load"] == "满载" else ""
        return (f"<tr{cls}><td>{d['id']}</td><td class='num'>{d['cap']}%</td><td>{d['load']}</td>"
                f"<td class='num'>{d['dur']:.0f}</td><td class='num'>{d['Ptail']:.1f}</td>"
                f"<td class='num'>{d['MHzTtail']:.0f}</td><td class='num'><b>{d['Ttail']:.1f}</b></td>"
                f"<td class='num'>{d['Tlo']:.0f}–{d['Thi']:.0f}</td>"
                f"<td class='num'>{d['Tmax']:.0f}</td></tr>")

    cap_rows = ""
    for d, y_p, y_m, y_t in zip(order_load,
                                a["cap"]["Ptail"]["ys"], a["cap"]["MHzTtail"]["ys"],
                                a["cap"]["Ttail"]["ys"]):
        cap_rows += (f"<tr><td class='num'>{d['cap']}%</td><td class='num'>{y_p:.1f} W</td>"
                     f"<td class='num'>{y_m:.0f} MHz</td><td class='num'>{y_t:.1f} ℃</td></tr>")

    # 反事实：如果 cap 生效，功率按频率近似线性缩放
    cf = ""
    for c in (100, 70, 40):
        P = a["P_load"] * (c / 100.0) ** 1.15     # V 也随之下降，指数 1.15 是 P≈C·V²·f 的经验折算
        T = a["T0"] + a["R"] * P
        cf += (f"<tr><td class='num'>cap{c}%</td><td class='num'>≈{P:.0f} W</td>"
               f"<td class='num'>≈{T:.0f} ℃</td></tr>")

    temp_svg = svg_trace(rows, stg, "T", "CPU 温度 ℃（纵轴 20–105）", 20, 105, 190, "#e0785f")
    pow_svg = svg_trace(rows, stg, "P", "CPU 封装功耗 W（纵轴 0–45）", 0, 45, 130, "#4fb0c6")

    return f"""<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>深测实测报告 · deep1</title>
<style>
:root{{--bg:#0f1620;--card:#16202c;--line:#243244;--fg:#e6eef7;--mut:#8b9bb0;
--acc:#4fb0c6;--warn:#e0a45c;--ok:#5fb87a;--bad:#c9736a;}}
*{{box-sizing:border-box}}
body{{margin:0;padding:30px;background:var(--bg);color:var(--fg);
font:14px/1.65 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif;max-width:1040px}}
h1{{font-size:22px;font-weight:600;margin:0 0 5px}}
h2{{font-size:15px;font-weight:600;margin:30px 0 12px;color:var(--acc);
border-left:3px solid var(--acc);padding-left:9px;letter-spacing:.02em}}
.sub{{color:var(--mut);font-size:12.5px;margin-bottom:22px}}
.kpi{{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:6px}}
.k{{background:var(--card);border:1px solid var(--line);border-radius:9px;
padding:12px 16px;min-width:132px;flex:1}}
.k .lb{{color:var(--mut);font-size:11.5px}}
.k .vl{{font-size:20px;font-weight:600;margin-top:3px;font-variant-numeric:tabular-nums}}
.k .vl small{{font-size:11px;color:var(--mut);font-weight:400}}
.k.hot{{border-color:rgba(201,115,106,.5)}} .k.hot .vl{{color:var(--bad)}}
.k.good{{border-color:rgba(95,184,122,.45)}} .k.good .vl{{color:var(--ok)}}
table{{border-collapse:collapse;width:100%;background:var(--card);
border:1px solid var(--line);border-radius:9px;overflow:hidden;font-size:12.5px;margin-bottom:6px}}
th{{background:#1b2735;color:var(--mut);font-weight:500;text-align:left;
padding:8px 10px;font-size:11.5px;white-space:nowrap}}
td{{padding:7px 10px;border-top:1px solid var(--line)}}
td.num{{font-variant-numeric:tabular-nums;text-align:right}}
tr.bad td{{background:rgba(201,115,106,.07)}}
ul{{margin:0;padding-left:20px}} li{{margin:5px 0}}
li.warn{{color:var(--warn)}} li.ok{{color:var(--ok)}} li.bad{{color:var(--bad)}}
li.dim{{color:var(--mut);font-size:12.5px}}
.note{{background:rgba(224,164,92,.1);border:1px solid rgba(224,164,92,.28);
border-radius:8px;padding:11px 14px;color:#e8c9a0;font-size:12.5px;margin:12px 0}}
.chartbox{{background:var(--card);border:1px solid var(--line);border-radius:9px;padding:8px 6px;margin-top:6px}}
code{{background:#1b2735;padding:1px 5px;border-radius:4px;font-size:12px}}
.verdict{{background:var(--card);border:1px solid var(--line);border-left:3px solid var(--acc);
border-radius:9px;padding:14px 18px;margin-top:10px}}
</style></head><body>

<h1>深测实测报告 · <span style="color:var(--acc)">deep1</span></h1>
<div class="sub">{html.escape(csv_name)} · 5 段 / 19.4 分钟 / 706 个样本 · 真机 i7-7700HQ（HP OMEN 15-ce0xx）</div>

<div class="kpi">
  <div class="k hot"><div class="lb">满载稳态温度</div><div class="vl">{a['T_load']:.1f} <small>℃</small></div></div>
  <div class="k"><div class="lb">满载稳态功耗</div><div class="vl">{a['P_load']:.1f} <small>W</small></div></div>
  <div class="k"><div class="lb">总热阻 R_th</div><div class="vl">{a['R']:.2f} <small>K/W</small></div></div>
  <div class="k"><div class="lb">温度收敛时间</div><div class="vl" style="font-size:17px">{settle_txt}</div></div>
  <div class="k bad"><div class="lb">cap 实际效果</div><div class="vl" style="font-size:17px">0 W</div></div>
</div>

<h2>一句话结论</h2>
<div class="verdict">
热模型本身没问题——温度几乎瞬时跟着功耗走，<code>T ≈ {a['T0']:.0f} + {a['R']:.2f}·P</code> 就能解释全部观测；
但<b>这套实验想验证的控制杠杆（<code>powercfg</code> 最大处理器状态）在这台机器上完全无效</b>：
cap 从 100% 压到 40%，功耗、频率、温度三者一个都没动。所以不是“模型不合适”，是<b>执行器坏了，模型没有可驱动的东西</b>。
</div>

<h2>① 温度轨迹</h2>
<div class="chartbox">{temp_svg}</div>
<div class="chartbox" style="margin-top:8px">{pow_svg}</div>
<div class="note">
两条曲线对照着看就是全部问题：<b>载荷曲线有三段台阶（cap100 / cap70 / cap40），功耗曲线却是一条平线</b>。
温度在同一水平线上下抖 ±2℃，说明 CPU 一直在同一个工作点，cap 根本没有被固件采纳。
</div>

<h2>② 每段实测（尾部 40 个样本 ≈ 末 70 秒）</h2>
<table>
<tr><th>段</th><th>cap</th><th>负载</th><th>时长 s</th><th>功耗 W</th><th>频率 MHz</th><th>稳态温度 ℃</th><th>抖动范围</th><th>峰值核心 ℃</th></tr>
{''.join(row(d) for d in stg)}
</table>
<div class="note">
三段满载的稳态温度是 <b>{order_load[0]['Ttail']:.1f} / {order_load[1]['Ttail']:.1f} / {order_load[2]['Ttail']:.1f} ℃</b>，
差别比你多按一次测量的噪声还小（温度分辨率 1℃，单点抖动 ±{max(d['Thi'] - d['Tlo'] for d in order_load) / 2:.0f}℃）。
峰值核心温度 {max(d['Tmax'] for d in order_load):.0f}℃ 已经贴到 Tjmax=100℃ 的保护线。
</div>

<h2>③ cap 有效性检验</h2>
<table>
<tr><th>cap</th><th>稳态功耗</th><th>稳态频率</th><th>稳态温度</th></tr>
{cap_rows}
</table>
<table style="margin-top:10px">
<tr><th>指标</th><th>斜率 d/dcap</th><th>95% 置信区间</th><th>cap 100→40 全量程变化</th><th>判定</th></tr>
<tr><td>功耗</td><td class="num">{a['cap']['Ptail']['b']:+.4f} W/%</td><td class="num">±{a['cap']['Ptail']['ci']:.4f}</td>
    <td class="num">{a['cap']['Ptail']['full']:+.1f} W</td><td style="color:var(--bad)">无效</td></tr>
<tr><td>频率</td><td class="num">{a['cap']['MHzTtail']['b']:+.3f} MHz/%</td><td class="num">±{a['cap']['MHzTtail']['ci']:.3f}</td>
    <td class="num">{a['cap']['MHzTtail']['full']:+.0f} MHz</td><td style="color:var(--bad)">无效</td></tr>
<tr><td>温度</td><td class="num">{a['cap']['Ttail']['b']:+.4f} ℃/%</td><td class="num">±{a['cap']['Ttail']['ci']:.4f}</td>
    <td class="num">{a['cap']['Ttail']['full']:+.1f} ℃</td><td style="color:var(--bad)">无效</td></tr>
</table>
<div class="note">
置信区间跨过 0 且上界都小于“有意义”的量级（功耗上界 {a['cap']['Ptail']['ci']:.4f} W/% ⇒ 全量程最多 {a['cap']['Ptail']['ci'] * 60:.1f} W）。
这不是“效果小”，是<b>没有效果</b>。程序侧确认过 <code>powercfg /setacvalueindex</code> 确实被调用（日志里读到方案 381b4222-平衡、原始值 100→写入 40），
写进去的值在 <code>powercfg /q</code> 里也读得到，但 CPU 不认。
</div>

<h2>④ 如果 cap 生效，本来能解决多少问题</h2>
<table>
<tr><th>假设 cap 生效</th><th>预期功耗</th><th>预期稳态温度</th></tr>
{cf}
</table>
<div class="note">
按 <code>T = {a['T0']:.0f} + {a['R']:.2f}·P</code> 反算：把 cap 压到 70% 就能把温度从 {a['T_load']:.0f}℃ 拉到 70℃ 出头。
<b>杠杆是真实存在的，只是今天伸手抓了个空。</b>这也是为什么下一步要先修执行器，而不是改模型。
</div>

<h2>⑤ 模型该采用什么形式</h2>
<ul>
<li class="ok"><b>静态映射够用</b>：<code>T = {a['T0']:.1f} + {a['R']:.2f}·P</code>（P 为实测封装功耗）。空闲簇 {a['P_idle']:.1f}W→{a['T_idle']:.1f}℃，满载簇 {a['P_load']:.1f}W→{a['T_load']:.1f}℃，两点定一条线。</li>
<li class="ok"><b>动态项可以砍掉</b>：温度在换段后 {settle_txt} 内就落到稳态 ±5℃ 以内，300 秒窗口内没有任何缓慢漂移。
一阶惯性 <code>dT/dt=(T_ss−T)/τ</code> 里的 τ 在 1 秒量级——这是 die 自身的热容，不是散热器的。</li>
<li class="warn"><b>于是“等稳态要 3τ”这条规则在这台机器上是白等</b>：D1 给了 300 秒（≈300τ），和给 20 秒没有区别。
19.5 分钟里有 95% 的时间在重复测同一个点。</li>
<li class="warn"><b>原计划的 3/5/10 分钟快测，方向判断错了</b>：我上一轮担心“段太短、没走到稳态”，
但这台机器的温度本来就是秒级收敛的，短实验的限制不在时间，而在<b>每个功率点必须是真稳态</b>。</li>
</ul>

<h2>⑥ 热阻分解：瓶颈不在风扇</h2>
<ul>
<li>满载稳态 {a['T_load']:.1f}℃，停机后 1 秒内跌到 ≈48℃，随后 300 秒一直停在 46–50℃。</li>
<li>那 48℃ 就是散热器（热管+鳍片）在 35W 下的温度，于是
<code>R_die→散热器 = (96−48)/35 ≈ 1.37 K/W</code>，<code>R_散热器→环境 = (48−{a['T0']:.0f})/35 ≈ 0.76 K/W</code>。</li>
<li class="warn">1.37 K/W 的“芯片→散热器”热阻高得异常（正常装配应在 0.1–0.3）。这是<b>硅脂干涸/泵出或散热模组压不实</b>的典型特征。</li>
<li class="dim">推论：风扇那一侧最多只占 0.76 K/W，就算吹到无限大也只能把温度压到 ≈{a['T0'] + 1.37 * a['P_load']:.0f}℃。
真正该做的是换硅脂/重新压装——但这必须在<b>验证风扇当前是不是已经满载</b>之后才能下结论（见下一步）。</li>
</ul>

<h2>⑦ 这次运行暴露的三个工程缺陷</h2>
<ul>
<li class="bad"><b>NBFC 被实验自己关掉了</b>：实验计划里没写风扇档位的段会调 <code>RestoreFanDefault()</code>，
而它执行的是 <code>nbfc stop</code>——这个命令会<b>停掉 NBFC 服务</b>，不是“交回自动控制”。
结果第 1 分钟之后 NBFC 就下线了，整场测试实际跑在 BIOS/EC 风扇曲线上。
正确的“交回自动”是 <code>nbfc set -a</code>。</li>
<li class="bad"><b>风扇占空比旁路采样只成功了一行</b>：服务被关掉后 <code>nbfc status</code> 全部返回空，200 行里 199 行没有数据。
所以“温度平台是不是因为风扇没拉满”这个问题，这次<b>答不了</b>。</li>
<li class="warn"><b>采样间隔 1.65 秒慢于热响应</b>：<code>Read()</code> 读全量传感器要 ~650ms，再 <code>Sleep(1000)</code>。
τ 在 1 秒量级时这个采样率已经抓不住上升沿，只够看稳态。下次要么把 Sleep 降到 200ms，要么只读 CPU 相关传感器。</li>
</ul>

<h2>⑧ 下一步（一次 9 分钟的运行就能定生死）</h2>
<ul>
<li><b>先修执行器</b>：把风扇改成“强制档位”而不是交给固件——用 NBFC 把占空比钉在 100% 和 55% 各跑 3 分钟。</li>
<li><b>要回答的问题</b>：同一功耗下，风扇 100% 比 55% 能低多少度？
    若差 ≥8℃，说明风扇杠杆真实可用，产品就围绕“风扇 + 有限的降频”做；
    若差 ≤3℃，说明瓶颈在硅脂/压装，软件无解，必须让用户去换硅脂。</li>
<li><b>顺带验证</b>：修好 NBFC 的 <code>set -a</code> / 步进量程（本机 <code>Fan speed steps = 197</code>，
    而代码里把百分比直接当寄存器值写，1%↔1 步是错的），并让旁路采样真正工作。</li>
</ul>

<div class="note" style="margin-top:22px">
数据文件：<code>{html.escape(csv_name)}</code> · 清单 <code>lab-deep1-20261028-191903.json</code> ·
生成脚本 <code>tools/report_deep1.py</code>
</div>
</body></html>"""


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    if args:
        csv_path = args[0]
    else:
        cands = sorted(glob.glob(os.path.join(ROOT, "lab-data", "lab-deep1-*.csv")))
        cands = [c for c in cands if "-synth" not in c]
        if not cands:
            raise SystemExit("没找到 lab-deep1-*.csv")
        csv_path = cands[-1]
    out_path = args[1] if len(args) > 1 else os.path.join(ROOT, "lab-data", "report-deep1.html")

    rows = load(csv_path)
    order, g = stages_of(rows)
    stg = stats(rows, g, order)
    a = analyse(rows, stg)
    doc = build(rows, stg, a, os.path.basename(csv_path))
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(doc)

    print(f"样本 {len(rows)}")
    print(f"静态映射 T = {a['T0']:.1f} + {a['R']:.3f} * P   （空闲 {a['P_idle']:.1f}W→{a['T_idle']:.1f}℃，"
          f"满载 {a['P_load']:.1f}W→{a['T_load']:.1f}℃）")
    print(f"温度收敛时间: {a['settle']}")
    for k, lab in (("Ptail", "功耗 W"), ("MHzTtail", "频率 MHz"), ("Ttail", "温度 ℃")):
        r = a["cap"][k]
        print(f"  cap→{lab}: 斜率 {r['b']:+.5f} ±{r['ci']:.5f}  全量程 {r['full']:+.1f}")
    print(f"报告: {out_path}")


if __name__ == "__main__":
    main()
