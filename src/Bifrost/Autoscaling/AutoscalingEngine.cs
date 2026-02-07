// =============================================================================
// <copyright file="AutoscalingEngine.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Autoscaling;

/// <summary>
/// Evaluates scaling decisions based on current utilization and configured watermarks.
/// </summary>
/// <remarks>
/// <para>
/// The autoscaling engine uses a watermark-based approach to make scaling decisions:
/// <list type="bullet">
///   <item><description>Scale up when utilization exceeds the high watermark</description></item>
///   <item><description>Scale down when utilization drops below the low watermark</description></item>
///   <item><description>No action when utilization is between watermarks</description></item>
/// </list>
/// </para>
/// <para>
/// A cooldown period prevents oscillation by blocking scaling decisions for a
/// configurable duration after each scaling action.
/// </para>
/// <para>
/// The engine supports timer-based periodic evaluation when started, making it
/// suitable for use as a hosted service. The <see cref="ScalingDecisionMade"/> event
/// is raised after each evaluation.
/// </para>
/// </remarks>
public sealed class AutoscalingEngine : IAutoscalingEngine, IDisposable
{
    private readonly AutoscalingOptions _options;
    private readonly IAutoscalingCoordinator _coordinator;
    private readonly ILogger<AutoscalingEngine> _logger;
    private readonly Lock _scalingLock = new();
    private readonly Timer _evaluationTimer;
    private DateTimeOffset _lastScalingTime = DateTimeOffset.MinValue;
    private volatile bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingEngine"/> class.
    /// </summary>
    /// <param name="options">The autoscaling configuration options.</param>
    /// <param name="coordinator">The coordinator providing worker metrics and scaling operations.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when options, coordinator, or logger is null.</exception>
    public AutoscalingEngine(
        IOptions<AutoscalingOptions> options,
        IAutoscalingCoordinator coordinator,
        ILogger<AutoscalingEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _coordinator = coordinator;
        _logger = logger;

        // Calculate effective minimum workers
        var initialWorkerCount = _coordinator.ActiveWorkerCount;
        EffectiveMinWorkers = Math.Max(initialWorkerCount, _options.MinWorkers);

        // Log warning if there's a mismatch
        if (initialWorkerCount != _options.MinWorkers)
        {
            _logger.LogWarning(
                "Config mismatch: InitialWorkers={InitialWorkers}, MinWorkers={ConfiguredMinWorkers}, using EffectiveMin={EffectiveMinWorkers}",
                initialWorkerCount,
                _options.MinWorkers,
                EffectiveMinWorkers);
        }

        // Create timer but don't start it yet
        _evaluationTimer = new Timer(
            EvaluateScalingCallback,
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingEngine"/> class for backward compatibility.
    /// </summary>
    /// <param name="options">The autoscaling configuration options.</param>
    /// <exception cref="ArgumentNullException">Thrown when options is null.</exception>
    /// <remarks>
    /// This constructor is provided for backward compatibility with existing tests.
    /// New code should use the constructor that accepts <see cref="IAutoscalingCoordinator"/>.
    /// </remarks>
    public AutoscalingEngine(IOptions<AutoscalingOptions> options)
        : this(options, new NullAutoscalingCoordinator(), NullLogger<AutoscalingEngine>.Instance)
    {
    }

    /// <inheritdoc/>
    public bool IsRunning => _isRunning;

    /// <inheritdoc/>
    public int EffectiveMinWorkers { get; }

    /// <inheritdoc/>
    public AutoscalingOptions Configuration => _options;

    /// <inheritdoc/>
    public event EventHandler<ScalingDecisionEventArgs>? ScalingDecisionMade;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        _evaluationTimer.Change(TimeSpan.Zero, _options.CheckInterval);
        _isRunning = true;

        _logger.LogInformation(
            "Autoscaling engine started with CheckInterval={CheckInterval}, EffectiveMinWorkers={EffectiveMinWorkers}",
            _options.CheckInterval,
            EffectiveMinWorkers);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            return Task.CompletedTask;
        }

        _evaluationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isRunning = false;

        _logger.LogInformation("Autoscaling engine stopped");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingDecision EvaluateScaling()
    {
        var currentWorkers = _coordinator.ActiveWorkerCount;
        var utilization = _coordinator.GetUtilizationRatio();

        return EvaluateScaling(currentWorkers, utilization, maxBacklog: 0);
    }

    /// <summary>
    /// Evaluates whether scaling is needed based on current state.
    /// </summary>
    /// <param name="currentWorkers">The current number of active workers.</param>
    /// <param name="utilization">The current utilization ratio (0.0 to 1.0).</param>
    /// <param name="maxBacklog">The maximum backlog capacity (not used in decision, for context).</param>
    /// <returns>A scaling decision indicating what action to take.</returns>
    /// <remarks>
    /// This method is thread-safe. Concurrent calls are serialized to ensure the cooldown
    /// period is respected and only one scaling decision is made per cooldown interval.
    /// </remarks>
    public ScalingDecision EvaluateScaling(int currentWorkers, double utilization, int maxBacklog)
    {
        lock (_scalingLock)
        {
            // Check cooldown period
            var now = DateTimeOffset.UtcNow;
            var timeSinceLastScaling = now - _lastScalingTime;

            if (timeSinceLastScaling < _options.CooldownPeriod)
            {
                return new ScalingDecision(
                    ScalingAction.None,
                    currentWorkers,
                    currentWorkers,
                    utilization,
                    $"In cooldown period ({_options.CooldownPeriod.TotalSeconds:F0}s remaining)");
            }

            // Check for scale up
            if (utilization > _options.HighWatermark)
            {
                if (currentWorkers >= _options.MaxWorkers)
                {
                    return new ScalingDecision(
                        ScalingAction.None,
                        currentWorkers,
                        currentWorkers,
                        utilization,
                        $"At max workers limit ({_options.MaxWorkers})");
                }

                var targetWorkers = Math.Min(currentWorkers + _options.ScaleUpStep, _options.MaxWorkers);
                _lastScalingTime = now;

                return new ScalingDecision(
                    ScalingAction.ScaleUp,
                    currentWorkers,
                    targetWorkers,
                    utilization,
                    $"Utilization ({utilization:P0}) exceeds high watermark ({_options.HighWatermark:P0})");
            }

            // Check for scale down - use EffectiveMinWorkers instead of raw MinWorkers
            if (utilization < _options.LowWatermark)
            {
                if (currentWorkers <= EffectiveMinWorkers)
                {
                    return new ScalingDecision(
                        ScalingAction.None,
                        currentWorkers,
                        currentWorkers,
                        utilization,
                        $"At min workers limit ({EffectiveMinWorkers})");
                }

                var targetWorkers = Math.Max(currentWorkers - _options.ScaleDownStep, EffectiveMinWorkers);
                _lastScalingTime = now;

                return new ScalingDecision(
                    ScalingAction.ScaleDown,
                    currentWorkers,
                    targetWorkers,
                    utilization,
                    $"Utilization ({utilization:P0}) below low watermark ({_options.LowWatermark:P0})");
            }

            // No scaling needed
            return new ScalingDecision(
                ScalingAction.None,
                currentWorkers,
                currentWorkers,
                utilization,
                $"Utilization ({utilization:P0}) within normal range");
        }
    }

    /// <summary>
    /// Releases the resources used by the autoscaling engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _evaluationTimer.Dispose();
        _disposed = true;
    }

