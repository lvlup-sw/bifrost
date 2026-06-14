#!/usr/bin/env python3
"""Generate the homogeneous-server-class figure SVGs for the 2026-06-13 Azure D64s_v6 run.

Mirrors the four-figure BCL-proposal set from DataFerry's i9-13900K validation
(docs/benchmarks/2026-06-11-cpq-v2-serverclass-i9-13900k.md): (1) scalability faceted per
workload vs the Naive single-lock baseline, (2) speedup over Naive, (3) single-thread scalar
latency, (4) relaxation cost — plus a per-doubling near-linearity panel and the stickiness ladder.

Data-driven (reads the sibling CSVs), pure stdlib — no matplotlib. Style mirrors
src/Bifrost.Concurrency/diagrams/generate_charts.py (GitHub palette, transparent background).
Colours follow the *Bifrost* convention so these sit beside BACKGROUND.md's charts:
relaxed/v2 = ORANGE, Naive/locking = BLUE, strict = GRAY (DataFerry's doc uses the inverse).

    python3 docs/benchmarks/data/azure-d64sv6-2026-06-13/generate_charts.py
"""

import csv
import math
from pathlib import Path

HERE = Path(__file__).parent

FG = "#8b949e"
GRID = "#8b949e"
BLUE = "#58a6ff"      # Naive single-lock / LockingPriorityQueue
ORANGE = "#f0883e"    # MultiQueue (relaxed / v2)
GRAY = "#8b949e"      # strict TryDequeueMin / baseline
RED = "#f85149"       # this-host marker / below-parity
GREEN = "#3fb950"     # ideal reference / Drain
PURPLE = "#bc8cff"    # NarrowKeyRange
CYAN = "#39c5cf"      # SplitProducerConsumer
FONT = "ui-sans-serif, system-ui, sans-serif"

THREADS = [1, 2, 4, 8, 16, 32, 64]   # capped at 64 logical (full on-core occupancy on D64s_v6)
WORKLOADS = ["UniformMixed5050", "SplitProducerConsumer", "NarrowKeyRange", "Drain"]
WL_COLOR = {"UniformMixed5050": ORANGE, "SplitProducerConsumer": CYAN,
            "NarrowKeyRange": PURPLE, "Drain": GREEN}
WL_LABEL = {"UniformMixed5050": "UniformMixed5050", "SplitProducerConsumer": "SplitProducerConsumer",
            "NarrowKeyRange": "NarrowKeyRange", "Drain": "Drain (1M pre-loaded)"}


# --- svg helpers (mirror the repo generator) ----------------------------------------
def svg_open(w, h, title):
    return [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" '
            f'font-family="{FONT}" role="img" aria-label="{title}">']


def _esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def text(x, y, s, size=12, anchor="middle", fill=FG, weight="normal"):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" text-anchor="{anchor}" '
            f'fill="{fill}" font-weight="{weight}">{_esc(s)}</text>')


def legend(lines, x, y, entries, gap=20):
    for i, (label, color) in enumerate(entries):
        yy = y + i * gap
        lines.append(f'<rect x="{x}" y="{yy - 9}" width="12" height="12" rx="2" fill="{color}"/>')
        lines.append(text(x + 18, yy + 1, label, size=12, anchor="start"))


def legend_row(lines, x, y, entries):
    """Horizontal legend (swatch + label, left to right)."""
    cx = x
    for label, color in entries:
        lines.append(f'<rect x="{cx}" y="{y - 9}" width="12" height="12" rx="2" fill="{color}"/>')
        lines.append(text(cx + 18, y + 1, label, size=12, anchor="start"))
        cx += 18 + 8 + len(label) * 7.2


def vlabel(lines, x, ycenter, s, size=12):
    """Y-axis title, rotated vertical so long labels never overflow the left margin."""
    lines.append(f'<text x="{x:.1f}" y="{ycenter:.1f}" font-size="{size}" text-anchor="middle" '
                 f'fill="{FG}" transform="rotate(-90 {x:.1f} {ycenter:.1f})">{_esc(s)}</text>')


