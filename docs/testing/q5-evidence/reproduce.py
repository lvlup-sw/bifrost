import argparse
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser(description="Build and exercise Q5 production mutations, then restore and verify.")
parser.add_argument("--output", default="work/q5-reproduced")
args = parser.parse_args()
output = (root / args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
source = root / "src/Bifrost.Concurrency/ConcurrentPriorityQueue.Dequeue.cs"
original = source.read_text()
project = "src/Bifrost.Tests.Concurrency/Bifrost.Tests.Concurrency.csproj"
dll = "src/Bifrost.Tests.Concurrency/bin/Release/net10.0/Bifrost.Tests.Concurrency.dll"
summary = []


def command(name, arguments):
    result = subprocess.run(arguments, cwd=root, capture_output=True, text=True, timeout=120)
    (output / f"{name}.log").write_text(result.stdout + result.stderr)
    return result


def build(name):
    result = command(name + "-build", ["dotnet", "build", project, "-c", "Release"])
    if result.returncode:
        raise RuntimeError(f"{name}: build failed; not a mutation kill")


def test(name, mutant=False, full=False):
    report = output / f"{name}.trx"
    report.unlink(missing_ok=True)
    arguments = ["dotnet", dll, "--minimum-expected-tests", "160" if full else "4", "--no-ansi",
                 "--report-trx", "--report-trx-filename", report.name, "--results-directory", str(output)]
    if not full:
        arguments += ["--treenode-filter", "/*/*/ObservedEmptyInvariantTests/*"]
    result = command(name, arguments)
    if not report.exists():
        raise RuntimeError(f"{name}: no test report; not a mutation kill")
    tree = ET.parse(report)
    counters = tree.find(".//{*}Counters").attrib
    results = tree.findall(".//{*}UnitTestResult")
    failures = {x.attrib["testName"]: " ".join(x.itertext()) for x in results if x.attrib["outcome"] == "Failed"}
    row = {"name": name, "exit": result.returncode, "counters": counters, "failedTests": list(failures)}
    summary.append(row)
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    executed = int(counters["executed"])
    if executed < (160 if full else 4):
        raise RuntimeError(f"{name}: insufficient discovery ({executed}); not a mutation kill")
    if mutant:
        witnesses = {
            "Q5HeldSparseLastItemRequiresRetryAndThenSuccess": "Q5 held populated stripe was treated as empty",
            "Q5ProducerComparerOverlapsConsumerWithoutPermittingFalse": "Q5 overlapping producer and consumer cannot erase the resident prefill",
        }
        if result.returncode != 2:
            raise RuntimeError(f"{name}: expected test assertion failure exit 2, got {result.returncode}")
        for test_name, message in witnesses.items():
            if not any(test_name in key and message in value for key, value in failures.items()):
                raise RuntimeError(f"{name}: missing completed false-result assertion: {test_name}; not a kill")
    elif result.returncode or int(counters["failed"]) or int(counters["passed"]) != executed:
        raise RuntimeError(f"{name}: baseline/restored tests did not all pass")
    print(f"{name}: exit={result.returncode} executed={executed} failed={counters['failed']}", flush=True)


scan_call = "return TryDequeueVerificationScan(out element, out priority);"
retry = "passVoided = true;\n                break;"
if original.count(scan_call) != 2 or original.count(retry) != 1:
    raise RuntimeError("Production mutation anchors changed; inspect source before updating the harness")
mutants = {
    "bypass-verification": original.replace(scan_call, "element = default;\n        priority = default;\n        return false;"),
    "contention-is-empty": original.replace(retry, "emptyObserved++;\n                continue;"),
}
build("baseline")
test("baseline")
try:
    for name, text in mutants.items():
        source.write_text(text)
        build(name)
        test(name, mutant=True)
finally:
    source.write_text(original)
    build("restored")
    test("restored")
    test("full-concurrency", full=True)
