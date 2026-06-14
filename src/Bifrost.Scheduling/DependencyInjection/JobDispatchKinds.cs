// =============================================================================
// <copyright file="JobDispatchKinds.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The dispatch-kind tags a job builder records, mirroring the values
/// <see cref="JobRecord.DispatchKind"/> carries so a built job's kind matches its
/// persisted record.
/// </summary>
internal static class JobDispatchKinds
{
    /// <summary>
    /// The orchestrator dispatch kind: a fire builds a work item enqueued on an
    /// <see cref="Bifrost.Core.IWorkOrchestrator{TWork}"/>.
    /// </summary>
    public const string Orchestrator = "orchestrator";

    /// <summary>
    /// The inline dispatch kind: a fire runs a supplied delegate on a pool thread.
    /// </summary>
    public const string Inline = "inline";

    /// <summary>
    /// The custom dispatch kind: a fire is handled by a DI-resolved
    /// <see cref="IJobDispatcher"/>.
    /// </summary>
    public const string Custom = "custom";
}
