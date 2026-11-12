# Hosted scheduler recovery

## Saved state was overwritten before it was loaded

The hosted registration service called `RegisterAsync` for every configured job.
That method writes a new record. The tick loop then loaded the store, after its
checkpoint, lifecycle state, cadence, and metadata had already been replaced.

Five tests through the real DI startup path failed before the fix: four lifecycle
states lost their saved record, and three required missed occurrence identities
were absent. Earlier synthetic restart fixtures manually replaced the store image
after registration, masking the hosted composition defect.

Startup now reads saved records once, matches configured names, and attaches their
configured dispatchers without saving a replacement record. Saved cadence, policy,
state, and checkpoints take precedence; change them through explicit registry
mutations. Unknown stored names are not activated from stored type names. Custom
`IScheduleRegistry` implementations continue to own their registration semantics.
The extra startup lookup is O(number of saved jobs); dispatch gains no new I/O.

## Past one-shots were armed after reconciliation

Following restoration, four of six policy/outage cases failed: `SkipMissed` fired
once, and checkpoint failures let Coalesce/FireAllMissed fire twice in one startup.
The seed path reconciled a past occurrence and then armed that same occurrence.
A registration wake could arm it again before a checkpoint updated live state.

Restored records no longer emit a redundant registration wake. The seed path arms
only future occurrences after applying the missed-fire policy. This does not
promise exactly-once effects across crashes: replay before checkpoint durability
remains permitted.

## Verification

The complete scheduler suite passed 381 tests. Four tests start a separate process,
observe a controlled cut, kill it, and start another process against the same file:

| Cut | Total recorded attempts after recovery |
|---|---:|
| Before dispatch | 1 |
| After dispatch, before checkpoint | 2 |
| Before checkpoint effect | 2 |
| After checkpoint effect, before acknowledgment | 1 |

Each result retains the same scheduled occurrence identity. The fixture flushes
its single-record file before rename. It uses controlled synchronous dispatch to
pin effects, not production router scheduling. This is process-death coverage,
not database, multi-writer, power-loss, or exactly-once qualification. The shipped
store is in-memory; actual consumer durable adapters still require qualification.

## Preview changed the scheduled instant

Two additional tests failed: an interval due at minute 10 moved to minute 11
when previewed at minute 1, and an expired one-shot appeared as a future fire.
The preview recomputed the first interval from the inspection time and accepted
non-future results. It now preserves a future `NextFireAt` and stops when a
cadence cannot advance. Later jittered occurrences remain projections, not
committed dispatch times.
