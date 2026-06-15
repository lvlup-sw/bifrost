// =============================================================================
// <copyright file="JobUnregisteredEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a job is removed from the scheduler.
/// </summary>
/// <param name="JobName">The name of the unregistered job.</param>
public readonly record struct JobUnregisteredEvent(string JobName);