def legend_lines(lines, x, y, entries):
    """Horizontal legend drawn with line segments so solid/dashed styles are distinguishable."""
    cx = x
    for label, color, dash in entries:
        da = f' stroke-dasharray="{dash}"' if dash else ''
        lines.append(f'<line x1="{cx:.1f}" y1="{y}" x2="{cx + 24:.1f}" y2="{y}" '
                     f'stroke="{color}" stroke-width="2.5"{da}/>')
        lines.append(text(cx + 30, y + 4, label, size=12, anchor="start"))
        cx += 30 + len(label) * 7.2 + 18


# --- data loading -------------------------------------------------------------------
def _rows(path):
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(ln for ln in f if not ln.startswith("#")))


def load_throughput(path):
    out = {}
    for r in _rows(path):
        out.setdefault((r["Target"], r["Workload"]), {})[int(r["ThreadCount"])] = \
            float(r["OpsPerSecond"]) / 1e6
    return out


def load_stickiness(path):
    out = {}
    for r in _rows(path):
        if r["Target"] != "MultiQueueRelaxed":
            continue
        out.setdefault((r["Workload"], int(r["ThreadCount"])), {})[int(r["Stickiness"])] = \
            float(r["OpsPerSecond"]) / 1e6
    return out


def load_bdn(path):
    out = {}
    for r in _rows(path):
        if not r["Method"].endswith("_Int"):
            continue
        out.setdefault(r["Method"], {})[int(r["Population"])] = \
            float(r["Mean"].replace(" ns", "").replace(",", ""))
    return out


# --- Figure 1: scalability faceted per workload, relaxed vs Naive vs strict -----------
def fig_scalability(tp):
    w, h = 920, 700
    L = svg_open(w, h, "Scalability across workloads — relaxed vs Naive single-lock")
    L.append(text(w / 2, 26, "Scalability across workloads — ConcurrentPriorityQueue vs Naive single-lock",
                  size=16, weight="bold"))
    L.append(text(w / 2, 45, "Xeon 8573C, 64 logical cores, 3 s windows · one panel per workload",
                  size=11))
    legend_row(L, 150, 66, [("ConcurrentPriorityQueue (relaxed)", ORANGE),
                            ("Naive (single global lock)", BLUE),
                            ("strict TryDequeueMin (O(n) scan)", GRAY)])
    ymax = 120.0
    cells = [(70, 430, 110, 340), (510, 870, 110, 340),
             (70, 430, 410, 640), (510, 870, 410, 640)]
    for wl, (x0, x1, y0, y1) in zip(WORKLOADS, cells):
        _panel(L, x0, x1, y0, y1, WL_LABEL[wl], ymax, [
            (ORANGE, tp[("MultiQueueRelaxed", wl)]),
            (BLUE, tp[("LockingBaseline", wl)]),
            (GRAY, tp[("MultiQueueDequeueMin", wl)]),
        ])
    L.append("</svg>")
    return "\n".join(L)


