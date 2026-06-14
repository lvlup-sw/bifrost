// =============================================================================
// <copyright file="AssemblyMarker.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Concurrency;

/// <summary>
/// Marker type anchoring the Bifrost.Concurrency assembly for typeof-level
/// reachability checks (e.g. assembly identity in tests) without reflection.
/// </summary>
internal static class AssemblyMarker;
