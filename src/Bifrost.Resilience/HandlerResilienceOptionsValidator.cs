// =============================================================================
// <copyright file="HandlerResilienceOptionsValidator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Options;

namespace Bifrost.Resilience;

/// <summary>Validates <see cref="HandlerResilienceOptions"/>.</summary>
/// <remarks>
/// The rules are written in code, not as DataAnnotations, so validation needs no reflection and
/// stays trim and AOT safe. The validator checks every options name, because each work type uses its
/// own name.
/// </remarks>
internal sealed class HandlerResilienceOptionsValidator : IValidateOptions<HandlerResilienceOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, HandlerResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string>? failures = null;

        if (options.MaxRetries is < 0 or > HandlerResilienceOptions.MaxRetriesLimit)
        {
            (failures ??= []).Add(
                $"HandlerResilienceOptions.MaxRetries must be between 0 and {HandlerResilienceOptions.MaxRetriesLimit}, but it is {options.MaxRetries}.");
        }

        if (options.MaxRetries > 0 && options.ShouldRetry is null)
        {
            (failures ??= []).Add(
                "HandlerResilienceOptions.ShouldRetry is required when MaxRetries is above 0. Classify which failures are transient and safe to retry.");
        }

        if (options.RetryDelay < TimeSpan.Zero || options.RetryDelay > HandlerResilienceOptions.MaxRetryDelay)
        {
            (failures ??= []).Add(
                $"HandlerResilienceOptions.RetryDelay must be between 0 and {HandlerResilienceOptions.MaxRetryDelay}, but it is {options.RetryDelay}.");
        }

        if (options.AttemptTimeout is { } timeout
            && (timeout < HandlerResilienceOptions.MinAttemptTimeout || timeout > HandlerResilienceOptions.MaxAttemptTimeout))
        {
            (failures ??= []).Add(
                $"HandlerResilienceOptions.AttemptTimeout must be between {HandlerResilienceOptions.MinAttemptTimeout} and {HandlerResilienceOptions.MaxAttemptTimeout}, but it is {timeout}.");
        }

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
