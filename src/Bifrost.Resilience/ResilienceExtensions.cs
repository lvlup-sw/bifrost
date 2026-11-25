// =============================================================================
// <copyright file="ResilienceExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Resilience;

/// <summary>
/// Extension methods for adding resilience support to the work orchestrator.
/// </summary>
public static class ResilienceExtensions
{
    /// <summary>
    /// The decorator order for resilience (inner to autoscaling).
    /// </summary>
    private const int ResilienceDecoratorOrder = 25;

    /// <summary>
    /// Adds admission resilience to the work orchestrator: Polly retry and timeout policies around
    /// <see cref="IWorkWriter{TWork}.EnqueueAsync"/>.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for resiliency settings.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder is null.</exception>
    /// <remarks>
    /// <para>
    /// The policies wrap <see cref="IWorkWriter{TWork}.EnqueueAsync"/> only. Each attempt has a
    /// timeout of <see cref="ResiliencySettings.TimeoutIntervalSeconds"/>. When
    /// <see cref="QueueFullMode.Wait"/> makes the write wait for space, the timeout cancels the
    /// wait. A timed-out attempt, or an inner <c>EnqueueAsync</c> that throws a transient exception
    /// (see <see cref="ResiliencyPolicyGenerator.GetTransientExceptionTypes"/>), is retried up to
    /// <see cref="ResiliencySettings.RetryCount"/> times. Other exceptions, including caller
    /// cancellation, propagate without a retry.
    /// </para>
    /// <para>
    /// The policies do not cover the following:
    /// <list type="bullet">
    ///   <item><description><see cref="IWorkWriter{TWork}.Enqueue"/> and
    ///   <see cref="IWorkWriter{TWork}.TryEnqueue"/> do not go through the policies.</description></item>
    ///   <item><description>Rejections are returned as an <see cref="EnqueueResult"/>, not thrown,
    ///   so they are not retried. This includes <see cref="QueueFullMode.Reject"/> rejections,
    ///   priority dispatch watermark and capacity rejections, and shutdown rejections.</description></item>
    ///   <item><description>Handler failures are not retried by these policies. Only the dead-letter
    ///   queue retries handler failures, and its retries are immediate.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The method registers <see cref="ResiliencySettings"/> as options named by work type, so the
    /// settings of one pipeline cannot replace the settings of another. It adds the
    /// <see cref="ResilientOrchestrator{TWork}"/> decorator at order 25, inner to the event stream
    /// decorator (order 50) and the autoscaling decorator (order 100).
    /// </para>
    /// <para>
    /// This method and <see cref="WithResilience{TWork}"/> may be called any number of times, in any
    /// combination. The decorator is added once, and every <paramref name="configure"/> action is
    /// applied in call order, so a later action wins for a setting that both actions set.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithAdmissionResilience<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<ResiliencySettings>? configure = null)
        => AddAdmissionResilience(builder, configure);

    /// <summary>
    /// Adds admission resilience to the work orchestrator. This is the original name for
    /// <see cref="WithAdmissionResilience{TWork}"/> and behaves the same way.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for resiliency settings.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder is null.</exception>
    /// <remarks>
    /// <para>
    /// The Polly retry and timeout policies wrap <see cref="IWorkWriter{TWork}.EnqueueAsync"/> only.
    /// They do not wrap handler calls, <see cref="IWorkWriter{TWork}.Enqueue"/>, or
    /// <see cref="IWorkWriter{TWork}.TryEnqueue"/>, and they do not retry rejections returned as an
    /// <see cref="EnqueueResult"/>. Only the dead-letter queue retries handler failures. See
    /// <see cref="WithAdmissionResilience{TWork}"/> for the full scope.
    /// </para>
    /// <para>
    /// New code should prefer <see cref="WithAdmissionResilience{TWork}"/>, because its name states
    /// the scope. Both names may be called more than once and in any combination; the configure
    /// actions merge and the decorator is added once.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithResilience<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<ResiliencySettings>? configure = null)
        => AddAdmissionResilience(builder, configure);

    /// <summary>
    /// Registers the admission resilience options and, on the first call for a builder, the
    /// <see cref="ResilientOrchestrator{TWork}"/> decorator.
    /// </summary>
    /// <remarks>
    /// <c>AddOptions(name).Configure</c> adds a new configure action on every call, so the actions
    /// of repeated calls accumulate. A later call finds the decorator already registered at
    /// <see cref="ResilienceDecoratorOrder"/> and does not add a second one, which
    /// <c>Build</c> would reject as a duplicate order.
    /// </remarks>
    private static WorkOrchestratorBuilder<TWork> AddAdmissionResilience<TWork>(
        WorkOrchestratorBuilder<TWork> builder,
        Action<ResiliencySettings>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var optionsName = WorkOptions.Name<TWork>();
        builder.Services.AddOptions<ResiliencySettings>(optionsName)
            .Configure(configure ?? (_ => { }));

        if (builder.Decorators.Any(d => d.Order == ResilienceDecoratorOrder))
        {
            return builder;
        }

        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            ResilienceDecoratorOrder,
            (sp, inner) => new ResilientOrchestrator<TWork>(
                inner,
                Options.Create(sp.GetRequiredService<IOptionsMonitor<ResiliencySettings>>().Get(optionsName)),
                sp.GetRequiredService<ILogger<ResilientOrchestrator<TWork>>>())));

        return builder;
    }
}