def _panel(L, x0, x1, y0, y1, title, ymax, series):
    def xf(i):
        return x0 + i * (x1 - x0) / (len(THREADS) - 1)

    def yf(v):
        return y1 - (min(v, ymax) / ymax) * (y1 - y0)

    L.append(text((x0 + x1) / 2, y0 - 12, title, size=13, weight="bold", fill="#c9d1d9"))
    for t in (0, 40, 80, 120):
        L.append(f'<line x1="{x0}" y1="{yf(t):.1f}" x2="{x1}" y2="{yf(t):.1f}" '
                 f'stroke="{GRID}" stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(x0 - 6, yf(t) + 4, str(t), size=10, anchor="end"))
    for i, t in enumerate(THREADS):
        L.append(text(xf(i), y1 + 16, str(t), size=10))
    L.append(text((x0 + x1) / 2, y1 + 33, "threads (log2)", size=10))
    L.append(text(x0 - 30, (y0 + y1) / 2, "M ops/s", size=10))
    for color, pts in series:
        poly = " ".join(f"{xf(i):.1f},{yf(pts[t]):.1f}" for i, t in enumerate(THREADS) if t in pts)
        L.append(f'<polyline points="{poly}" fill="none" stroke="{color}" stroke-width="2.5"/>')
        for i, t in enumerate(THREADS):
            if t in pts:
                L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(pts[t]):.1f}" r="3" fill="{color}"/>')
    # annotate the relaxed peak (first series)
    rp = series[0][1]
    pk_t = max(THREADS, key=lambda t: rp[t])
    L.append(text(xf(THREADS.index(pk_t)), yf(rp[pk_t]) - 8, f"{rp[pk_t]:.0f}",
                  size=10, fill=ORANGE, weight="bold"))


# --- Figure 2: speedup over Naive, all workloads, log-y -----------------------------
def fig_speedup(tp):
    w, h = 760, 460
    px0, px1, py0, py1 = 76, 700, 86, 388
    ratios = {wl: {t: tp[("MultiQueueRelaxed", wl)][t] / tp[("LockingBaseline", wl)][t]
                   for t in THREADS} for wl in WORKLOADS}
    ymin, ymax = 0.25, 32.0  # log scale

    def xf(i):
        return px0 + i * (px1 - px0) / (len(THREADS) - 1)

    def yf(v):
        lv = math.log2(max(min(v, ymax), ymin))
        return py1 - (lv - math.log2(ymin)) / (math.log2(ymax) - math.log2(ymin)) * (py1 - py0)

    L = svg_open(w, h, "Speedup over the Naive single-lock baseline")
    L.append(text(w / 2, 26, "Speedup over Naive single-lock — relaxed ÷ Naive (log y)", size=16, weight="bold"))
    L.append(text(w / 2, 45, "Above the dashed parity line the relaxed queue wins · all four workloads cross 1× by 4 threads",
                  size=11))
    for t in (0.25, 0.5, 1, 2, 4, 8, 16, 32):
        y = yf(t)
        L.append(f'<line x1="{px0}" y1="{y:.1f}" x2="{px1}" y2="{y:.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(px0 - 8, y + 4, f"{t:g}×", size=11, anchor="end"))
    L.append(f'<line x1="{px0}" y1="{yf(1):.1f}" x2="{px1}" y2="{yf(1):.1f}" stroke="{FG}" '
             f'stroke-width="1.5" stroke-dasharray="6 4"/>')
    L.append(text(px1 - 4, yf(1) - 6, "parity (1×)", size=10, fill=FG, anchor="end"))
    for i, t in enumerate(THREADS):
        L.append(text(xf(i), py1 + 18, str(t), size=11))
    L.append(text((px0 + px1) / 2, py1 + 38, "threads (log2)", size=12))
    for wl in WORKLOADS:
        pts = ratios[wl]
        poly = " ".join(f"{xf(i):.1f},{yf(pts[t]):.1f}" for i, t in enumerate(THREADS))
        L.append(f'<polyline points="{poly}" fill="none" stroke="{WL_COLOR[wl]}" stroke-width="2.5"/>')
        for i, t in enumerate(THREADS):
            L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(pts[t]):.1f}" r="3.5" fill="{WL_COLOR[wl]}"/>')
    # end-of-line ratio labels at 64T, de-collided top-to-bottom so the close ones don't overlap
    prev_y = -1e9
    for wl in sorted(WORKLOADS, key=lambda w: ratios[w][64], reverse=True):
        v = ratios[wl][64]
        y = yf(v)
        if y < prev_y + 15:
            y = prev_y + 15
        prev_y = y
        L.append(text(xf(len(THREADS) - 1) + 10, y + 3, f"{v:.0f}×",
                      size=11, fill=WL_COLOR[wl], weight="bold", anchor="start"))
    legend(L, px0 + 14, py0 + 14, [(WL_LABEL[wl], WL_COLOR[wl]) for wl in WORKLOADS])
    L.append("</svg>")
    return "\n".join(L)


