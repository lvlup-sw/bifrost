// =============================================================================
// <copyright file="JobPausedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a job is paused.
/// </summary>
/// <param name="JobName">The name of the paused job.</param>
public readonly record struct JobPausedEvent(string JobName);
