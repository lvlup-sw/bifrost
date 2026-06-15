// =============================================================================
// Bifrost.Concurrency.AotSmoke — NativeAOT compatibility smoke test (DR-1)
//
// Exercises the buffered ConcurrentPriorityQueue REFERENCE-TYPE path under a
// NativeAOT-published binary. The buffered core (ESA 2021 §4 insertion/deletion
// buffers, opt-in via the bufferCapacity knob) wraps the arity-4 heap with an
// [InlineArray] inline buffer that is GC-tracked even for reference elements;
// the move helpers use Span.CopyTo / gated Clear so they never bypass the GC
// write barrier. This smoke proves that whole path:
//   • publishes warning-clean — no IL2xxx (trim) / IL3050 (AOT codegen) warnings;
//   • RUNS as a native binary and dispatches every element correctly.
//
// The queue is constructed with a reference element type (object) and
// bufferCapacity: 16, then loaded with far more than 16 elements per sub-queue
// so every active sub-queue must FLUSH its insertion buffer into the heap and,
// on drain, REFILL its deletion buffer from the heap — exercising the exact
// reference-element buffer-move code that carries the AOT/trim risk.
//
// Dispatch is asserted via the relaxed two-choice TryDequeue — the MultiQueue
// throughput drain that serves D.front() and refills D from the heap, i.e. the
// buffer-aware pop. Its contract is "one of the smallest", a bounded rank error,
// NOT the strict global minimum, so the smoke asserts what that contract
// guarantees: every enqueued element is dispatched exactly once (multiset
// conservation), the drained count equals the enqueued count, and the queue is
// empty afterward. A regression in the buffered reference-element flush/refill
// path would surface here as a dropped, duplicated, or corrupted element. This
// mirrors the "assert real dispatch, not just construction" shape of the
// scheduling AOT smoke.
// =============================================================================

using Bifrost.Concurrency;

Console.WriteLine("Bifrost.Concurrency AOT smoke — starting.");

// ─────────────────────────────────────────────────────────────────────────────
// 1. Construct the buffered queue with a REFERENCE element type.
//
//    bufferCapacity: 16 activates the ESA 2021 §4 per-sub-queue insertion and
//    deletion buffers (the highest AOT/trim-risk surface — InlineArray<object>
//    moves through Span<object> copies). boundedCapacity: -1 = unbounded,
//    stickiness: -1 = default. object elements force the reference-type write
//    path through the buffer moves.
// ─────────────────────────────────────────────────────────────────────────────

var queue = new ConcurrentPriorityQueue<object, long>(
    boundedCapacity: -1,
    stickiness: -1,
    bufferCapacity: 16);

// ─────────────────────────────────────────────────────────────────────────────
// 2. Enqueue a spread that forces buffer FLUSH + REFILL.
//
//    With bufferCapacity 16, any sub-queue that receives more than 16 inserts
//    must flush its insertion buffer into the arity-4 heap, and on drain refill
//    its deletion buffer from the heap. The default sub-queue count is
//    4 × ProcessorCount rounded up to a power of two; enqueueing well past
//    16 × that count guarantees every active sub-queue crosses the flush
//    threshold many times over on any host. Priorities are inserted in a
//    deliberately non-sorted (interleaved) order so the heap — not the
//    insertion order — determines the drain order. Each priority is distinct
//    (a dense 0..elementCount-1 range) so conservation can be checked exactly.
// ─────────────────────────────────────────────────────────────────────────────

const int elementCount = 4096;

// Interleave priorities so neither the insertion nor the heap-array order is
// already sorted: walk even keys ascending, then odd keys ascending. Each
// element is a distinct boxed reference (object) carrying its own priority.
var enqueued = 0;
for (long key = 0; key < elementCount; key += 2)
{
    queue.Enqueue(new SmokeItem(key), key);
    enqueued++;
}

for (long key = 1; key < elementCount; key += 2)
{
    queue.Enqueue(new SmokeItem(key), key);
    enqueued++;
}

