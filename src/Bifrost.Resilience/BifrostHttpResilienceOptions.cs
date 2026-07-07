// =============================================================================
// <copyright file="BifrostHttpResilienceOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Resilience;

/// <summary>
/// The root, config-bindable options for Bifrost HttpClient resilience: a <see cref="Default"/> policy
/// applied to every client, plus per-HttpClient-name overrides in <see cref="Policies"/>.
/// </summary>
/// <remarks>
/// <para>
/// Bind from the <see cref="SectionName"/> configuration section. Because
/// <see cref="BifrostHttpResilienceExtensions.AddBifrostResilienceHandler"/> resolves the policy by
/// HttpClient name at pipeline-build time, a client gets a different policy purely by adding an entry to
/// <see cref="Policies"/> — no second handler, no code branch. Example:
/// </para>
/// <code>
/// {
///   "Bifrost": { "Resilience": {
///     "Default":  { "TotalRequestTimeout": "00:00:30", "AttemptTimeout": "00:00:10", "MaxRetries": 3 },
///     "Policies": {
///       "control-plane-streaming": { "TotalRequestTimeout": "01:00:00", "MaxRetries": 0, "EnableCircuitBreaker": false },
///       "e2b-sidecar":             { "TotalRequestTimeout": "00:02:00", "AttemptTimeout": "00:02:00", "MaxRetries": 2 }
///     }
///   }}
/// }
/// </code>
/// </remarks>
public sealed class BifrostHttpResilienceOptions
{
    /// <summary>
    /// The configuration section these options bind from.
    /// </summary>
    public const string SectionName = "Bifrost:Resilience";

    /// <summary>
    /// Gets or sets the policy applied to any HttpClient without a matching entry in <see cref="Policies"/>.
    /// </summary>
    public BifrostHttpResiliencePolicy Default { get; set; } = new();

    /// <summary>
    /// Gets the per-HttpClient-name policy overrides, keyed by the logical client name passed to
    /// <c>AddHttpClient(name)</c>. Lookups are case-insensitive.
    /// </summary>
    public IDictionary<string, BifrostHttpResiliencePolicy> Policies { get; }
        = new Dictionary<string, BifrostHttpResiliencePolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the policy for a given HttpClient name: the matching <see cref="Policies"/> entry when
    /// present, otherwise <see cref="Default"/>.
    /// </summary>
    /// <param name="clientName">The logical HttpClient name (empty for the default client).</param>
    /// <returns>The resolved <see cref="BifrostHttpResiliencePolicy"/>.</returns>
    public BifrostHttpResiliencePolicy ResolvePolicy(string? clientName) =>
        !string.IsNullOrEmpty(clientName) && Policies.TryGetValue(clientName, out var policy)
            ? policy
            : Default;
}
