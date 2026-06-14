#!/usr/bin/env python3
"""Generate the benchmark figure SVGs for src/Bifrost.Concurrency/BACKGROUND.md.

Pure stdlib — no matplotlib. Data values are transcribed from the committed benchmark
documents (sources cited per chart below); re-run after updating those docs:

    python3 src/Bifrost.Concurrency/diagrams/generate_charts.py

Colors follow the GitHub palette midtones so the transparent-background charts stay
readable on both light and dark themes.
"""

from pathlib import Path

OUT = Path(__file__).parent

FG = "#8b949e"        # axis/labels — GitHub muted gray, readable on light + dark
GRID = "#8b949e"
BLUE = "#58a6ff"      # Locking binding
ORANGE = "#f0883e"    # MultiQueue binding
GRAY = "#8b949e"      # baseline series
RED = "#f85149"       # regression series
GREEN = "#3fb950"     # fixed series
FONT = "ui-sans-serif, system-ui, sans-serif"


def svg_open(w, h, title):
    return [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" '
        f'font-family="{FONT}" role="img" aria-label="{title}">',
    ]


def text(x, y, s, size=12, anchor="middle", fill=FG, weight="normal"):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" text-anchor="{anchor}" '
            f'fill="{fill}" font-weight="{weight}">{s}</text>')


def y_axis(lines, x0, x1, y_for, ticks, fmt=str):
    for t in ticks:
        y = y_for(t)
        lines.append(f'<line x1="{x0}" y1="{y:.1f}" x2="{x1}" y2="{y:.1f}" '
                     f'stroke="{GRID}" stroke-opacity="0.25" stroke-width="1"/>')
        lines.append(text(x0 - 8, y + 4, fmt(t), size=11, anchor="end"))


def legend(lines, x, y, entries):
    for i, (label, color) in enumerate(entries):
        yy = y + i * 20
        lines.append(f'<rect x="{x}" y="{yy - 9}" width="12" height="12" rx="2" fill="{color}"/>')
        lines.append(text(x + 18, yy + 1, label, size=12, anchor="start"))


