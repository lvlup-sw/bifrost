// =============================================================================
// <copyright file="WorkOrchestratorBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;
using Bifrost.Handlers;
using Bifrost.Outcomes;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Builder for configuring <see cref="IWorkOrchestrator{TWork}"/> with optional decorators.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
public sealed class WorkOrchestratorBuilder<TWork>
{
    /// <summary>
    /// Gets the service collection for registering additional services.
    /// </summary>
    /// <value>The service collection.</value>
    internal IServiceCollection Services { get; }

    /// <summary>
    /// Gets the list of decorator registrations.
    /// </summary>
    internal List<DecoratorRegistration<TWork>> Decorators { get; } = [];

    /// <summary>
    /// Gets the list of handler decorator registrations applied before the handler is passed to the orchestrator.
    /// </summary>
    /// <value>A list of handler decorator registrations with ordering support.</value>
    internal List<HandlerDecoratorRegistration<TWork>> HandlerDecorators { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a handler has been registered via WithHandler,
    /// WithTaskHandler, or WithScopedHandler.
    /// </summary>
    /// <value>
    /// <c>true</c> if WithHandler, WithTaskHandler, or WithScopedHandler was called; otherwise, <c>false</c>.
    /// </value>
    internal bool HandlerRegistered { get; set; }

    /// <summary>
    /// Gets or sets the service lifetime of the registered handler, if any.
    /// </summary>
    /// <value>
    /// The service lifetime, or <c>null</c> if no handler was registered via WithHandler,
    /// WithTaskHandler, or WithScopedHandler.
    /// </value>
    internal ServiceLifetime? HandlerLifetime { get; set; }

    /// <summary>
    /// Gets or sets the callback for publishing events to the event stream.
    /// </summary>
    /// <value>An action that publishes an event, or <c>null</c> if no event stream is configured.</value>
    internal Action<IOrchestratorEvent>? EventPublishCallback { get; set; }

    /// <summary>
    /// Gets the list of actions to execute after the orchestrator factory has been resolved.
    /// </summary>
    /// <value>A list of post-build actions that receive the service provider.</value>
    internal List<Action<IServiceProvider>> PostBuildActions { get; } = [];

    /// <summary>
    /// Gets or sets the work classifier delegate (DR-2) threaded to the
    /// orchestrator constructor. Precedence rule: a per-call class other than
    /// <see cref="WorkClass.Default"/> wins; a per-call
    /// <see cref="WorkClass.Default"/> defers to the classifier; with no classifier
    /// the class stays <see cref="WorkClass.Default"/>.
    /// </summary>
    /// <value>The classifier delegate, or <c>null</c> when none is configured.</value>
    internal Func<TWork, WorkClass>? Classifier { get; set; }

    /// <summary>
    /// Gets or sets the keyed queue factory set by <c>WithKeyedDispatch</c>. When it is set,
    /// <see cref="Build"/> creates the orchestrator through the queue-factory constructor.
    /// </summary>
    /// <value>The factory, or <c>null</c> when keyed dispatch is not configured.</value>
    internal Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>>? KeyedQueueFactory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>UsePriorityDispatch</c> was called, so that
    /// <see cref="Build"/> can reject it next to keyed dispatch in either call order.
    /// </summary>
    /// <value><c>true</c> if <c>UsePriorityDispatch</c> was called; otherwise, <c>false</c>.</value>
    internal bool PriorityDispatchConfigured { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>WithDeadLetterQueue</c> was called, so that
    /// <see cref="Build"/> wires dead-letter retries and capture into the orchestrator's fixed
    /// terminal outcome stage.
    /// </summary>
    /// <value><c>true</c> if <c>WithDeadLetterQueue</c> was called; otherwise, <c>false</c>.</value>
    internal bool DeadLetterConfigured { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>WithHandlerResilience</c> was called, so that
    /// <see cref="Build"/> can require zero dead-letter retries next to it in either call order.
    /// </summary>
    /// <value><c>true</c> if <c>WithHandlerResilience</c> was called; otherwise, <c>false</c>.</value>
    internal bool HandlerResilienceConfigured { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestratorBuilder{TWork}"/> class.
    /// </summary>
    /// <param name="services">The service collection to register services with.</param>
    internal WorkOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Builds and registers the <see cref="IWorkOrchestrator{TWork}"/> with all configured decorators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method first makes sure that the orchestrator decorators and the handler decorators each
    /// have unique order values. It then sorts both lists by order, innermost first, and copies the
    /// post-build actions so that the factory runs a stable snapshot.
    /// </para>
    /// <para>
    /// The factory resolves the handler through <see cref="ScopedHandlerProxy{TWork}"/> for the
    /// scoped lifetime, and otherwise directly from DI (the existing behavior). It applies the handler
    /// decorators in order, wraps the decorated handler in the base
    /// <see cref="WorkOrchestrator{TWork}"/>, applies the orchestrator decorators in order, and then
    /// runs the post-build actions.
    /// </para>
    /// <para>
    /// Dead-letter retries and capture (<c>WithDeadLetterQueue</c>) and completion events
    /// (<c>WithEventStream</c>) are not handler decorators. The factory passes them to the base
    /// orchestrator's terminal outcome stage from flags read here, so they run outside the
    /// outermost handler decorator for every call order.
    /// </para>
    /// <para>
    /// With <c>WithKeyedDispatch</c>, the base orchestrator is created from the keyed queue factory,
    /// and the autoscaler reads its dispatchable backlog for scale-up. Keyed dispatch combined with
    /// <c>UsePriorityDispatch</c> or <c>WithClassifier</c> is rejected here, in either call order.
    /// A conflicting <see cref="WorkOrchestratorOptions.DispatchStrategy"/> or
    /// <see cref="WorkOrchestratorOptions.FullMode"/> set through options is rejected by options
    /// validation, which runs on start and when the orchestrator is first resolved.
    /// </para>
    /// <para>
    /// With both <c>WithHandlerResilience</c> and <c>WithDeadLetterQueue</c>, handler resilience is
    /// the one retry owner. The method registers options validation that requires
    /// <see cref="Bifrost.Core.DeadLetter.DeadLetterQueueOptions.MaxRetries"/> to be zero. It runs on
    /// start and when the orchestrator is first resolved, because the retry count may come from
    /// configuration that this method cannot read.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when decorator orders repeat, or when keyed dispatch is combined with
    /// <c>UsePriorityDispatch</c> or <c>WithClassifier</c>.
    /// </exception>
    public void Build()
    {
        var duplicateOrders = Decorators
            .GroupBy(d => d.Order)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate decorator order(s): {string.Join(", ", duplicateOrders)}. Each orchestrator decorator must have a unique Order value.");
        }

        var duplicateHandlerOrders = HandlerDecorators
            .GroupBy(d => d.Order)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateHandlerOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate handler decorator order(s): {string.Join(", ", duplicateHandlerOrders)}. Each handler decorator must have a unique Order value.");
        }

        var keyedQueueFactory = KeyedQueueFactory;
        if (keyedQueueFactory is not null && PriorityDispatchConfigured)
        {
            throw new InvalidOperationException(
                "Keyed dispatch cannot be combined with class priority dispatch. Remove UsePriorityDispatch or WithKeyedDispatch.");
        }

        if (keyedQueueFactory is not null && Classifier is not null)
        {
            throw new InvalidOperationException(
                "Keyed dispatch cannot be combined with a work classifier. Remove WithClassifier or WithKeyedDispatch.");
        }

        var orderedDecorators = Decorators.OrderBy(d => d.Order).ToList();

        var orderedHandlerDecorators = HandlerDecorators.OrderBy(d => d.Order).ToList();

        var postBuildActions = PostBuildActions.ToList();
        var handlerLifetime = HandlerLifetime;
        var classifier = Classifier;
        var deadLetterConfigured = DeadLetterConfigured;
        var eventPublisher = EventPublishCallback;

        Services.AddSingleton<IValidateOptions<WorkOrchestratorOptions>>(
            new DispatchStrategyOptionsValidator(WorkOptions.Name<TWork>(), keyedQueueFactory is not null));
        if (keyedQueueFactory is not null)
        {
            Services.TryAddSingleton<DispatchableBacklogSource<TWork>>();
        }

        if (deadLetterConfigured && HandlerResilienceConfigured)
        {
            Services.AddSingleton<IValidateOptions<DeadLetterQueueOptions>>(
                new HandlerRetryOwnerValidator(WorkOptions.Name<TWork>(), typeof(TWork).Name));
        }

        Services.TryAddSingleton(static _ => new TerminalOutcomeCounters<TWork>());

        Services.AddSingleton<IWorkWriter<TWork>>(sp => sp.GetRequiredService<IWorkOrchestrator<TWork>>());
        Services.AddSingleton<IWorkOrchestrator<TWork>>(sp =>
        {
            IWorkHandler<TWork> handler;

            if (handlerLifetime == ServiceLifetime.Scoped)
            {
                handler = new ScopedHandlerProxy<TWork>(
                    sp.GetRequiredService<IServiceScopeFactory>());
            }
            else
            {
                handler = sp.GetRequiredService<IWorkHandler<TWork>>();
            }

            foreach (var registration in orderedHandlerDecorators)
            {
                handler = registration.Factory(sp, handler);
            }

            var options = Options.Create(
                sp.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<TWork>()));
            var logger = sp.GetRequiredService<ILogger<WorkOrchestrator<TWork>>>();
            var outcomes = CreateOutcomeOptions(sp, deadLetterConfigured, eventPublisher);
            var core = new WorkOrchestrator<TWork>(handler, options, logger, null, classifier, keyedQueueFactory, outcomes);
            if (keyedQueueFactory is not null)
            {
                sp.GetRequiredService<DispatchableBacklogSource<TWork>>().Attach(core);
            }

            IWorkOrchestrator<TWork> orchestrator = core;

            foreach (var registration in orderedDecorators)
            {
                orchestrator = registration.Factory(sp, orchestrator);
            }

            foreach (var action in postBuildActions)
            {
                action(sp);
            }

            return orchestrator;
        });
    }

    /// <summary>Resolves the fixed services of the terminal outcome stage.</summary>
    /// <param name="sp">The service provider.</param>
    /// <param name="deadLetterConfigured">Whether <c>WithDeadLetterQueue</c> was called.</param>
    /// <param name="eventPublisher">The event-stream publisher, or <see langword="null"/>.</param>
    /// <returns>The stage options.</returns>
    private static TerminalOutcomeOptions<TWork> CreateOutcomeOptions(
        IServiceProvider sp, bool deadLetterConfigured, Action<IOrchestratorEvent>? eventPublisher)
    {
        var counters = sp.GetRequiredService<TerminalOutcomeCounters<TWork>>();
        if (!deadLetterConfigured)
        {
            return new TerminalOutcomeOptions<TWork> { EventPublisher = eventPublisher, Counters = counters };
        }

        var deadLetterOptions = sp.GetRequiredService<IOptionsMonitor<DeadLetterQueueOptions>>().Get(WorkOptions.Name<TWork>());
        return new TerminalOutcomeOptions<TWork>
        {
            DeadLetterQueue = sp.GetRequiredService<IDeadLetterQueue<TWork>>(),
            DeadLetterRetries = deadLetterOptions.MaxRetries,
            Notifier = sp.GetRequiredService<DeadLetterNotifier<TWork>>(),
            EventPublisher = eventPublisher,
            Counters = counters,
        };
    }
}

/// <summary>
/// Registration for a decorator in the chain.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <param name="Order">The order in which to apply this decorator (lower = innermost).</param>
/// <param name="Factory">Factory function to create the decorator.</param>
internal sealed record DecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkOrchestrator<TWork>, IWorkOrchestrator<TWork>> Factory);

/// <summary>
/// Registration for a handler decorator in the chain.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <param name="Order">The order in which to apply this handler decorator (lower = innermost).</param>
/// <param name="Factory">Factory function to create the handler decorator.</param>
internal sealed record HandlerDecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>> Factory);
