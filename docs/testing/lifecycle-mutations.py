from pathlib import Path
import subprocess
cases = [
    ('drain-aborts', 'src/Bifrost/WorkOrchestrator.cs', 'return Completion.WaitAsync(ct);', 'return AbortAsync(ct);', 'O1_O2_GatedLifecycleHistories'),
    ('extra-admission-retry', 'src/Bifrost.Resilience/ResiliencyPolicyGenerator.cs', 'retryCount: settings.RetryCount,', 'retryCount: settings.RetryCount + 1,', 'R1_AdmissionRetryBudgetsAndEffectIdentities'),
    ('drop-write', 'src/Bifrost/DeadLetter/DeadLetterQueue.cs', 'FullMode = BoundedChannelFullMode.DropOldest,', 'FullMode = BoundedChannelFullMode.DropWrite,', 'D1_GeneratedSequentialDropOldestLedger'),
]
for name, filename, old, new, test in cases:
    path = Path(filename)
    original = path.read_text()
    assert old in original
    command = ['dotnet', 'run', '--project', 'src/Bifrost.Tests', '--', '--treenode-filter', f'/*/*/LifecycleInvariantTests/{test}']
    try:
        path.write_text(original.replace(old, new))
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
        Path(f'docs/testing/lifecycle-mutation-{name}.log').write_text('COMMAND: ' + ' '.join(command) + '\nEXIT: ' + str(result.returncode) + '\n' + result.stdout)
    finally:
        path.write_text(original)
