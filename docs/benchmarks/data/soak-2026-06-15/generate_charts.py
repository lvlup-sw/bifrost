#!/usr/bin/env python3
"""Generate the consumer-shaped soak (DR-8) figure SVGs from the release-gate CSVs.

Reads the sibling soak-classes.csv / soak-runs.csv (the artifacts the `soak` verb writes) and
emits the queue-wait, fairness, and starvation-bound figures referenced by
docs/benchmarks/2026-06-cpq-soak.md. Pure stdlib — no matplotlib. Style mirrors the sibling
azure-d64sv6-2026-06-13/generate_charts.py (GitHub palette, transparent background); colours
follow the Bifrost convention so these sit beside BACKGROUND.md's charts: the locking binding
is BLUE, the relaxed MultiQueue is ORANGE.

    python3 docs/benchmarks/data/soak-2026-06-15/generate_charts.py
"""

import csv
from pathlib import Path

HERE = Path(__file__).parent

FG = "#8b949e"
GRID = "#8b949e"
BLUE = "#58a6ff"      # LockingPriority (exact min-key)
ORANGE = "#f0883e"    # MultiQueuePriority (relaxed two-choice)
GRAY = "#8b949e"      # offered share / reference
RED = "#f85149"       # bound marker / exceeded
GREEN = "#3fb950"     # held
FONT = "ui-sans-serif, system-ui, sans-serif"

LOCKING = "LockingPriority"
MQ = "MultiQueuePriority"
CLASSES = ["Interactive", "Default", "Batch"]
WORKERS = [2, 8]


# --- svg helpers (mirror the repo generator) ----------------------------------------
def svg_open(w, h, title):
    return [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" '
            f'font-family="{FONT}" role="img" aria-label="{_esc(title)}">']


def _esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def text(x, y, s, size=12, anchor="middle", fill=FG, weight="normal"):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" text-anchor="{anchor}" '
            f'fill="{fill}" font-weight="{weight}">{_esc(s)}</text>')


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


def _nice_ceiling(v):
    """Round an axis max up to a clean 1/2/5 × 10^k step."""
    if v <= 0:
        return 1.0
    import math
    mag = 10 ** math.floor(math.log10(v))
    for m in (1, 2, 2.5, 5, 10):
        if v <= m * mag:
            return m * mag
    return 10 * mag


# --- data loading -------------------------------------------------------------------
def _rows(path):
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(ln for ln in f if not ln.startswith("#")))


def load_classes(path):
    """{(Binding, Workers, Class): row-dict-of-floats}."""
    out = {}
    for r in _rows(path):
        out[(r["Binding"], int(r["Workers"]), r["Class"])] = {
            "offered": int(r["Offered"]),
            "rejected": int(r["Rejected"]),
            "dispatched": int(r["Dispatched"]),
            "off_pct": float(r["OfferedSharePct"]),
            "disp_pct": float(r["DispatchSharePct"]),
            "p50": float(r["WaitP50Ms"]),
            "p95": float(r["WaitP95Ms"]),
            "p99": float(r["WaitP99Ms"]),
            "max": float(r["WaitMaxMs"]),
        }
    return out


def load_runs(path):
    """{(Binding, Workers): row-dict}."""
    out = {}
    for r in _rows(path):
        out[(r["Binding"], int(r["Workers"]))] = {
            "occ_mean": float(r["MeanOccupancyPct"]),
            "in_band": float(r["InBandPct"]),
            "alloc_mean_mib": float(r["AllocBytesPerMinMean"]) / 1048576.0,
            "gen0": int(r["Gen0"]), "gen1": int(r["Gen1"]), "gen2": int(r["Gen2"]),
            "max_batch_wait": float(r["MaxBatchWaitSeconds"]),
            "bound": float(r["DepthAdjustedBoundSeconds"]),
            "held": r["StarvationBoundHeld"].strip().lower() == "true",
        }
    return out