# --- Figure 3: single-thread scalar latency vs population ----------------------------
def fig_latency(bdn):
    # population 10 is omitted: with 256 sub-queues a 10-element queue is ~96% empty, so each
    # TryDequeue pays the honest-emptiness verification scan (~260 ns) — the sparse-structure
    # regime, not steady-state latency. Plotting it distorts the axis and reads as an error.
    pops = [1000, 100000, 1000000]
    series = [("ConcurrentPriorityQueue (relaxed)", ORANGE, bdn["MultiQueue_EnqueueDequeue_Int"]),
              ("Naive (LockingPriorityQueue)", BLUE, bdn["Locking_EnqueueDequeue_Int"]),
              ("lock-wrapped PriorityQueue", GRAY, bdn["RawLocked_EnqueueDequeue_Int"])]
    w, h = 760, 440
    px0, px1, py0, py1 = 76, 730, 70, 372
    ymax = 200.0

    def xf(i):
        return px0 + i * (px1 - px0) / (len(pops) - 1)

    def yf(v):
        return py1 - (min(v, ymax) / ymax) * (py1 - py0)

    L = svg_open(w, h, "Single-thread Enqueue+Dequeue latency")
    L.append(text(w / 2, 26, "Single-thread Enqueue+Dequeue latency", size=16, weight="bold"))
    L.append(text(w / 2, 45, "All paths 0 B/op · relaxed within 1.4–2.2× of a lock-wrapped PriorityQueue at population 1k+",
                  size=11))
    for t in range(0, 201, 40):
        L.append(f'<line x1="{px0}" y1="{yf(t):.1f}" x2="{px1}" y2="{yf(t):.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(px0 - 8, yf(t) + 4, str(t), size=11, anchor="end"))
    vlabel(L, 22, (py0 + py1) / 2, "ns / op")
    for i, p in enumerate(pops):
        L.append(text(xf(i), py1 + 18, {10: "10", 1000: "1k", 100000: "100k", 1000000: "1M"}[p], size=11))
    L.append(text((px0 + px1) / 2, py1 + 38, "population (log)", size=12))
    for label, color, pts in series:
        poly = " ".join(f"{xf(i):.1f},{yf(pts[p]):.1f}" for i, p in enumerate(pops))
        L.append(f'<polyline points="{poly}" fill="none" stroke="{color}" stroke-width="2.5"/>')
        for i, p in enumerate(pops):
            L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(pts[p]):.1f}" r="3.5" fill="{color}"/>')
            L.append(text(xf(i), yf(pts[p]) - 9, f"{pts[p]:.0f}", size=9, fill=color))
    legend(L, px0 + 336, py0 + 22, [(s[0], s[1]) for s in series])
    L.append("</svg>")
    return "\n".join(L)


