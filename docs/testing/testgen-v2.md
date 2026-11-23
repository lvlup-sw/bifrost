# Typed stateful test generation

`Systematic/Testgen/worker-registry.json` defines a sequential WorkerRegistry campaign. Its independent dictionary model tracks creation, idle/busy transitions, stop requests, completion, and reuse of a worker ID. The binding compares each worker's state and the registry's snapshot counts after every operation. Worker delegates finish only when the binding releases them.

The maintained suite has 12 histories: one explicit lifecycle and seeded candidate histories with up to 24 executed steps. Each generated history allows 192 candidate attempts. Disabled operations are rejected by the model. Cleanup has a five-second environmental deadline. Worker lifetime ends only on explicit release; the native test process owns the overall timeout.

Use a testgen package advertising version 2 stateful support:

```sh
testgen describe generate_stateful_tests --json
testgen generate_stateful_tests --input src/Bifrost.Tests/Systematic/Testgen/worker-registry.json > /tmp/worker-registry.patch
dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/WorkerRegistryGeneratedTests/*' --minimum-expected-tests 12
python3 scripts/invariants/worker-registry-testgen-control.py --testgen /absolute/path/to/testgen-cli.js --output /tmp/bifrost-testgen-control
```

The generator returns a create-only patch. For regeneration, remove only the generated file in a scratch checkout, apply the patch, and review the diff. Keep the handwritten binding. The control script temporarily changes `IdleWorkerCount` to include stopping workers, requires an oracle failure, replays its concrete reduced history, restores source and suite in `finally`, then reruns the baseline. Do not run it concurrently with other builds or source edits in that checkout.

The control reduces to `create(a), stop(a)` with signature `stateful/stop/state-mismatch`. This establishes sensitivity to that count defect. It does not test arbitrary concurrent histories, durable recovery, throughput, or production risk. The model uses no WorkerRegistry implementation code to compute expected states; the target binding uses the public API. Native worker cleanup schedules remain uncontrolled.

[Issue #85](https://github.com/lvlup-sw/bifrost/issues/85) separately tracks real production persistence, sustained dependency outages, resource limits, and release applicability. The existing process-recovery fixture uses a single-record file store and process termination; it is not a production durability adapter.
