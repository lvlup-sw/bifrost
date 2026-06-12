// =============================================================================
// <copyright file="WorkEnvelope.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost;

/// <summary>
/// Pairs a work item with the scheduling metadata required for class-based
/// priority dispatch.
/// </summary>
/// <typeparam name="TWork">The type of work item being enveloped.</typeparam>
/// <param name="Work">The work item to process.</param>
/// <param name="Class">The <see cref="WorkClass"/> the item was enqueued under.</param>
/// <param name="EnqueuedAtTicks">
/// The monotonic timestamp captured via <see cref="TimeProvider.GetTimestamp"/> at
/// enqueue time. This is <b>not</b> a wall-clock value: it is in
/// <see cref="TimeProvider.TimestampFrequency"/> units, not
/// <see cref="DateTimeOffset.UtcNow"/> ticks, and must never be compared against
/// wall-clock time. Queue-wait duration is computed later via
/// <see cref="TimeProvider.GetElapsedTime(long)"/> with this value as the start.
/// </param>
public readonly record struct WorkEnvelope<TWork>(TWork Work, WorkClass Class, long EnqueuedAtTicks);
