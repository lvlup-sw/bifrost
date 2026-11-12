// =============================================================================
// <copyright file="RegistryCommand.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Registry;

/// <summary>
/// The kind of control action a <see cref="RegistryCommand"/> carries.
/// </summary>
internal enum RegistryCommandKind
{
    /// <summary>A job was registered and its cadence should begin evaluating.</summary>
    Register,

    /// <summary>A job was unregistered and should stop firing.</summary>
    Unregister,

    /// <summary>A job was paused and should stop firing until resumed.</summary>
    Pause,

    /// <summary>A job was resumed and should return to active scheduling.</summary>
    Resume,

    /// <summary>A job was triggered and should fire immediately, out of band.</summary>
    Trigger,
}

/// <summary>
/// A control message posted to the registry's wake channel when a job's lifecycle
/// changes. The registry owns durable state and metadata; this command wakes the
/// tick loop so it can re-evaluate the affected job's schedule. It captures only the
/// minimum the tick loop needs — the action kind and the job name — and the loop
/// re-reads current job state from the registry.
/// </summary>
/// <param name="Kind">The lifecycle action that produced this command.</param>
/// <param name="JobName">The name of the job the action applies to.</param>
/// <param name="RegistrationId">
/// The strictly positive registration identity that produced this command.
/// </param>
internal readonly record struct RegistryCommand(
    RegistryCommandKind Kind,
    string JobName,
    long RegistrationId = 0);

/// <summary>The dispatcher bound to a registration, or why the lookup missed.</summary>
/// <param name="Dispatcher">The dispatcher for the requested registration.</param>
/// <param name="Replaced">True when a different registration now owns the name.</param>
internal readonly record struct DispatcherResolution(IJobDispatcher? Dispatcher, bool Replaced);
