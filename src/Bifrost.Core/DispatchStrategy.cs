// =============================================================================
// <copyright file="DispatchStrategy.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Selects the work-queue binding the orchestrator constructs.
/// </summary>
/// <remarks>
/// <para>
/// Selection is enum/factory-based by design: the orchestrator switches on this
/// value and constructs the binding directly — no reflective resolution, keeping
/// the factory trim/AOT-safe. All bindings are sealed so the default path
/// devirtualizes.
/// </para>
/// <para>
/// <b>Enqueue semantics differ by strategy.</b> <see cref="Fifo"/> keeps
/// producer-wait semantics on the asynchronous enqueue path (await space at
/// capacity). The priority strategies are FAIL-FAST at admission: an enqueue at
/// capacity — or above the work class's admission watermark — is rejected
/// immediately rather than waited out, because producer-wait at capacity would
/// reintroduce admission-side priority inversion. See
/// <see cref="WorkOrchestratorOptions.DispatchStrategy"/> and the orchestrator's
/// enqueue documentation for the full semantics table.
/// </para>
/// <para>
/// <b>Choosing between the priority strategies.</b> Indicative soak measurements
/// (<c>docs/benchmarks/2026-06-cpq-soak.md</c>) currently favor
/// <see cref="PriorityLocking"/> in the 1–8-worker, seconds-long regime typical of
/// this orchestrator; <see cref="PriorityMultiQueue"/> targets higher producer
/// concurrency. Treat the soak numbers as indicative until the release-run
/// benchmarks finalize the guidance.
/// </para>
/// </remarks>
public enum DispatchStrategy
{
    /// <summary>
    /// Strict-FIFO bounded-channel queue — the default, preserving the
    /// orchestrator's pre-existing semantics: producer-wait on the asynchronous
    /// enqueue path, no class-based ordering or admission policy.
    /// </summary>
    Fifo = 0,

    /// <summary>
    /// Lock-free MultiQueue-based priority binding: class-aware virtual-time
    /// ordering with watermark admission, relaxed (approximate) ordering and
    /// counting under concurrency, fail-fast admission.
    /// </summary>
    PriorityMultiQueue = 1,

    /// <summary>
    /// Coarse-locking binary-heap priority binding: class-aware virtual-time
    /// ordering with watermark admission, exact ordering and exact admission
    /// boundaries under a global lock, fail-fast admission.
    /// </summary>
    PriorityLocking = 2,
}
