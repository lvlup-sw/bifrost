// =============================================================================
// <copyright file="ResilienceExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    ///   <item><description>Handler failures are not retried by these policies. Use
    ///   <see cref="WithHandlerResilience{TWork}"/> for handler timeouts and retries. Without it, only
    ///   the dead-letter queue retries handler failures, and its retries are immediate.</description></item>
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
    /// <see cref="EnqueueResult"/>. Handler timeouts and retries come from
    /// <see cref="WithHandlerResilience{TWork}"/> or the dead-letter queue. See
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
    /// Adds handler resilience: a Polly v8 pipeline around each handler call, with a cooperative
    /// per-attempt timeout and short, opt-in retries for failures the caller classifies as transient.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for the handler resilience settings.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The defaults add no timeout and no retries. Set
    /// <see cref="HandlerResilienceOptions.AttemptTimeout"/> for a per-attempt timeout, and set
    /// <see cref="HandlerResilienceOptions.MaxRetries"/> with
    /// <see cref="HandlerResilienceOptions.ShouldRetry"/> to retry classified transient failures, for
    /// example:
    /// <code>
    /// builder.WithHandlerResilience(o =>
    /// {
    ///     o.AttemptTimeout = TimeSpan.FromSeconds(30);
    ///     o.MaxRetries = 2;
    ///     o.ShouldRetry = static ex => ex is HttpRequestException;
    /// });
    /// </code>
    /// A failure after the orchestrator's handler token is cancelled (host shutdown or
    /// <c>AbortAsync</c>) is never retried.
    /// </para>
    /// <para>
    /// <b>Placement.</b> The decorator uses a reserved handler decorator order, so for every builder
    /// call order it is the outermost handler decorator: inside the orchestrator's dead-letter retries
    /// and capture, and outside every <c>WithHandlerDecorator</c> decorator and the scoped handler
    /// proxy. Each attempt therefore gets a fresh DI scope (with <c>WithScopedHandler</c> or a scoped
    /// <c>WithHandler</c>), every handler decorator sees each attempt, and the scope is disposed
    /// before the retry delay starts.
    /// </para>
    /// <para>
    /// <b>One retry owner.</b> With <c>WithDeadLetterQueue</c>, handler resilience owns handler
    /// retries and <see cref="Bifrost.Core.DeadLetter.DeadLetterQueueOptions.MaxRetries"/> must be
    /// 0, for example <c>WithDeadLetterQueue(o =&gt; o.MaxRetries = 0)</c>. The dead-letter queue then
    /// captures the final failure once, after the last attempt. A non-zero value fails options
    /// validation on host start, or on the first resolution of the orchestrator without a host,
    /// because the value may come from configuration that <c>Build</c> cannot read.
    /// </para>
    /// <para>
    /// <b>Slots and attempts.</b> The worker and, with <c>WithKeyedDispatch</c>, the item's key slot
    /// are held across all attempts, retry delays, and scope disposals. A handler that ignores the
    /// timeout's cancellation keeps both until it exits. The item records one terminal outcome, and
    /// its attempt count (also in <c>bifrost.orchestrator.handler.attempts</c>) includes every
    /// retry. A timed-out attempt fails with Polly's <c>TimeoutRejectedException</c> and is recorded
    /// as failed, not canceled.
    /// </para>
    /// <para>
    /// Timeouts and retry delays use the <see cref="TimeProvider"/> registered in the service
    /// collection, or <see cref="TimeProvider.System"/>. The settings are options named by work type
    /// and validated on start. This method may be called more than once: every
    /// <paramref name="configure"/> action is applied in call order and the decorator is added once.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithHandlerResilience<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<HandlerResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var optionsName = WorkOptions.Name<TWork>();
        builder.Services.AddOptions<HandlerResilienceOptions>(optionsName)
            .Configure(configure ?? (_ => { }))
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HandlerResilienceOptions>, HandlerResilienceOptionsValidator>());
        builder.HandlerResilienceConfigured = true;

        if (builder.HandlerDecorators.Any(d => d.Order == HandlerDecoratorOrders.HandlerResilience))
        {
            return builder;
        }

        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
            HandlerDecoratorOrders.HandlerResilience,
            (sp, inner) => new ResilientWorkHandler<TWork>(
                inner,
                sp.GetRequiredService<IOptionsMonitor<HandlerResilienceOptions>>().Get(optionsName),
                sp.GetRequiredService<ILogger<ResilientWorkHandler<TWork>>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System)));

        return builder;
    }

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
