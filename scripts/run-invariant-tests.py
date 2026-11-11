import argparse
import json
import pathlib
import os
import subprocess
import time
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--configuration', default='Release')
parser.add_argument('--output', default='artifacts/invariants')
parser.add_argument('--skip-build', action='store_true')
args = parser.parse_args()
started_all = time.monotonic()
root = pathlib.Path(__file__).resolve().parents[1]
output = (root / args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
os.environ["BIFROST_REPLAY_OUTPUT"] = str(output / "replays")
os.environ["BIFROST_REGISTRY_ARTIFACTS"] = str(output / "registry")
suites = {
    'Bifrost.Tests': ['HistoryToolsTests', 'LifecycleInvariantTests', 'LifecycleIdentityTests', 'LifecycleOutcomeInvariantTests', 'PriorityScalingInvariantTests', 'AutoscalingClockInvariantTests', 'AutoscalingLifecycleInvariantTests'],
    'Bifrost.Tests.Concurrency': ['GeneratedQueueInvariantTests', 'QueueReplayTests', 'ObservedEmptyInvariantTests'],
    'Bifrost.Tests.Scheduling': ['IntervalInvariantTests', 'IntervalMutationAdequacyTests', 'SchedulerReplayTests', 'SystematicRegistryTests', 'SystematicRegistryRaceTests', 'SystematicRegistryRestartTests'],
}
summary = {'sourceSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(), 'dirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True).strip()), 'configuration': args.configuration, 'seedMultiplier': os.environ.get('BIFROST_INVARIANT_SEED_MULTIPLIER', '1'), 'results': []}
failed = False
for project, classes in suites.items():
    if not args.skip_build:
        build = subprocess.run(['dotnet', 'build', f'src/{project}/{project}.csproj', '-c', args.configuration], cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (output / f'{project}-build.log').write_text(build.stdout)
        if build.returncode:
            summary['results'].append({'project': project, 'stage': 'build', 'exit': build.returncode})
            failed = True
            continue
    for name in classes:
        command = ['dotnet', f'src/{project}/bin/{args.configuration}/net10.0/{project}.dll', '--treenode-filter', f'/*/*/{name}/*', '--minimum-expected-tests', '1', '--report-trx', '--report-trx-filename', f'{name}.trx', '--results-directory', str(output), '--no-ansi']
        report = output / f'{name}.trx'
        report.unlink(missing_ok=True)
        started = time.monotonic()
        result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (output / f'{name}.log').write_text(result.stdout)
        counters = ET.parse(report).find('.//{*}Counters').attrib if report.exists() else {}
        selected = int(counters.get('executed', '0'))
        summary['results'].append({'project': project, 'class': name, 'command': command, 'exit': result.returncode, 'counters': counters, 'seconds': round(time.monotonic() - started, 3)})
        failed |= result.returncode != 0 or selected == 0
        print(f'{name}: exit={result.returncode}', flush=True)
summary['elapsedSeconds'] = round(time.monotonic() - started_all, 3)
(output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
raise SystemExit(1 if failed else 0)
