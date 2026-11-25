"""Check the test results that scripts/ci/test.sh and scripts/ci/stress.sh write.

  results.py trx FILE       the TRX run executed at least one test and every executed test passed
  results.py coverage DIR   every DIR/<project>/tests.trx passed, the invariant classes ran, and each
                            assembly in DIR/Cobertura.xml has at least 80% line and branch coverage
"""
import argparse
from collections import Counter
from decimal import Decimal
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

THRESHOLD = 80
SHIPPING_ASSEMBLIES = {
    'Bifrost', 'Bifrost.Concurrency', 'Bifrost.Core', 'Bifrost.HealthChecks', 'Bifrost.OpenTelemetry',
    'Bifrost.Resilience', 'Bifrost.Scheduling', 'Bifrost.Scheduling.Core', 'Bifrost.Scheduling.Testing',
}
# The fewest passing cases each invariant class must contribute to a coverage run. The
# classes match scripts/run-invariant-tests.py; a renamed or emptied class fails the gate.
INVARIANTS = {
    'Bifrost.Tests': {
        'HistoryToolsTests': 8, 'LifecycleInvariantTests': 9, 'LifecycleIdentityTests': 1,
        'LifecycleOutcomeInvariantTests': 3, 'PriorityScalingInvariantTests': 3,
        'PriorityVirtualTimePropertyTests': 113, 'AutoscalingClockInvariantTests': 3,
        'AutoscalingLifecycleInvariantTests': 1, 'WorkerRetirementTests': 18, 'HostedTeardownRegressionTests': 3,
    },
    'Bifrost.Tests.Concurrency': {
        'GeneratedQueueInvariantTests': 7, 'QueueReplayTests': 6, 'ObservedEmptyInvariantTests': 4,
        'ComparerClearRefillRegressionTests': 1,
    },
    'Bifrost.Tests.Scheduling': {
        'CronCoalesceTests': 5, 'JobMutationGatesTests': 3, 'CheckpointOrderingTests': 5,
        'IntervalInvariantTests': 13, 'IntervalDateBoundaryTests': 5, 'IntervalMutationAdequacyTests': 2,
        'SchedulerReplayTests': 2, 'SystematicRegistryTests': 6, 'SystematicRegistryRaceTests': 10,
        'SystematicRegistryRestartTests': 14, 'SystematicRegistryIdleBarrierTests': 1,
        'SystematicRegistryCheckpointRaceTests': 7, 'SystematicRegistryRestartReplayTests': 6,
    },
}
RATE = re.compile(r'0(?:\.[0-9]+)?|1(?:\.0+)?')


class GateError(Exception):
    pass


def parse(path, root):
    document = ET.parse(path).getroot()
    if document.tag.rsplit('}', 1)[-1] != root:
        raise GateError(f'{path}: expected a {root} document')
    return document


def count(value, path):
    if value is None or not value.isdigit():
        raise GateError(f'{path}: invalid counter {value!r}')
    return int(value)


def check_trx(path):
    run = parse(path, 'TestRun')
    counters = run.findall('./{*}ResultSummary/{*}Counters')
    if len(counters) != 1:
        raise GateError(f'{path}: expected one result summary')
    executed, passed, total, failed = (count(counters[0].get(name), path)
                                       for name in ('executed', 'passed', 'total', 'failed'))
    if not 0 < executed == passed <= total or failed:
        raise GateError(f'{path}: {passed} of {executed} executed tests passed and {failed} failed')
    print(f'{path}: {passed} tests passed')
    return run


def passed_by_class(run):
    passed = Counter(result.get('testId') for result in run.findall('./{*}Results/{*}UnitTestResult')
                     if result.get('outcome') == 'Passed')
    classes = Counter()
    for test in run.findall('./{*}TestDefinitions/{*}UnitTest'):
        method = test.find('./{*}TestMethod')
        if method is not None and test.get('id') in passed:
            classes[method.get('className', '').rsplit('.', 1)[-1]] += passed[test.get('id')]
    return classes


def packages(path):
    report = parse(path, 'coverage')
    rows = report.findall('./packages/package')
    names = [row.get('name') for row in rows]
    if not names or not all(names) or len(set(names)) != len(names):
        raise GateError(f'{path}: coverage needs uniquely named assemblies')
    valid, covered = count(report.get('lines-valid'), path), count(report.get('lines-covered'), path)
    if valid <= 0 or covered > valid:
        raise GateError(f'{path}: coverage has no measurable lines or invalid totals')
    return rows


def rate(row, metric):
    raw = row.get(metric, '')
    if not RATE.fullmatch(raw):
        raise GateError(f'{row.get("name")}: invalid {metric} {raw!r}')
    return Decimal(raw) * 100


def check_coverage(directory):
    projects = sorted(trx.parent for trx in directory.glob('*/tests.trx'))
    absent = sorted(set(INVARIANTS) - {project.name for project in projects})
    if absent:
        raise GateError('missing test results for ' + ', '.join(absent))
    for project in projects:
        classes = passed_by_class(check_trx(project / 'tests.trx'))
        packages(project / 'coverage.cobertura.xml')
        missing = sorted(name for name, least in INVARIANTS.get(project.name, {}).items() if classes[name] < least)
        if missing:
            raise GateError(f'{project.name} is missing required invariant cases: ' + ', '.join(missing))
    rows = packages(directory / 'Cobertura.xml')
    missing = sorted(SHIPPING_ASSEMBLIES - {row.get('name') for row in rows})
    if missing:
        raise GateError('coverage is missing shipping assemblies: ' + ', '.join(missing))
    failures = []
    # Every assembly in the merged report is gated, not only the shipping list.
    for row in rows:
        line, branch = rate(row, 'line-rate'), rate(row, 'branch-rate')
        print(f'{row.get("name")}: line {line:.2f}%, branch {branch:.2f}%')
        failures += [f'{row.get("name")} {metric} {value:.2f}% is below {THRESHOLD}%'
                     for metric, value in (('line', line), ('branch', branch)) if value < THRESHOLD]
    if failures:
        raise GateError('\n'.join(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('check', choices=['trx', 'coverage'])
    parser.add_argument('path', type=Path)
    args = parser.parse_args()
    try:
        check_trx(args.path) if args.check == 'trx' else check_coverage(args.path)
    except (GateError, ET.ParseError, OSError) as error:
        print(f'error: {error}', file=sys.stderr)
        raise SystemExit(1)


if __name__ == '__main__':
    main()
