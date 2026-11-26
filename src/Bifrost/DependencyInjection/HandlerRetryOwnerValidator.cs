// =============================================================================
// <copyright file="HandlerRetryOwnerValidator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;

using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Validates that the dead-letter queue does not retry handler failures when handler resilience
/// owns handler retries.
/// </summary>
/// <param name="name">The options name of the work type.</param>
/// <param name="workTypeName">The work type name used in the error message.</param>
/// <remarks>
/// <para>
/// <c>Build</c> registers this validator only when both <c>WithHandlerResilience</c> and
/// <c>WithDeadLetterQueue</c> were called, in either order. Handler resilience is then the one
/// retry owner, and <see cref="DeadLetterQueueOptions.MaxRetries"/> must be zero: the dead-letter
/// queue captures the final failure after the last handler resilience attempt.
/// </para>
/// <para>
/// The check is options validation, not a <c>Build</c> check, because
/// <see cref="DeadLetterQueueOptions.MaxRetries"/> may come from configuration or from configure
/// actions that run after <c>Build</c>. <c>WithDeadLetterQueue</c> registers the options with
/// <c>ValidateOnStart</c>, so a host fails to start. Without a host, the first resolution of the
/// orchestrator fails with an <see cref="OptionsValidationException"/>.
/// </para>
/// </remarks>
internal sealed class HandlerRetryOwnerValidator(string name, string workTypeName) : IValidateOptions<DeadLetterQueueOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? optionsName, DeadLetterQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(optionsName, name, StringComparison.Ordinal))
            return ValidateOptionsResult.Skip;

        return options.MaxRetries == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Handler resilience owns handler retries for work type '{workTypeName}', so DeadLetterQueueOptions.MaxRetries must be 0, but it is {options.MaxRetries}. "
                + "Configure WithDeadLetterQueue(o => o.MaxRetries = 0) and set retries in WithHandlerResilience, or remove WithHandlerResilience.");
    }
}
