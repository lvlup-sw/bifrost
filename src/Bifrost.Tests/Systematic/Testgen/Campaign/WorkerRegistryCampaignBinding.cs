// =============================================================================
// <copyright file="WorkerRegistryCampaignBinding.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Text.Json;

using Bifrost.Autoscaling;

namespace Bifrost.Tests.Systematic.Testgen.Campaign;

/// <summary>
/// Stateful binding for issue #111 against the public <see cref="WorkerRegistry"/>
/// API. The model is an independent dictionary; it never reads target state to
/// decide expected state or enabled operations. Worker delegates finish only
/// when the binding releases them, so sequential histories are deterministic.
/// </summary>
internal static class WorkerRegistryCampaignBinding
{
    /// <summary>Per-worker model state.</summary>
    /// <param name="Idle">Whether the worker is idle.</param>
    /// <param name="Stopped">Whether a stop was requested.</param>
    internal sealed record State(bool Idle, bool Stopped);

    /// <summary>Application-side state: the registry plus release gates.</summary>
    internal sealed class SystemState
    {
        /// <summary>Gets the registry under test.</summary>
        public WorkerRegistry Registry { get; } = new();

        /// <summary>Gets the release gates keyed by worker id.</summary>
        public Dictionary<string, TaskCompletionSource> Releases { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Observed snapshot compared against the model.</summary>
    /// <param name="Workers">Per-worker observed state.</param>
    /// <param name="Total">Snapshot total.</param>
    /// <param name="Active">Snapshot active.</param>
    /// <param name="Idle">Snapshot idle.</param>
    /// <param name="Busy">Snapshot busy.</param>
    /// <param name="Stopping">Snapshot stopping.</param>
    /// <param name="IdleCount">The registry idle count property.</param>
    internal sealed record Observation(
        Dictionary<string, State> Workers, int Total, int Active, int Idle, int Busy, int Stopping, int IdleCount);

    /// <summary>Creates the empty independent model.</summary>
    /// <returns>An empty model.</returns>
    public static Task<Dictionary<string, State>> CreateModel()
        => Task.FromResult(new Dictionary<string, State>(StringComparer.Ordinal));

    /// <summary>Creates a fresh registry.</summary>
    /// <returns>The system state.</returns>
    public static Task<SystemState> CreateSystem() => Task.FromResult(new SystemState());

    /// <summary>Releases every worker and waits (bounded) for the registry to empty.</summary>
    /// <param name="system">The system state.</param>
    /// <returns>A task that completes when cleanup finishes.</returns>
    public static async Task DisposeSystem(SystemState system)
    {
        foreach (var release in system.Releases.Values)
        {
            release.TrySetResult();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (system.Registry.ActiveWorkerCount != 0)
        {
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Model precondition: create needs a fresh id, other operations need an existing id.</summary>
    /// <param name="model">The model.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="args">The worker id argument.</param>
    /// <returns><see langword="true"/> when the operation is enabled.</returns>
    public static bool IsEnabled(Dictionary<string, State> model, string operation, JsonElement args)
        => operation == "create" ? !model.ContainsKey(args.GetString()!) : model.ContainsKey(args.GetString()!);

    /// <summary>Applies an operation to the independent model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="args">The worker id argument.</param>
    /// <returns>The next model.</returns>
    public static Task<Dictionary<string, State>> Transition(
        Dictionary<string, State> model, string operation, JsonElement args)
    {
        var next = new Dictionary<string, State>(model, StringComparer.Ordinal);
        var id = args.GetString()!;
        switch (operation)
        {
            case "create": next.Add(id, new State(true, false)); break;
            case "idle": next[id] = next[id] with { Idle = true }; break;
            case "busy": next[id] = next[id] with { Idle = false }; break;
            case "stop": next[id] = next[id] with { Stopped = true }; break;
            case "finish": next.Remove(id); break;
            default: throw new ArgumentException("Unknown model operation.", nameof(operation));
        }

        return Task.FromResult(next);
    }

    /// <summary>Executes an operation on the registry through its public API.</summary>
    /// <param name="system">The system state.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="args">The worker id argument.</param>
    /// <returns>A task that completes when the operation took effect.</returns>
    public static async Task Execute(SystemState system, string operation, JsonElement args)
    {
        var id = args.GetString()!;
        switch (operation)
        {
            case "create":
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                system.Releases.Add(id, release);
                await system.Registry.CreateWorkerAsync(
                    id, (_, _) => release.Task.WaitAsync(CancellationToken.None), CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            case "idle": system.Registry.GetWorkerInfo(id)!.MarkIdle(); break;
            case "busy": system.Registry.GetWorkerInfo(id)!.MarkBusy(); break;
            case "stop": system.Registry.RequestWorkerStop(id); break;
            case "finish":
                system.Releases[id].TrySetResult();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    while (system.Registry.GetWorkerInfo(id) is not null)
                    {
                        await Task.Delay(1, timeout.Token).ConfigureAwait(false);
                    }
                }

                system.Releases.Remove(id);
                break;
            default: throw new ArgumentException("Unknown operation.", nameof(operation));
        }
    }

    /// <summary>Observes per-worker state and snapshot partitions.</summary>
    /// <param name="system">The system state.</param>
    /// <returns>The observation.</returns>
    public static Task<Observation> Observe(SystemState system)
    {
        var workers = system.Registry.GetAllWorkers()
            .ToDictionary(w => w.WorkerId, w => new State(w.IsIdle, w.StopRequested), StringComparer.Ordinal);
        var snapshot = system.Registry.GetSnapshot();
        return Task.FromResult(new Observation(
            workers, snapshot.TotalWorkers, snapshot.ActiveWorkers, snapshot.IdleWorkers,
            snapshot.BusyWorkers, snapshot.StoppingWorkers, system.Registry.IdleWorkerCount));
    }

    /// <summary>Compares the model against the observation, including count partitions.</summary>
    /// <param name="model">The model.</param>
    /// <param name="observed">The observation.</param>
    /// <returns><see langword="true"/> when model and observation agree.</returns>
    public static Task<bool> Equivalent(Dictionary<string, State> model, Observation observed)
        => Task.FromResult(
            model.Count == observed.Workers.Count
            && model.All(pair => observed.Workers.TryGetValue(pair.Key, out var value) && pair.Value == value)
            && observed.Total == model.Count && observed.Active == model.Count
            && observed.Idle == model.Values.Count(value => value.Idle && !value.Stopped)
            && observed.IdleCount == observed.Idle
            && observed.Busy == model.Values.Count(value => !value.Idle && !value.Stopped)
            && observed.Stopping == model.Values.Count(value => value.Stopped));
}