# --- Figure 4: relaxation cost — expected rank error vs core count -------------------
def fig_relaxation_cost():
    cores = [4, 8, 16, 32, 64, 128]

    def rank_err(pc):
        n = 1 << (4 * pc - 1).bit_length()   # RoundUpPow2(4 * ProcessorCount)
        return (5 / 6) * n - 1 + 1 / (6 * n)

    errs = {c: rank_err(c) for c in cores}
    w, h = 760, 440
    px0, px1, py0, py1 = 80, 730, 70, 372
    ymax = 460.0

    def xf(i):
        return px0 + i * (px1 - px0) / (len(cores) - 1)

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    L = svg_open(w, h, "Relaxation cost — expected rank error vs core count")
    L.append(text(w / 2, 26, "The relaxation cost — expected dequeue rank error scales with the machine",
                  size=15, weight="bold"))
    L.append(text(w / 2, 45, "Rank error ~ (5/6)·n, n = RoundUpPow2(4 × ProcessorCount) · the quality price for the scalability above",
                  size=11))
    for t in range(0, 461, 100):
        L.append(f'<line x1="{px0}" y1="{yf(t):.1f}" x2="{px1}" y2="{yf(t):.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(px0 - 8, yf(t) + 4, str(t), size=11, anchor="end"))
    vlabel(L, 22, (py0 + py1) / 2, "expected rank error")
    for i, c in enumerate(cores):
        L.append(text(xf(i), py1 + 18, str(c), size=11))
    L.append(text((px0 + px1) / 2, py1 + 38, "ProcessorCount", size=12))
    poly = " ".join(f"{xf(i):.1f},{yf(errs[c]):.1f}" for i, c in enumerate(cores))
    L.append(f'<polyline points="{poly}" fill="none" stroke="{ORANGE}" stroke-width="2.5"/>')
    for i, c in enumerate(cores):
        host = c == 64
        L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(errs[c]):.1f}" r="{5 if host else 3.5}" '
                 f'fill="{RED if host else ORANGE}"/>')
        L.append(text(xf(i), yf(errs[c]) - 9, f"{errs[c]:.0f}", size=10,
                      fill=RED if host else ORANGE, weight="bold" if host else "normal"))
    hi = cores.index(64)
    L.append(text(xf(hi), yf(errs[64]) + 22, "D64s_v6 (n=256)", size=11, fill=RED, weight="bold"))
    L.append("</svg>")
    return "\n".join(L)


# --- near-linearity: per-doubling multiplier vs ideal 2x ----------------------------
def fig_near_linear(tp):
    pts = tp[("MultiQueueRelaxed", "UniformMixed5050")]
    pairs = list(zip(THREADS, THREADS[1:]))
    mult = [(a, b, pts[b] / pts[a]) for a, b in pairs]
    w, h = 760, 440
    px0, px1, py0, py1 = 74, 730, 72, 372
    ymax = 2.2
    bw = (px1 - px0) / len(mult) * 0.62

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    def xc(i):
        return px0 + (i + 0.5) * (px1 - px0) / len(mult)

    L = svg_open(w, h, "Near-linearity — per-doubling multiplier")
    L.append(text(w / 2, 26, "How near-linear? Per-doubling throughput multiplier — UniformMixed5050",
                  size=15, weight="bold"))
    L.append(text(w / 2, 45, "A clean ~84% near-linear region at 4–16T, degrading gracefully to 1.44× by 64T",
                  size=11))
    for t in (0, 0.5, 1.0, 1.5, 2.0):
        L.append(text(px0 - 8, yf(t) + 4, f"{t:.1f}×", size=11, anchor="end"))
    L.append(f'<line x1="{px0}" y1="{yf(2.0):.1f}" x2="{px1}" y2="{yf(2.0):.1f}" stroke="{GREEN}" '
             f'stroke-width="1.5" stroke-dasharray="6 4"/>')
    L.append(text(px1 - 4, yf(2.0) - 6, "ideal linear (2×)", size=10, fill=GREEN, anchor="end"))
    L.append(f'<line x1="{px0}" y1="{yf(1.0):.1f}" x2="{px1}" y2="{yf(1.0):.1f}" stroke="{FG}" '
             f'stroke-opacity="0.5" stroke-width="1" stroke-dasharray="2 3"/>')
    L.append(text(px1 - 4, yf(1.0) - 6, "break-even (1×)", size=10, fill=FG, anchor="end"))
    for i, (a, b, m) in enumerate(mult):
        color = RED if m < 1.0 else ORANGE
        x = xc(i) - bw / 2
        L.append(f'<rect x="{x:.1f}" y="{yf(m):.1f}" width="{bw:.1f}" height="{py1 - yf(m):.1f}" '
                 f'rx="3" fill="{color}"/>')
        L.append(text(xc(i), yf(m) - 7, f"{m:.2f}×", size=11, fill=color, weight="bold"))
        L.append(text(xc(i), py1 + 18, f"{a}–{b}", size=10))
    L.append(text((px0 + px1) / 2, py1 + 38, "thread-count doubling", size=12))
    L.append("</svg>")
    return "\n".join(L)


