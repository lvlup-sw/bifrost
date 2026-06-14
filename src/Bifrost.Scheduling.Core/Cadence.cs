// =============================================================================
// <copyright file="Cadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The abstract schedule that determines when a job fires.
/// </summary>
/// <remarks>
/// This is the minimal contract surface: it is the polymorphic base carried by
/// <see cref="JobRecord"/> and the scheduling registry. The cadence engine in a
/// later group expands this base with the occurrence-computation method and the
/// concrete cadence factories (interval, cron, calendar); keeping the base
/// trivially subclassable here lets the contract types compose without taking a
/// dependency on that engine.
/// </remarks>
public abstract record Cadence;
