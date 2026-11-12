// =============================================================================
// <copyright file="ScheduleRegistry.Restore.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;

namespace Bifrost.Scheduling.Registry;

/// <summary>Attaches configured dispatchers to saved schedules at startup.</summary>
public sealed partial class ScheduleRegistry
{
    // Startup-only attachment: unlike RegisterAsync this does not create or save a new
    // schedule. Durable state is authoritative; the configured dispatcher supplies code.
    internal async ValueTask RestoreAsync(JobRecord record, IJobDispatcher dispatcher, CancellationToken ct)
    {
        this.ThrowIfDisposed();
        ValidateName(record.Name);
        ArgumentNullException.ThrowIfNull(record.Cadence);
        ArgumentNullException.ThrowIfNull(dispatcher);
        using var lease = await this.mutationGates.EnterAsync(record.Name, ct).ConfigureAwait(false);
        this.ThrowIfDisposed();
        if (!this.jobs.TryAdd(record.Name, record))
        {
            throw new DuplicateJobNameException(record.Name);
        }

        this.dispatchers[record.Name] = dispatcher;
        this.metrics.RecordRegistered();
        this.events.Publish(new JobRegisteredEvent(record.Name));
        // The tick loop seeds restored records itself. A Register wake would re-arm
        // a reconciled one-shot while its best-effort checkpoint is still pending.
    }
}
