// =============================================================================
// <copyright file="WorkOrchestratorHostedService.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Hosting;

/// <summary>
/// Hosted service that manages the lifecycle of a work orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This service integrates the <see cref="IWorkOrchestrator{TWork}"/> with the
/// ASP.NET Core hosting lifecycle, ensuring proper startup and graceful shutdown.
/// </para>
/// <para>
/// Workers are started on orchestrator construction (via dependency injection),
/// so <see cref="StartAsync"/> completes immediately. <see cref="StopAsync"/>
/// triggers graceful shutdown, allowing pending work items to complete.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Register orchestrator with hosted service
/// services.AddWorkOrchestrator&lt;MyWork&gt;()
///     .WithHandler&lt;MyWorkHandler&gt;()
///     .Build();
/// services.AddWorkOrchestratorHostedService&lt;MyWork&gt;();
/// </code>
/// </example>
public sealed class WorkOrchestratorHostedService<TWork> : IHostedService
{
    private readonly IWorkOrchestrator<TWork> _orchestrator;
    private readonly ILogger<WorkOrchestratorHostedService<TWork>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestratorHostedService{TWork}"/> class.
    /// </summary>
    /// <param name="orchestrator">The work orchestrator to manage.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public WorkOrchestratorHostedService(
        IWorkOrchestrator<TWork> orchestrator,
        ILogger<WorkOrchestratorHostedService<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Starts the hosted service.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel startup.</param>
    /// <returns>A <see cref="Task"/> that completes when startup is done.</returns>
    /// <remarks>
    /// Workers are started on orchestrator construction, so this method
    /// completes immediately after logging the startup message.
    /// </remarks>
    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "WorkOrchestrator<{WorkType}> started with {WorkerCount} workers and capacity {Capacity}",
            typeof(TWork).Name,
            _orchestrator.ActiveWorkers,
            _orchestrator.Capacity);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gracefully stops the hosted service.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel shutdown.</param>
    /// <returns>A <see cref="Task"/> that completes when the orchestrator has stopped.</returns>
    /// <remarks>
    /// This method calls <see cref="IWorkOrchestrator{TWork}.StopAsync"/> to trigger
    /// graceful shutdown, allowing pending work items to complete before the channel
    /// is closed.
    /// </remarks>
    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "WorkOrchestrator<{WorkType}> stopping with {PendingCount} pending items...",
            typeof(TWork).Name,
            _orchestrator.PendingCount);

        await _orchestrator.StopAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "WorkOrchestrator<{WorkType}> stopped",
            typeof(TWork).Name);
    }
}