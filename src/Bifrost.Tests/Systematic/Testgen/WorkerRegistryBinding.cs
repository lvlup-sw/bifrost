using System.Text.Json;

using Bifrost.Autoscaling;

namespace Bifrost.Tests.Systematic.Testgen;

internal static class WorkerRegistryBinding
{
    internal sealed record State(bool Idle, bool Stopped);

    internal sealed class SystemState
    {
        public WorkerRegistry Registry { get; } = new();
        public Dictionary<string, TaskCompletionSource> Releases { get; } = new(StringComparer.Ordinal);
    }

    public static Task<Dictionary<string, State>> CreateModel() =>
        Task.FromResult(new Dictionary<string, State>(StringComparer.Ordinal));

    public static Task<SystemState> CreateSystem() => Task.FromResult(new SystemState());

    public static async Task DisposeSystem(SystemState system)
    {
        foreach (var release in system.Releases.Values) release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (system.Registry.ActiveWorkerCount != 0)
        {
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        }
    }

    public static bool IsEnabled(Dictionary<string, State> model, string operation, JsonElement args) =>
        operation == "create" ? !model.ContainsKey(args.GetString()!) : model.ContainsKey(args.GetString()!);

    public static Task<Dictionary<string, State>> Transition(Dictionary<string, State> model, string operation, JsonElement args)
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

    public static async Task Execute(SystemState system, string operation, JsonElement args)
    {
        var id = args.GetString()!;
        switch (operation)
        {
            case "create":
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                system.Releases.Add(id, release);
                await system.Registry.CreateWorkerAsync(id, (_, _) => release.Task.WaitAsync(TimeSpan.FromSeconds(30)), CancellationToken.None).ConfigureAwait(false);
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
            default: throw new ArgumentException("Unknown system operation.", nameof(operation));
        }
    }

    public static Task<Observation> Observe(SystemState system)
    {
        var workers = system.Registry.GetAllWorkers().ToDictionary(
            worker => worker.WorkerId, worker => new State(worker.IsIdle, worker.StopRequested), StringComparer.Ordinal);
        var snapshot = system.Registry.GetSnapshot();
        return Task.FromResult(new Observation(workers, snapshot.TotalWorkers, snapshot.ActiveWorkers,
            snapshot.IdleWorkers, snapshot.BusyWorkers, snapshot.StoppingWorkers, system.Registry.IdleWorkerCount));
    }

    internal sealed record Observation(Dictionary<string, State> Workers, int Total, int Active, int Idle, int Busy, int Stopping, int IdleCount);

    public static Task<bool> Equivalent(Dictionary<string, State> model, Observation observed) => Task.FromResult(
        model.Count == observed.Workers.Count && model.All(pair => observed.Workers.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        observed.Total == model.Count && observed.Active == model.Count &&
        observed.Idle == model.Values.Count(value => value.Idle && !value.Stopped) && observed.IdleCount == observed.Idle &&
        observed.Busy == model.Values.Count(value => !value.Idle && !value.Stopped) &&
        observed.Stopping == model.Values.Count(value => value.Stopped));
}
