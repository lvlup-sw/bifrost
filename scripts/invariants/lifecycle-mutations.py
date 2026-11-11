from pathlib import Path
import subprocess
import re
root = Path(__file__).resolve().parents[2]
output = root / "artifacts/invariants/mutations"
output.mkdir(parents=True, exist_ok=True)
cases = [
    ('drain-aborts', 'src/Bifrost/WorkOrchestrator.cs', 'return Completion.WaitAsync(ct);', 'return AbortAsync(ct);', 'O1_O2_GatedLifecycleHistories'),
    ('extra-admission-retry', 'src/Bifrost.Resilience/ResiliencyPolicyGenerator.cs', 'retryCount: settings.RetryCount,', 'retryCount: settings.RetryCount + 1,', 'R1_AdmissionRetryBudgetsAndEffectIdentities'),
    ('drop-write', 'src/Bifrost/DeadLetter/DeadLetterQueue.cs', 'FullMode = BoundedChannelFullMode.DropOldest,', 'FullMode = BoundedChannelFullMode.DropWrite,', 'D1_GeneratedSequentialDropOldestLedger'),
]
for name, filename, old, new, test in cases:
    path = root / filename
    original = path.read_text()
    assert old in original
    command = ['dotnet', 'run', '--project', 'src/Bifrost.Tests', '--', '--treenode-filter', f'/*/*/LifecycleInvariantTests/{test}', '--minimum-expected-tests', '1']
    try:
        path.write_text(original.replace(old, new))
        result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
        (output / f'lifecycle-mutation-{name}.log').write_text('COMMAND: ' + ' '.join(command) + '\nEXIT: ' + str(result.returncode) + '\n' + result.stdout)
        failures = re.search(r'failed:\s+([0-9]+)', result.stdout)
        if result.returncode != 2 or failures is None or int(failures.group(1)) == 0:
            raise RuntimeError(f'{name}: expected an actual failing test, not build/discovery failure')
    finally:
        path.write_text(original)