# --- stickiness ladder --------------------------------------------------------------
def fig_stickiness(st):
    w, h = 760, 440
    px0, px1, py0, py1 = 74, 730, 70, 372
    s_levels = [1, 2, 4, 8]
    ymax = 24.0

    def xf(i):
        return px0 + i * (px1 - px0) / (len(s_levels) - 1)

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    L = svg_open(w, h, "Stickiness ladder at 2 threads")
    L.append(text(w / 2, 26, "Stickiness ladder — the remedy for the 1–2T fixed-overhead loss", size=15, weight="bold"))
    L.append(text(w / 2, 45, "MultiQueueRelaxed, 2 threads · reusing sampled sub-queues for s ops trades rank error for locality",
                  size=11))
    for t in range(0, 25, 4):
        L.append(f'<line x1="{px0}" y1="{yf(t):.1f}" x2="{px1}" y2="{yf(t):.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(px0 - 8, yf(t) + 4, str(t), size=11, anchor="end"))
    vlabel(L, 22, (py0 + py1) / 2, "M ops/s")
    for i, s in enumerate(s_levels):
        L.append(text(xf(i), py1 + 18, f"s={s}", size=11))
    L.append(text((px0 + px1) / 2, py1 + 38, "stickiness", size=12))
    for wl in WORKLOADS:
        pts = st[(wl, 2)]
        poly = " ".join(f"{xf(i):.1f},{yf(pts[s]):.1f}" for i, s in enumerate(s_levels))
        L.append(f'<polyline points="{poly}" fill="none" stroke="{WL_COLOR[wl]}" stroke-width="2.5"/>')
        for i, s in enumerate(s_levels):
            L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(pts[s]):.1f}" r="3.5" fill="{WL_COLOR[wl]}"/>')
    legend(L, px0 + 14, py0 + 14, [(WL_LABEL[wl], WL_COLOR[wl]) for wl in WORKLOADS])
    L.append("</svg>")
    return "\n".join(L)


