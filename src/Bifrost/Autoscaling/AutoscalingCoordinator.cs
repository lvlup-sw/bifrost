// =============================================================================
// <copyright file="AutoscalingCoordinator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling.Ports;
using Bifrost.Core;
using Bifrost.Core.Events;

namespace Bifrost.Autoscaling;

/// <summary>
/// Stateless mediator that delegates all operations to the underlying work orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item being orchestrated.</typeparam>
/// <remarks>
/// <para>
/// This implementation of <see cref="IAutoscalingCoordinator"/> provides a thin
/// adapter layer between the autoscaling components and the work orchestrator.
/// It resolves the circular dependency that would otherwise exist if autoscaling
/// components depended directly on the orchestrator.
/// </para>
/// <para>
/// The coordinator is intentionally stateless - it merely delegates calls to the
/// underlying orchestrator and worker registry. This design ensures:
/// <list type="bullet">
///   <item><description>Single source of truth for metrics (the orchestrator)</description></item>
///   <item><description>No state synchronization issues</description></item>
///   <item><description>Thread-safe by delegation</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class AutoscalingCoordinator<TWork> : IAutoscalingCoordinator
{
    private readonly IWorkOrchestrator<TWork> _orchestrator;
    private readonly IWorkerRegistry _registry;
    private readonly Action<IOrchestratorEvent>? _publishCallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingCoordinator{TWork}"/> class.
    /// </summary>
    /// <param name="orchestrator">The work orchestrator to coordinate with.</param>
    /// <param name="registry">The worker registry for dynamic worker management.</param>
    /// <param name="publishCallback">Optional callback for publishing events to the event stream.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="orchestrator"/> or <paramref name="registry"/> is null.</exception>
    public AutoscalingCoordinator(
        IWorkOrchestrator<TWork> orchestrator,
        IWorkerRegistry registry,
        Action<IOrchestratorEvent>? publishCallback = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(registry);
        _orchestrator = orchestrator;
        _registry = registry;
        _publishCallback = publishCallback;
    }

    #region IAutoscalingMetricsPort

    /// <inheritdoc/>
    public int MaxBacklog => _orchestrator.Capacity;

    /// <inheritdoc/>
    public int PendingWorkCount => _orchestrator.PendingCount;

    /// <inheritdoc/>
    public int ActiveWorkerCount => _orchestrator.ActiveWorkers;

    /// <inheritdoc/>
    /// <remarks>
    /// Currently returns 0 as a placeholder. This will be connected to the
    /// event stream decorator in a future phase.
    /// </remarks>
    public long QueuedCount => 0L;

    /// <inheritdoc/>
    public double GetUtilizationRatio()
    {
        // Prevent division by zero
        if (_orchestrator.Capacity == 0)
        {
            return 0.0;
        }

        return (double)_orchestrator.PendingCount / _orchestrator.Capacity;
    }

    #endregion

    #region IAutoscalingControlPort

    /// <inheritdoc/>
    public async Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            var workerId = $"Coordinator-{Guid.NewGuid():N}";
            var workerFunc = _orchestrator.CreateWorkerFunction();
            await _registry.CreateWorkerAsync(workerId, workerFunc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
    {
        _registry.RequestMultipleWorkerStop(count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
    {
        return _orchestrator.CreateWorkerFunction();
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
    {
        return _orchestrator.CreateWorkerFunction(stateCallback);
    }

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken()
    {
        return _orchestrator.GetShutdownToken();
    }

    #endregion

    #region IAutoscalingEventsPort

    /// <inheritdoc/>
    public void PublishEvent(IOrchestratorEvent orchestratorEvent)
    {
        _publishCallback?.Invoke(orchestratorEvent);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns 0 as a placeholder. Real event count tracking would require
    /// coupling to the event stream implementation.
    /// </remarks>
    public long QueuedEventCount => 0L;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns 0 as a placeholder. Real subscriber count tracking would require
    /// coupling to the event stream implementation.
    /// </remarks>
    public long ActiveSubscriberCount => 0L;

    #endregion
}
