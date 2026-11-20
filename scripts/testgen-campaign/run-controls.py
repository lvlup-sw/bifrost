#!/usr/bin/env python3
"""Fault-control + replay driver for the issue #111 testgen campaign.

For each control: apply a reviewed fault patch, require the native suite to
fail at the intended oracle (a TESTGEN_REPLAY record, not a build error),
build a replay suite at a fresh target with the fault still applied, require
the same signature, restore production source, and require the replay plus
the full suite to pass. Production source and generated replay files are
restored/removed in `finally`. Do not run concurrently with other builds.

Usage:
  python3 scripts/testgen-campaign/run-controls.py \
      --testgen /abs/path/to/testgen --output /abs/external/output \
      [--only fifo,advance,...]
"""
import argparse, json, re, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FAULTS = ROOT / "docs/testing/evidence/testgen-campaign/faults"

CONTROLS = {
    "fifo": dict(
        fault="fifo-drop-extra.patch", project="src/Bifrost.Tests",
        request="src/Bifrost.Tests/Systematic/Testgen/Campaign/fifo-property.testgen.json",
        cls="FifoPropertyTests", replay_cls="FifoReplayTests",
        replay_path="src/Bifrost.Tests/Systematic/Testgen/Campaign/FifoReplayTests.cs"),
    "translate": dict(
        fault="coalesce-earliest.patch", project="src/Bifrost.Tests.Scheduling",
        request="src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-translate.testgen.json",
        cls="CoalesceTranslateTests", replay_cls="CoalesceTranslateReplayTests",
        replay_path="src/Bifrost.Tests.Scheduling/Testgen/Campaign/CoalesceTranslateReplayTests.cs",
        expect_fail=False),  # translation alone cannot detect earliest-selection
    "advance": dict(
        fault="coalesce-earliest.patch", project="src/Bifrost.Tests.Scheduling",
        request="src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-advance.testgen.json",
        cls="CoalesceAdvanceTests", replay_cls="CoalesceAdvanceReplayTests",
        replay_path="src/Bifrost.Tests.Scheduling/Testgen/Campaign/CoalesceAdvanceReplayTests.cs"),
    "differential": dict(
        fault="coalesce-earliest.patch", project="src/Bifrost.Tests.Scheduling",
        request="src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-differential.testgen.json",
        cls="CoalesceDifferentialTests", replay_cls="CoalesceDifferentialReplayTests",
        replay_path="src/Bifrost.Tests.Scheduling/Testgen/Campaign/CoalesceDifferentialReplayTests.cs"),
    "differential-exclusive": dict(
        fault="coalesce-exclusive.patch", project="src/Bifrost.Tests.Scheduling",
        request="src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-differential.testgen.json",
        cls="CoalesceDifferentialTests", replay_cls="CoalesceDifferentialReplayTests",
        replay_path="src/Bifrost.Tests.Scheduling/Testgen/Campaign/CoalesceDifferentialReplayTests.cs"),
    "worker": dict(
        fault="worker-idle-count.patch", project="src/Bifrost.Tests",
        request="src/Bifrost.Tests/Systematic/Testgen/Campaign/worker-registry.testgen.json",
        cls="WorkerRegistryCampaignTests", replay_cls="WorkerRegistryReplayTests",
        replay_path="src/Bifrost.Tests/Systematic/Testgen/Campaign/WorkerRegistryReplayTests.cs"),
}


def run(cmd, timeout=300):
    return subprocess.run(cmd, cwd=ROOT, text=True, capture_output=True, timeout=timeout)


def native(project, cls, out, name):
    r = run(["dotnet", "run", "--project", project, "-c", "Release", "--",
             "--treenode-filter", f"/*/*/{cls}/*", "--minimum-expected-tests", "1"])
    out.joinpath(name).write_text(r.stdout + r.stderr)
    return r


def records(text):
    return [json.loads(m) for m in re.findall(r"TESTGEN_REPLAY (\{[^\n]+\})", text)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--testgen", required=True)
    ap.add_argument("--output", type=Path, required=True)
    ap.add_argument("--only", default="")
    args = ap.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    testgen = [args.testgen] if not args.testgen.endswith(".js") else ["node", args.testgen]
    only = set(filter(None, args.only.split(","))) or set(CONTROLS)
    summary = {}
    for name, c in CONTROLS.items():
        if name not in only:
            continue
        out = args.output / name
        out.mkdir(exist_ok=True)
        fault = FAULTS / c["fault"]
        replay_file = ROOT / c["replay_path"]
        req_copy = out / "replay-request.json"
        try:
            assert run(["git", "apply", str(fault)]).returncode == 0
            failed = native(c["project"], c["cls"], out, "fault.log")
            text = out.joinpath("fault.log").read_text()
            recs = records(text)
            if c.get("expect_fail") is False:
                # Survivor control: suite must pass and emit no oracle replay.
                assert failed.returncode == 0 and not recs, (failed.returncode, recs[:1])
                summary[name] = {"outcome": "survivor", "suiteExit": failed.returncode}
                print(name, json.dumps(summary[name])); continue
            assert failed.returncode != 0 and recs, (failed.returncode, text[-2000:])
            rec = recs[0]
            assert "oracle" in rec["signature"] or "state-mismatch" in rec["signature"], rec
            # Replay at a fresh target, fault still applied.
            req = json.loads((ROOT / c["request"]).read_text())
            req["target"]["path"] = c["replay_path"]; req["target"]["className"] = c["replay_cls"]
            req_copy.write_text(json.dumps(req))
            fixture = out / "replay.json"; fixture.write_text(json.dumps(rec))
            rep = run([*testgen, "replay", "--input", str(req_copy), "--fixture", str(fixture),
                       "--case", rec["caseId"]])
            assert rep.returncode == 0, rep.stderr
            applied = subprocess.run(["git", "apply"], cwd=ROOT, input=rep.stdout,
                                     text=True, capture_output=True)
            assert applied.returncode == 0, applied.stderr
            replayed = native(c["project"], c["replay_cls"], out, "replay-fault.log")
            rrecs = records(out.joinpath("replay-fault.log").read_text())
            assert replayed.returncode != 0 and rrecs and rrecs[0]["signature"] == rec["signature"], \
                (replayed.returncode, rrecs[:1], rec)
            summary[name] = {"outcome": "killed+replayed", "signature": rec["signature"],
                             "caseId": rec["caseId"], "cases": rec.get("cases") or rec.get("histories"),
                             "reduction": rec.get("reduction")}
        finally:
            run(["git", "apply", "-R", str(fault)])
            if replay_file.exists():
                replay_file.unlink()
        # Restored baseline: replay suite was removed, original suite must pass.
        restored = native(c["project"], c["cls"], out, "restored.log")
        assert restored.returncode == 0, out.joinpath("restored.log").read_text()[-2000:]
        summary[name] = {**summary.get(name, {}), "restoredExit": restored.returncode}
        print(name, json.dumps(summary[name]))
    (args.output / "controls-summary.json").write_text(json.dumps(summary, indent=2))
    print("OK", json.dumps(summary))


if __name__ == "__main__":
    main()
