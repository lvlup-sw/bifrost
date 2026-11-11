# Reproduce Q5 mutation evidence

From a clean checkout with the repository's .NET SDK and dependencies available:

```sh
python3 docs/testing/q5-evidence/reproduce.py
```

The harness locates the repository from its own path. It builds the unchanged baseline and runs all four Q5 tests, applies each production mutation independently, then restores the exact original production text in `finally`, rebuilds, reruns Q5, and runs the full concurrency suite. Do not run concurrent builds or edits in the same checkout during this experiment.

Fresh raw logs, TRX reports, and a machine-readable summary go to `work/q5-reproduced` (override with `--output`). The committed logs and summary in this directory retain one actual run; they are not fabricated expected output. Build logs are included to show that both mutants compiled. The console logs retain actual failure messages and recorded histories. Full TRX reports are produced on reproduction but omitted here to avoid duplicating console output.

The harness requires four discovered Q5 tests, successful baseline/restored results, and test-failure exit 2 for each mutant. Each mutant must fail both controlled tests with the actual false-result assertion text. Build failure, missing/zero discovery, process timeout, and watchdog failures cannot satisfy those checks. Generated prefill mutant failure counts can vary with the OS schedule; controlled behavioral witnesses are mandatory. The full-suite check requires at least 160 executed tests, allowing subsequent additional tests.

`finally` restoration covers normal exceptions and subprocess timeout; forcibly terminating the Python process or machine cannot execute cleanup. If interrupted that way, restore the production file before building. No network publishing, PR creation, or production change is part of this harness.
