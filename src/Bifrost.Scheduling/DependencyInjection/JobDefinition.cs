// =============================================================================
// <copyright file="JobDefinition.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// A registrable job definition produced by the fluent scheduler builder (DR-1):
/// the accumulated cadence, missed-fire policy, and dispatch configuration for one
/// job, plus a factory that constructs the concrete <see cref="IJobDispatcher"/>
/// against an <see cref="IServiceProvider"/> at startup.
/// </summary>
/// <remarks>
/// The definition is the seam between the build-time DSL and the runtime registry:
/// <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/> wires a tiny
/// hosted service that, for each accumulated definition, resolves
/// <see cref="DispatcherFactory"/> against the application service provider and
/// calls <see cref="IScheduleRegistry.RegisterAsync"/>. Holding a factory rather
/// than a constructed dispatcher keeps DI resolution deferred to startup, after the
/// service provider exists and is fully built.
/// </remarks>
/// <param name="Name">The unique job name; the registry's identity key.</param>
/// <param name="Cadence">The schedule that determines when the job fires.</param>
/// <param name="MissedFirePolicy">
/// How the scheduler reconciles occurrences missed while the job could not fire;
/// defaults to <see cref="Core.MissedFirePolicy.Coalesce"/> when unset.
/// </param>
/// <param name="DispatchKind">
/// The dispatch kind tag — <c>"orchestrator"</c>, <c>"inline"</c>, or
/// <c>"custom"</c> — mirroring <see cref="JobRecord.DispatchKind"/>.
/// </param>
/// <param name="WorkClass">
/// The <see cref="Bifrost.Core.WorkClass"/> an orchestrator dispatch enqueues
/// under. For inline and custom dispatch it is unused and left at its default.
/// </param>
/// <param name="DispatcherFactory">
/// Constructs the concrete dispatcher against the supplied service provider —
/// resolving the orchestrator and shared sink for orchestrator dispatch, wrapping
/// the delegate for inline dispatch, or resolving the registered dispatcher type
/// for custom dispatch.
/// </param>
internal sealed record JobDefinition(
    string Name,
    Cadence Cadence,
    MissedFirePolicy MissedFirePolicy,
    string DispatchKind,
    WorkClass WorkClass,
    Func<IServiceProvider, IJobDispatcher> DispatcherFactory);