# --- grouped-bar panel --------------------------------------------------------------
def _grouped_bars(L, x0, x1, y0, y1, title, ymax, groups, series, unit, fmt="{:.0f}"):
    """groups: list of category labels (x). series: list of (label, color, [val per group])."""
    n_g = len(groups)
    n_s = len(series)
    gw = (x1 - x0) / n_g
    bw = gw * 0.74 / n_s

    def yf(v):
        return y1 - (min(v, ymax) / ymax) * (y1 - y0)

    if title:
        L.append(text((x0 + x1) / 2, y0 - 12, title, size=13, weight="bold", fill="#c9d1d9"))
    # y gridlines
    steps = 5
    for k in range(steps + 1):
        gv = ymax * k / steps
        L.append(f'<line x1="{x0}" y1="{yf(gv):.1f}" x2="{x1}" y2="{yf(gv):.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(x0 - 6, yf(gv) + 4, fmt.format(gv), size=10, anchor="end"))
    L.append(text(x0 - 44, (y0 + y1) / 2, unit, size=10))
    for gi, g in enumerate(groups):
        gx = x0 + gi * gw
        L.append(text(gx + gw / 2, y1 + 16, g, size=11))
        for si, (label, color, vals) in enumerate(series):
            bx = gx + gw * 0.13 + si * bw
            v = vals[gi]
            L.append(f'<rect x="{bx:.1f}" y="{yf(v):.1f}" width="{bw * 0.9:.1f}" '
                     f'height="{y1 - yf(v):.1f}" rx="2" fill="{color}"/>')
            L.append(text(bx + bw * 0.45, yf(v) - 5, fmt.format(v), size=9, fill=color, weight="bold"))


# --- Figure 1: per-class p95 queue-wait, locking vs MultiQueue (panels W=2, W=8) -----
def fig_queue_wait(cls):
    w, h = 920, 460
    L = svg_open(w, h, "Per-class p95 queue-wait — locking vs MultiQueue")
    L.append(text(w / 2, 26, "Per-class p95 queue-wait — locking vs MultiQueue", size=16, weight="bold"))
    L.append(text(w / 2, 45, "600 s consumer-shaped soak · lower is better · the exact min-key binding "
                 "separates the classes the relaxed dequeue washes out", size=11))
    legend_row(L, 320, 66, [("LockingPriority", BLUE), ("MultiQueuePriority", ORANGE)])
    cells = [(76, 446, 110, 392), (520, 890, 110, 392)]
    for wk, (x0, x1, y0, y1) in zip(WORKERS, cells):
        # Each worker count is a distinct operating regime (2w runs saturated, 8w healthy), so
        # scale each panel to its own max — the within-panel binding gap is the comparison, not
        # absolute height across panels (a shared axis squashes the 8-worker story flat).
        ymax = _nice_ceiling(max(cls[(b, wk, c)]["p95"] for b in (LOCKING, MQ) for c in CLASSES) / 1000.0)
        _grouped_bars(L, x0, x1, y0, y1, f"{wk} workers", ymax, CLASSES, [
            ("LockingPriority", BLUE, [cls[(LOCKING, wk, c)]["p95"] / 1000.0 for c in CLASSES]),
            ("MultiQueuePriority", ORANGE, [cls[(MQ, wk, c)]["p95"] / 1000.0 for c in CLASSES]),
        ], "p95 wait (s)", fmt="{:.1f}")
    L.append("</svg>")
    return "\n".join(L)


# --- Figure 2: dispatch fairness — offered share vs dispatch share -------------------
def fig_fairness(cls):
    w, h = 920, 460
    L = svg_open(w, h, "Dispatch fairness — offered share vs dispatch share")
    L.append(text(w / 2, 26, "Dispatch fairness — offered share vs dispatch share by class", size=16, weight="bold"))
    L.append(text(w / 2, 45, "The boost redistributes dispatch from Batch/Default toward Interactive — "
                 "more sharply under the exact-ordering locking binding", size=11))
    legend_row(L, 210, 66, [("offered %", GRAY), ("LockingPriority dispatch %", BLUE),
                            ("MultiQueuePriority dispatch %", ORANGE)])
    ymax = 100.0
    cells = [(76, 446, 110, 392), (520, 890, 110, 392)]
    for wk, (x0, x1, y0, y1) in zip(WORKERS, cells):
        _grouped_bars(L, x0, x1, y0, y1, f"{wk} workers", ymax, CLASSES, [
            ("offered", GRAY, [cls[(LOCKING, wk, c)]["off_pct"] for c in CLASSES]),
            ("locking", BLUE, [cls[(LOCKING, wk, c)]["disp_pct"] for c in CLASSES]),
            ("mq", ORANGE, [cls[(MQ, wk, c)]["disp_pct"] for c in CLASSES]),
        ], "share (%)", fmt="{:.0f}")
    L.append("</svg>")
    return "\n".join(L)


# --- Figure 3: starvation bound — max batch wait vs depth-adjusted ceiling -----------
def fig_starvation(runs):
    w, h = 760, 460
    px0, px1, py0, py1 = 80, 700, 96, 392
    order = [(LOCKING, 2), (LOCKING, 8), (MQ, 2), (MQ, 8)]
    labels = ["Lock\n2w", "Lock\n8w", "MQ\n2w", "MQ\n8w"]
    ymax = _nice_ceiling(max(max(runs[k]["bound"], runs[k]["max_batch_wait"]) for k in order))

    def yf(v):
        return py1 - (min(v, ymax) / ymax) * (py1 - py0)

    L = svg_open(w, h, "Starvation bound — max batch wait vs depth-adjusted ceiling")
    L.append(text(w / 2, 26, "Starvation bound — max Batch wait vs the depth-adjusted ceiling", size=15, weight="bold"))
    L.append(text(w / 2, 45, "Each bar is the worst Batch wait observed; the tick is its depth-adjusted "
                 "ceiling boostWindow + capacity·meanWork/workers", size=11))
    legend_row(L, 230, 66, [("max Batch wait", GREEN), ("depth-adjusted bound", RED)])
    for k in range(6):
        gv = ymax * k / 5
        L.append(f'<line x1="{px0}" y1="{yf(gv):.1f}" x2="{px1}" y2="{yf(gv):.1f}" stroke="{GRID}" '
                 f'stroke-opacity="0.2" stroke-width="1"/>')
        L.append(text(px0 - 6, yf(gv) + 4, f"{gv:.0f}", size=10, anchor="end"))
    vlabel(L, 24, (py0 + py1) / 2, "seconds")
    gw = (px1 - px0) / len(order)
    for i, (k, lab) in enumerate(zip(order, labels)):
        r = runs[k]
        gx = px0 + i * gw
        bw = gw * 0.5
        bx = gx + gw / 2 - bw / 2
        L.append(f'<rect x="{bx:.1f}" y="{yf(r["max_batch_wait"]):.1f}" width="{bw:.1f}" '
                 f'height="{py1 - yf(r["max_batch_wait"]):.1f}" rx="3" fill="{GREEN}"/>')
        L.append(text(gx + gw / 2, yf(r["max_batch_wait"]) - 6, f'{r["max_batch_wait"]:.0f}s',
                      size=10, fill=GREEN, weight="bold"))
        # bound tick
        L.append(f'<line x1="{bx - 6:.1f}" y1="{yf(r["bound"]):.1f}" x2="{bx + bw + 6:.1f}" '
                 f'y2="{yf(r["bound"]):.1f}" stroke="{RED}" stroke-width="2.5"/>')
        L.append(text(gx + gw / 2, yf(r["bound"]) - 6, f'{r["bound"]:.0f}s', size=10, fill=RED))
        for j, line in enumerate(lab.split("\n")):
            L.append(text(gx + gw / 2, py1 + 18 + j * 14, line, size=11))
        held = "HELD" if r["held"] else "EXCEEDED"
        L.append(text(gx + gw / 2, py1 + 46, held, size=10, fill=GREEN if r["held"] else RED, weight="bold"))
    L.append("</svg>")
    return "\n".join(L)


def main():
    cls = load_classes(HERE / "soak-classes.csv")
    runs = load_runs(HERE / "soak-runs.csv")
    charts = {
        "chart-soak-queue-wait.svg": fig_queue_wait(cls),
        "chart-soak-fairness.svg": fig_fairness(cls),
        "chart-soak-starvation.svg": fig_starvation(runs),
    }
    for name, svg in charts.items():
        (HERE / name).write_text(svg + "\n", encoding="utf-8")
        print(f"wrote {name}")


if __name__ == "__main__":
    main()
