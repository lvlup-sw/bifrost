// =============================================================================
// <copyright file="JobRegisteredEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a job is registered with the scheduler.
/// </summary>
/// <param name="JobName">The name of the registered job.</param>
public readonly record struct JobRegisteredEvent(string JobName);
