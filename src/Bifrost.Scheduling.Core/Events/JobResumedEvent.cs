// =============================================================================
// <copyright file="JobResumedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a paused job is resumed.
/// </summary>
/// <param name="JobName">The name of the resumed job.</param>
public readonly record struct JobResumedEvent(string JobName);