Console.WriteLine($"Enqueued {enqueued} reference-type element(s) into a buffered queue (bufferCapacity 16).");

if (queue.Count != enqueued)
{
    Console.Error.WriteLine(
        $"FAIL: queue.Count ({queue.Count}) does not equal the {enqueued} elements enqueued — buffered Count (I+D+heap) is wrong under NativeAOT.");
    return 1;
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Drain via the relaxed two-choice TryDequeue (the buffer-aware throughput
//    pop) and ASSERT real dispatch by multiset conservation: every distinct
//    priority must be dispatched EXACTLY ONCE, with its matching element. The
//    relaxed contract does not promise strict global order, so order is not
//    asserted; what it does promise — that nothing is dropped, duplicated, or
//    corrupted across the buffered flush + refill — is. A regression in the
//    buffered reference-element move/refill path would surface as a duplicate
//    priority, a missing priority, or an element/priority mismatch.
// ─────────────────────────────────────────────────────────────────────────────

var seen = new bool[elementCount];
var drained = 0;

while (queue.TryDequeue(out object? element, out long priority))
{
    if (priority < 0 || priority >= elementCount)
    {
        Console.Error.WriteLine(
            $"FAIL: dequeued out-of-range priority {priority} — buffered drain corrupted a priority under NativeAOT.");
        return 1;
    }

    if (seen[priority])
    {
        Console.Error.WriteLine(
            $"FAIL: priority {priority} dequeued more than once — buffered flush/refill duplicated an element under NativeAOT.");
        return 1;
    }

    if (element is not SmokeItem item || item.Priority != priority)
    {
        Console.Error.WriteLine(
            $"FAIL: element/priority mismatch at drain position {drained} (priority {priority}) — buffered reference payload corrupted under NativeAOT.");
        return 1;
    }

    seen[priority] = true;
    drained++;
}

Console.WriteLine($"Drained {drained} element(s) via the buffer-aware relaxed dequeue.");

// ─────────────────────────────────────────────────────────────────────────────
// 4. Verify exact conservation: every enqueued priority came back exactly once,
//    the drained count equals the enqueued count, and the queue is now empty.
//    A short/long drain or a gap in `seen` means the buffered flush/refill path
//    dropped or duplicated elements under NativeAOT.
// ─────────────────────────────────────────────────────────────────────────────

if (drained != enqueued)
{
    Console.Error.WriteLine(
        $"FAIL: drained {drained} element(s) but enqueued {enqueued} — buffered flush/refill conservation broken under NativeAOT.");
    return 1;
}

for (var p = 0; p < elementCount; p++)
{
    if (!seen[p])
    {
        Console.Error.WriteLine(
            $"FAIL: priority {p} was never dequeued — buffered flush/refill dropped an element under NativeAOT.");
        return 1;
    }
}

if (queue.Count != 0)
{
    Console.Error.WriteLine(
        $"FAIL: queue.Count is {queue.Count} after a full drain, expected 0 — buffered Count broken under NativeAOT.");
    return 1;
}

Console.WriteLine("Buffered reference-type dispatch verified: every element dispatched exactly once, conserved, and empty after drain.");
Console.WriteLine("Bifrost.Concurrency AOT smoke — PASS.");
return 0;

// =============================================================================
// Reference payload — a distinct boxed reference per enqueued element so the
// buffer moves exercise the reference-element (write-barrier) path, not the
// blittable value-type path. Carries its own priority so the drain can assert
// the element/priority pairing survived the flush + refill.
// =============================================================================

/// <summary>
/// A minimal reference-type payload enqueued into the buffered queue. Using a
/// reference element forces the ESA 2021 §4 buffer moves through the
/// write-barrier-aware <c>Span&lt;object&gt;</c> copy path — the surface this
/// AOT smoke is here to prove publishes warning-clean and runs correctly.
/// </summary>
/// <param name="Priority">The priority this item was enqueued with.</param>
internal sealed record SmokeItem(long Priority);
