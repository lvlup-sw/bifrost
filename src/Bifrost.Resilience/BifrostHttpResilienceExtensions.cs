// =============================================================================
// <copyright file="BifrostHttpResilienceExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Net.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;

namespace Bifrost.Resilience;

/// <summary>
/// Wires Bifrost-generated resilience pipelines onto <see cref="System.Net.Http.HttpClient"/> instances
/// created by <see cref="IHttpClientFactory"/>.
/// </summary>
/// <remarks>
/// This is the HttpClient counterpart to <see cref="ResiliencyPolicyGenerator"/> (which protects arbitrary
/// operations imperatively). It builds a Polly v8 <see cref="ResiliencePipeline{T}"/> of
/// <see cref="HttpResponseMessage"/> and attaches it through the platform's
/// <c>AddResilienceHandler</c> seam, so it composes with service discovery and the rest of the HttpClient
/// pipeline like any first-party handler. Register it once via <c>ConfigureHttpClientDefaults</c> to give
/// every client resilience centrally; per-client tuning is then pure configuration
/// (<see cref="BifrostHttpResilienceOptions.Policies"/>).
/// </remarks>
public static class BifrostHttpResilienceExtensions
{
    /// <summary>
    /// The default resilience-pipeline name. The final Polly pipeline key is
    /// <c>{httpClientName}-{pipelineName}</c>, so this stays constant across clients while the resolved
    /// policy varies by client name.
    /// </summary>
    public const string DefaultPipelineName = "bifrost-http";

    /// <summary>
    /// Makes Bifrost the resilience handler for this HttpClient. The concrete policy is resolved by the
    /// client's name from <see cref="BifrostHttpResilienceOptions"/> when the pipeline is built, so applying
    /// this once via <c>ConfigureHttpClientDefaults</c> gives every client the
    /// <see cref="BifrostHttpResilienceOptions.Default"/> policy, while calling it on a single named client
    /// applies that client's keyed policy — no second handler, no configuration branch in application code.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="pipelineName">The resilience-pipeline name. Defaults to <see cref="DefaultPipelineName"/>.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pipelineName"/> is null or whitespace.</exception>
    /// <remarks>
    /// The call is idempotent and override-safe: it first strips any resilience handler already present —
    /// including a blanket default inherited from <c>ConfigureHttpClientDefaults</c> — so a per-client
    /// override never double-stacks a second pipeline. <c>RemoveAllResilienceHandlers</c> is the platform's
    /// documented override primitive; its <c>[Experimental]</c> (EXTEXP0001) surface is confined to this
    /// method so consumers never write it themselves.
    /// </remarks>
    public static IHttpClientBuilder AddBifrostResilienceHandler(
        this IHttpClientBuilder builder,
        string pipelineName = DefaultPipelineName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

        // Bind the options from configuration so policies are config-driven out of the box; when the
        // section is absent every policy simply uses the BifrostHttpResiliencePolicy defaults. Repeated
        // calls (per client) re-register the same binding harmlessly.
        builder.Services.AddOptions<BifrostHttpResilienceOptions>()
            .BindConfiguration(BifrostHttpResilienceOptions.SectionName);

        // Strip any existing handler (e.g. an inherited blanket default) so Bifrost is the single pipeline.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddResilienceHandler(pipelineName, (pipelineBuilder, context) =>
        {
            var options = context.ServiceProvider
                .GetRequiredService<IOptionsMonitor<BifrostHttpResilienceOptions>>()
                .CurrentValue;

            ConfigurePipeline(pipelineBuilder, options.ResolvePolicy(GetClientName(context.BuilderName, pipelineName)));
        });

        return builder;
    }

    /// <summary>
    /// Recovers the logical HttpClient name from a <see cref="ResilienceHandlerContext.BuilderName"/>.
    /// The platform combines the two as <c>{httpClientName}-{pipelineName}</c>, so the pipeline suffix is
    /// stripped to get the client name used to key <see cref="BifrostHttpResilienceOptions.Policies"/>.
    /// The default client (empty name) yields an empty client name, which resolves to
    /// <see cref="BifrostHttpResilienceOptions.Default"/>.
    /// </summary>
    /// <param name="builderName">The <see cref="ResilienceHandlerContext.BuilderName"/>.</param>
    /// <param name="pipelineName">The pipeline name passed to <c>AddResilienceHandler</c>.</param>
    /// <returns>The logical HttpClient name.</returns>
    internal static string GetClientName(string builderName, string pipelineName)
    {
        var suffix = "-" + pipelineName;
        return builderName.EndsWith(suffix, StringComparison.Ordinal)
            ? builderName[..^suffix.Length]
            : builderName;
    }

    /// <summary>
    /// Configures a resilience pipeline builder from a <see cref="BifrostHttpResiliencePolicy"/>. Ordered
    /// outermost to innermost: total-request timeout → retry (omitted when
    /// <see cref="BifrostHttpResiliencePolicy.MaxRetries"/> is 0) → circuit breaker (when enabled) →
    /// per-attempt timeout. The retry and circuit-breaker strategies use the HTTP-aware
    /// <c>ShouldHandle</c> defaults (transient 5xx/408/429 responses and network exceptions).
    /// </summary>
    /// <param name="pipelineBuilder">The pipeline builder to configure.</param>
    /// <param name="policy">The resolved policy.</param>
    internal static void ConfigurePipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> pipelineBuilder,
        BifrostHttpResiliencePolicy policy)
    {
        pipelineBuilder.AddTimeout(policy.TotalRequestTimeout);

        if (policy.MaxRetries > 0)
        {
            pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = policy.MaxRetries,
                BackoffType = policy.UseExponentialBackoff
                    ? DelayBackoffType.Exponential
                    : DelayBackoffType.Constant,
                Delay = policy.RetryBaseDelay,
                UseJitter = policy.UseJitter,
            });
        }

        if (policy.EnableCircuitBreaker)
        {
            pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = policy.CircuitBreakerFailureRatio,
                MinimumThroughput = policy.CircuitBreakerMinimumThroughput,
                SamplingDuration = policy.CircuitBreakerSamplingDuration,
                BreakDuration = policy.CircuitBreakerBreakDuration,
            });
        }

        pipelineBuilder.AddTimeout(policy.AttemptTimeout);
    }
}
