import json
import pathlib
import subprocess
import time

root = pathlib.Path(__file__).resolve().parents[2]
output = root / 'artifacts/invariants/mutations'
output.mkdir(parents=True, exist_ok=True)
project = 'src/Bifrost.Tests.Concurrency'
production = root / 'src/Bifrost.Concurrency'
cases = [
    ('Q1', 'ConcurrentPriorityQueue.Collection.cs', 'int removed = t.LockedClear();', 'int removed = 0;', 'Q1Generated*'),
    ('Q2', 'ConcurrentPriorityQueue.Enqueue.cs', 'if (count >= _boundedCapacity) return false;', 'if (count > _boundedCapacity) return false;', 'Q2Generated*'),
    ('Q3', 'ConcurrentPriorityQueue.Inspect.cs', 'if (found && CompareEffective(top, minimum) >= 0)', 'if (found && CompareEffective(top, minimum) <= 0)', 'Q3Lawful*'),
    ('Q4', 'ConcurrentPriorityQueue.Enqueue.cs', 'Interlocked.Decrement(ref _boundedCount);', 'Interlocked.Add(ref _boundedCount, 0);', 'Q4Reachable*'),
    ('Q5', 'ConcurrentPriorityQueue.Enqueue.cs', 'if (queues[index].TryLockedPush(element, priority))\n                {\n                    return true;', 'if (queues[index].TryLockedPush(element, priority))\n                {\n                    queues[index].TryLockedPush(element, priority);\n                    return true;', 'Q5Controlled*'),
    ('M', 'ConcurrentPriorityQueue.Collection.cs', 'int removed = t.LockedClear();', 'int removed = 0;', 'Metamorphic*'),
]
results = []
for family, file, before, after, test in cases:
    path = production / file
    original = path.read_text()
    if original.count(before) != 1:
        raise RuntimeError(f'{family}: expected unique mutation site')
    started = time.monotonic()
    try:
        path.write_text(original.replace(before, after))
        command = ['dotnet', 'run', '--project', project, '--', '--treenode-filter', f'/*/*/GeneratedQueueInvariantTests/{test}', '--output', 'Detailed', '--no-ansi']
        run = subprocess.run(command, cwd=root, capture_output=True, text=True, timeout=120)
        output = run.stdout + run.stderr
        (output / f'queue-mutation-{family}.log').write_text(output)
        killed = run.returncode != 0 and 'failed: 1' in output and 'error CS' not in output and 'error SA' not in output
        results.append(dict(family=family, mutation=f'{file}: {before} -> {after}', exit=run.returncode, killed=killed, elapsedSeconds=round(time.monotonic()-started, 3)))
        print(json.dumps(results[-1]), flush=True)
        if not killed:
            raise RuntimeError(f'{family}: mutant did not produce the expected test failure')
    finally:
        path.write_text(original)
(output / 'queue-mutation-results.json').write_text(json.dumps(results, indent=2) + '\n')