# --- stickiness in the low-thread regime: relaxed (s=1..8, 1T & 2T) vs Naive references ---
def _sticky_panel(L, x0, x1, y0, y1, wl, r1, r2, naive1, naive2, ymax, s_levels):
    def xf(i):
        return x0 + i * (x1 - x0) / (len(s_levels) - 1)

    def yf(v):
        return y1 - (min(v, ymax) / ymax) * (y1 - y0)

    L.append(text((x0 + x1) / 2, y0 - 12, WL_LABEL[wl], size=13, weight="bold", fill="#c9d1d9"))
    for t in (0, 8, 16, 24):
        L.append(f'<line x1="{x0}" y1="{yf(t):.1f}" x2="{x1}" y2="{yf(t):.1f}" '
                 f'stroke="{GRID}" stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(x0 - 6, yf(t) + 4, str(t), size=10, anchor="end"))
    for i, s in enumerate(s_levels):
        L.append(text(xf(i), y1 + 16, f"s={s}", size=10))
    L.append(text((x0 + x1) / 2, y1 + 33, "stickiness", size=10))
    L.append(text(x0 - 30, (y0 + y1) / 2, "M ops/s", size=10))
    # Naive references (horizontal — independent of s)
    L.append(f'<line x1="{x0}" y1="{yf(naive2):.1f}" x2="{x1}" y2="{yf(naive2):.1f}" '
             f'stroke="{BLUE}" stroke-width="1.8" stroke-dasharray="6 4"/>')
    L.append(text(x1 - 4, yf(naive2) - 5, "Naive 2T", size=10, fill=BLUE, anchor="end"))
    if naive1 <= ymax:
        L.append(f'<line x1="{x0}" y1="{yf(naive1):.1f}" x2="{x1}" y2="{yf(naive1):.1f}" '
                 f'stroke="{BLUE}" stroke-opacity="0.5" stroke-width="1.4" stroke-dasharray="2 3"/>')
        L.append(text(x1 - 4, yf(naive1) - 5, "Naive 1T", size=10, fill=BLUE, anchor="end"))
    else:
        L.append(text(x1 - 4, y0 + 12, f"Naive 1T = {naive1:.0f} (above)", size=10, fill=BLUE, anchor="end"))
    # relaxed 1 thread (dashed), then 2 threads (solid hero)
    p1 = " ".join(f"{xf(i):.1f},{yf(r1[s]):.1f}" for i, s in enumerate(s_levels))
    L.append(f'<polyline points="{p1}" fill="none" stroke="{ORANGE}" stroke-width="2" '
             f'stroke-dasharray="5 4" opacity="0.75"/>')
    for i, s in enumerate(s_levels):
        L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(r1[s]):.1f}" r="2.5" fill="{ORANGE}" opacity="0.75"/>')
    p2 = " ".join(f"{xf(i):.1f},{yf(r2[s]):.1f}" for i, s in enumerate(s_levels))
    L.append(f'<polyline points="{p2}" fill="none" stroke="{ORANGE}" stroke-width="2.5"/>')
    for i, s in enumerate(s_levels):
        L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(r2[s]):.1f}" r="3" fill="{ORANGE}"/>')


def fig_stickiness_lowthread(tp, st):
    w, h = 920, 700
    L = svg_open(w, h, "Stickiness in the low-thread regime")
    L.append(text(w / 2, 26, "Stickiness in the low-thread regime — relaxed (s = 1..8) vs Naive single-lock",
                  size=16, weight="bold"))
    L.append(text(w / 2, 45, "Threads 1 and 2 only (the regime the stickiness sweep covers) · raising s lifts relaxed past Naive at 2 threads",
                  size=11))
    legend_lines(L, 250, 66, [("relaxed, 2 threads", ORANGE, ""),
                              ("relaxed, 1 thread", ORANGE, "5 4"),
                              ("Naive, 2 threads", BLUE, "6 4")])
    s_levels = [1, 2, 4, 8]
    ymax = 24.0
    cells = [(70, 430, 110, 340), (510, 870, 110, 340),
             (70, 430, 410, 640), (510, 870, 410, 640)]
    for wl, (x0, x1, y0, y1) in zip(WORKLOADS, cells):
        _sticky_panel(L, x0, x1, y0, y1, wl, st[(wl, 1)], st[(wl, 2)],
                      tp[("LockingBaseline", wl)][1], tp[("LockingBaseline", wl)][2], ymax, s_levels)
    L.append("</svg>")
    return "\n".join(L)


def main():
    tp = load_throughput(HERE / "throughput.csv")
    st = load_stickiness(HERE / "throughput-stickiness.csv")
    bdn = load_bdn(HERE / "cpq-singlethreaded-latency-report.csv")
    charts = {
        "chart-xeon-scalability.svg": fig_scalability(tp),
        "chart-xeon-speedup.svg": fig_speedup(tp),
        "chart-xeon-latency.svg": fig_latency(bdn),
        "chart-xeon-relaxation-cost.svg": fig_relaxation_cost(),
        "chart-xeon-near-linear.svg": fig_near_linear(tp),
        "chart-xeon-stickiness.svg": fig_stickiness(st),
        "chart-xeon-stickiness-lowthread.svg": fig_stickiness_lowthread(tp, st),
    }
    for name, svg in charts.items():
        (HERE / name).write_text(svg + "\n", encoding="utf-8")
        print(f"wrote {name}")


if __name__ == "__main__":
    main()
