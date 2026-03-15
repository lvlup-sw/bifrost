// =============================================================================
// <copyright file="ResilientOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;
using Polly.Wrap;

namespace Bifrost.Resilience;

/// <summary>
/// Decorator that adds Polly resilience policies to a work orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This decorator wraps an <see cref="IWorkOrchestrator{TWork}"/> and applies
/// Polly resilience policies to enqueue operations. This provides automatic
/// retry, timeout, and circuit breaker protection.
/// </para>
/// <para>
/// The resilience policies are configured via <see cref="ResiliencySettings"/>
/// and include retry with exponential backoff and timeout handling.
/// </para>
/// </remarks>
public sealed class ResilientOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly IWorkOrchestrator<TWork> _inner;
    private readonly AsyncPolicyWrap _policy;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner orchestrator to wrap.</param>
    /// <param name="options">The resiliency settings options.</param>
    /// <param name="logger">The logger for policy events.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public ResilientOrchestrator(
        IWorkOrchestrator<TWork> inner,
        IOptions<ResiliencySettings> options,
        ILogger<ResilientOrchestrator<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;

        var settings = options.Value;
        var exceptionPredicate = ResiliencyPolicyGenerator.CreateExceptionPredicate(
            ResiliencyPolicyGenerator.GetTransientExceptionTypes());

        _policy = ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(
            logger,
            settings,
            exceptionPredicate);
    }

    /// <inheritdoc/>
    public int PendingCount => _inner.PendingCount;

    /// <inheritdoc/>
    public int ActiveWorkers => _inner.ActiveWorkers;

    /// <inheritdoc/>
    public int Capacity => _inner.Capacity;

    /// <inheritdoc/>
    public ChannelWriter<TWork> Writer => _inner.Writer;

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)
    {
        await _policy.ExecuteAsync(
            async (context, cancellationToken) =>
            {
                await _inner.EnqueueAsync(work, cancellationToken).ConfigureAwait(false);
            },
            new Context("EnqueueAsync"),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work)
    {
        // TryEnqueue is synchronous, so we don't wrap it with async policies
        return _inner.TryEnqueue(work);
    }

    /// <inheritdoc/>
    public void Run(TWork work)
        => _inner.Run(work);

    /// <inheritdoc/>
    public bool TryRun(TWork work)
        => _inner.TryRun(work);

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => _inner.CreateWorkerFunction();

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => _inner.CreateWorkerFunction(stateCallback);

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleUpAsync(count, cancellationToken);

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleDownAsync(count, cancellationToken);

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken()
        => _inner.GetShutdownToken();

    /// <inheritdoc/>
    public Task DrainAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        return _inner.StopAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}