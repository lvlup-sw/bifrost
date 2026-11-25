// =============================================================================
// <copyright file="DispatchStrategyOptionsValidator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Validates that the named orchestrator options agree with whether keyed dispatch was configured
/// on the builder.
/// </summary>
/// <param name="name">The options name of the work type.</param>
/// <param name="keyed">Whether <c>WithKeyedDispatch</c> was configured.</param>
/// <remarks>
/// <para>
/// With keyed dispatch, the options must select <see cref="DispatchStrategy.Keyed"/> and
/// <see cref="QueueFullMode.Reject"/>. <c>WithKeyedDispatch</c> sets both, so a failure means
/// another configure action changed them: for example a priority strategy set in
/// <c>AddWorkOrchestrator</c>, or <see cref="QueueFullMode.Wait"/> set later.
/// </para>
/// <para>
/// Without keyed dispatch, <see cref="DispatchStrategy.Keyed"/> is invalid, because no key
/// selector exists.
/// </para>
/// <para>
/// <c>AddWorkOrchestrator</c> registers the options with <c>ValidateOnStart</c>, so a host fails to
/// start on a bad combination. Without a host, the first resolution of the orchestrator fails.
/// </para>
/// </remarks>
internal sealed class DispatchStrategyOptionsValidator(string name, bool keyed) : IValidateOptions<WorkOrchestratorOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? optionsName, WorkOrchestratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(optionsName, name, StringComparison.Ordinal))
            return ValidateOptionsResult.Skip;

        if (!keyed)
        {
            return options.DispatchStrategy == DispatchStrategy.Keyed
                ? ValidateOptionsResult.Fail("DispatchStrategy.Keyed needs a key selector. Configure it with WithKeyedDispatch.")
                : ValidateOptionsResult.Success;
        }

        List<string>? failures = null;
        if (options.DispatchStrategy != DispatchStrategy.Keyed)
        {
            (failures ??= []).Add(
                $"Keyed dispatch needs DispatchStrategy.Keyed, but the options select {options.DispatchStrategy}. Keyed dispatch cannot be combined with class priority dispatch.");
        }

        if (options.FullMode != QueueFullMode.Reject)
        {
            (failures ??= []).Add(
                $"Keyed dispatch supports only QueueFullMode.Reject, but the options select {options.FullMode}.");
        }

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