    private void EvaluateScalingCallback(object? state)
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            var decision = EvaluateScaling();
            var correlationId = Guid.NewGuid().ToString();

            // Raise the event
            OnScalingDecisionMade(new ScalingDecisionEventArgs(decision, correlationId));

            // Execute the scaling action if needed (fire-and-forget, intentionally not awaited)
            if (decision.Action != ScalingAction.None)
            {
                _ = ExecuteScalingDecisionAsync(decision);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scaling evaluation");
        }
    }

    private void OnScalingDecisionMade(ScalingDecisionEventArgs args)
    {
        try
        {
            ScalingDecisionMade?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ScalingDecisionMade event handler");
        }
    }

    private async Task ExecuteScalingDecisionAsync(ScalingDecision decision)
    {
        try
        {
            switch (decision.Action)
            {
                case ScalingAction.ScaleUp:
                    var countToAdd = decision.TargetWorkers - decision.CurrentWorkers;
                    await _coordinator.RequestScaleUpAsync(countToAdd).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Scaled up by {Count} workers: {CurrentWorkers} -> {TargetWorkers}",
                        countToAdd,
                        decision.CurrentWorkers,
                        decision.TargetWorkers);
                    break;

                case ScalingAction.ScaleDown:
                    var countToRemove = decision.CurrentWorkers - decision.TargetWorkers;
                    await _coordinator.RequestScaleDownAsync(countToRemove).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Scaled down by {Count} workers: {CurrentWorkers} -> {TargetWorkers}",
                        countToRemove,
                        decision.CurrentWorkers,
                        decision.TargetWorkers);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing scaling decision: {Action}", decision.Action);
        }
    }

    /// <summary>
    /// A null implementation of <see cref="IAutoscalingCoordinator"/> for backward compatibility.
    /// </summary>
    private sealed class NullAutoscalingCoordinator : IAutoscalingCoordinator
    {
        // IAutoscalingMetricsPort
        public int MaxBacklog => 100;
        public int PendingWorkCount => 0;
        public int ActiveWorkerCount => 1;
        public long QueuedCount => 0;
        public double GetUtilizationRatio() => 0.0;

        // IAutoscalingControlPort
        public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Func<string, CancellationToken, Task> CreateWorkerFunction()
            => (_, _) => Task.CompletedTask;
        public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
            => (_, _) => Task.CompletedTask;
        public CancellationToken GetShutdownToken()
            => CancellationToken.None;

        // IAutoscalingEventsPort
        public void PublishEvent(Core.Events.IOrchestratorEvent orchestratorEvent) { }
        public long QueuedEventCount => 0;
        public long ActiveSubscriberCount => 0;
    }
}