def chart_throughput():
    """Contended throughput, UniformMixed5050 (fixed-window harness, 0.5 s windows).

    Source: docs/benchmarks/2026-06-cpq-port-parity.md §3 (Bifrost port columns).
    """
    threads = [1, 4, 16, 32, 64]
    relaxed = [15.4, 35.4, 76.7, 82.2, 80.2]   # MultiQueueRelaxed, M ops/s
    locking = [22.2, 18.8, 17.4, 17.5, 12.7]   # LockingBaseline, M ops/s

    w, h = 720, 420
    px0, px1, py0, py1 = 70, 690, 56, 356      # plot rect (y grows downward)
    ymax = 90.0

    def xf(i):
        return px0 + i * (px1 - px0) / (len(threads) - 1)

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    L = svg_open(w, h, "Contended throughput: MultiQueue vs locking baseline")
    L.append(text(w / 2, 24, "Contended throughput — UniformMixed5050 (i9-13900K, 0.5 s windows)",
                  size=15, weight="bold"))
    L.append(text(w / 2, 42, "Bifrost port numbers · docs/benchmarks/2026-06-cpq-port-parity.md §3",
                  size=11))
    y_axis(L, px0, px1, yf, range(0, 91, 15), fmt=lambda t: f"{t}")
    L.append(text(24, (py0 + py1) / 2, "M ops/s", size=12))
    for i, t in enumerate(threads):
        L.append(text(xf(i), py1 + 18, str(t), size=11))
    L.append(text((px0 + px1) / 2, py1 + 38, "threads", size=12))

    for series, color in ((relaxed, ORANGE), (locking, BLUE)):
        pts = " ".join(f"{xf(i):.1f},{yf(v):.1f}" for i, v in enumerate(series))
        L.append(f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="2.5"/>')
        for i, v in enumerate(series):
            L.append(f'<circle cx="{xf(i):.1f}" cy="{yf(v):.1f}" r="4" fill="{color}"/>')
            dy = -10 if series is relaxed else 18
            L.append(text(xf(i), yf(v) + dy, f"{v:g}", size=10, fill=color))

    legend(L, px0 + 14, py0 + 16, [
        ("ConcurrentPriorityQueue (relaxed two-choice)", ORANGE),
        ("LockingPriorityQueue (exact, global lock)", BLUE),
    ])
    L.append("</svg>")
    return "\n".join(L)


def chart_soak_p95():
    """Interactive-class p95 queue wait from the consumer-shaped soak (45 s smoke runs).

    Source: docs/benchmarks/2026-06-cpq-soak.md per-class table (INDICATIVE).
    """
    groups = ["2 workers", "8 workers"]
    locking = [8.417, 1.780]     # seconds
    multiq = [30.964, 17.798]    # seconds

    w, h = 720, 400
    px0, px1, py0, py1 = 70, 690, 56, 330
    ymax = 35.0
    gw = (px1 - px0) / len(groups)
    bw = 90

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    L = svg_open(w, h, "Soak: interactive p95 queue wait by binding")
    L.append(text(w / 2, 24, "Interactive-class p95 queue wait — consumer-shaped soak (45 s smoke, INDICATIVE)",
                  size=15, weight="bold"))
    L.append(text(w / 2, 42, "Lower is better · docs/benchmarks/2026-06-cpq-soak.md", size=11))
    y_axis(L, px0, px1, yf, range(0, 36, 5), fmt=lambda t: f"{t}s")

    for g, (name, lv, mv) in enumerate(zip(groups, locking, multiq)):
        cx = px0 + gw * (g + 0.5)
        for off, v, color in ((-bw - 6, lv, BLUE), (6, mv, ORANGE)):
            x = cx + off
            L.append(f'<rect x="{x:.1f}" y="{yf(v):.1f}" width="{bw}" '
                     f'height="{py1 - yf(v):.1f}" rx="3" fill="{color}"/>')
            L.append(text(x + bw / 2, yf(v) - 8, f"{v:.1f}s", size=12, fill=color, weight="bold"))
        L.append(text(cx, py1 + 22, name, size=13))

    legend(L, px0 + 14, py0 + 16, [
        ("LockingPriorityWorkQueue", BLUE),
        ("ConcurrentPriorityWorkQueue", ORANGE),
    ])
    L.append(text(w / 2, h - 12,
                  "Seconds-long work items, capacity 128 — the low-contention regime where exact ordering wins",
                  size=11))
    L.append("</svg>")
    return "\n".join(L)


def chart_dr7_gate():
    """DR-7 FIFO no-regression gate: enqueue-to-dispatch round trip, 10k items (ShortRun means).

    Source: docs/benchmarks/2026-06-cpq-orchestrator-baseline.md (T1, T28 run 1, T28-fix run 1).
    """
    groups = ["1 worker", "2 workers", "8 workers"]
    baseline = [2.109, 3.130, 10.342]   # ms
    rewrite = [4.549, 5.961, 19.632]
    fixed = [2.956, 3.135, 12.275]

    w, h = 720, 400
    px0, px1, py0, py1 = 70, 690, 56, 330
    ymax = 22.0
    gw = (px1 - px0) / len(groups)
    bw = 52

    def yf(v):
        return py1 - (v / ymax) * (py1 - py0)

    L = svg_open(w, h, "DR-7 FIFO gate: baseline vs rewrite vs fix")
    L.append(text(w / 2, 24, "DR-7 FIFO no-regression gate — 10k-item round trip (ShortRun means)",
                  size=15, weight="bold"))
    L.append(text(w / 2, 42, "Lower is better · docs/benchmarks/2026-06-cpq-orchestrator-baseline.md", size=11))
    y_axis(L, px0, px1, yf, range(0, 23, 4), fmt=lambda t: f"{t}ms")

    for g, name in enumerate(groups):
        cx = px0 + gw * (g + 0.5)
        for off, v, color in ((-1.5 * bw - 8, baseline[g], GRAY),
                              (-0.5 * bw, rewrite[g], RED),
                              (0.5 * bw + 8, fixed[g], GREEN)):
            x = cx + off
            L.append(f'<rect x="{x:.1f}" y="{yf(v):.1f}" width="{bw}" '
                     f'height="{py1 - yf(v):.1f}" rx="3" fill="{color}"/>')
            L.append(text(x + bw / 2, yf(v) - 8, f"{v:.2f}", size=10, fill=color))
        L.append(text(cx, py1 + 22, name, size=13))

    legend(L, px0 + 14, py0 + 16, [
        ("Baseline before port (T1)", GRAY),
        ("After IWorkQueue rewrite (T28) — gate FAIL", RED),
        ("After T28-fix remediation — gate PASS", GREEN),
    ])
    L.append(text(w / 2, h - 12,
                  "T28-fix also restored the deterministic 0 B/op allocation profile at 1 worker (0 B in both runs)",
                  size=11))
    L.append("</svg>")
    return "\n".join(L)


CHARTS = {
    "chart-throughput-contended.svg": chart_throughput,
    "chart-soak-interactive-p95.svg": chart_soak_p95,
    "chart-dr7-fifo-gate.svg": chart_dr7_gate,
}

if __name__ == "__main__":
    for name, fn in CHARTS.items():
        path = OUT / name
        path.write_text(fn() + "\n", encoding="utf-8")
        print(f"wrote {path}")
