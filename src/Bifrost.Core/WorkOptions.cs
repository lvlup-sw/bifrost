// =============================================================================
// <copyright file="WorkOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>Identifies options belonging to a registered work type.</summary>
public static class WorkOptions
{
    /// <summary>Returns the named-options key for a work type.</summary>
    /// <typeparam name="TWork">The registered work type.</typeparam>
    /// <returns>A type-specific options name.</returns>
    public static string Name<TWork>() => typeof(TWork).AssemblyQualifiedName!;
}
