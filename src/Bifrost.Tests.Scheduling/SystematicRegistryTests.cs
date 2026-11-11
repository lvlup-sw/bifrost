#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, VSTHRD002, CA2007

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Bifrost.Tests.Systematic;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryTests
{
    internal static readonly DateTimeOffset Epoch = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(17)]
    [Arguments(83)]
    [Arguments(2026)]
    public async Task GeneratedCommittedHistories(int seed)
    {
        var random = new Random(seed);
        for (var sample = 0; sample < 32; sample++)
        {
            var commands = new List<HistoryCommand>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (var step = 0; step < 40; step++)
            {
                var name = "job-" + random.Next(3);
                var kind = present.Contains(name) ? new[] { "update", "pause", "resume", "unregister" }[random.Next(4)] : "register";
                var fault = random.Next(3);
                commands.Add(new(kind + ":" + fault, name, random.Next(1, 61)));
                if (fault == 0 && kind == "register")
                {
                    present.Add(name);
                }

                if (fault == 0 && kind == "unregister")
                {
                    present.Remove(name);
                }
            }

            var history = new HistoryCase(new() { ["seed"] = seed }, commands.ToArray());
            var result = Execute(history);
            await Assert.That(result.FailureClass).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task IndependentJobsCommuteAndReplayUsesExecutor()
    {
        var left = new HistoryCommand[] { new("register:0", "alpha", 3), new("pause:0", "alpha"), new("update:2", "alpha", 9), new("resume:0", "alpha") };
        var right = new HistoryCommand[] { new("register:0", "beta", 5), new("update:0", "beta", 7), new("unregister:1", "beta"), new("pause:0", "beta") };
        var source = new HistoryCase(new(), [.. left, .. right]);
        var followup = source with { Commands = [.. right, .. left] };
        var first = Execute(source);
        var second = Execute(followup);
        await Assert.That(first.FailureClass).IsEqualTo(string.Empty);
        await Assert.That(second.FailureClass).IsEqualTo(string.Empty);
        await Assert.That(first.Actual).IsEqualTo(second.Actual);
        var record = Artifact(source, followup, first);
        var replay = ReplayRecord.FromJson(record.ToJson()).Replay(IsValid, (a, b) =>
        {
            var actual = Execute(a);
            var other = Execute(b!);
            return actual.Actual == other.Actual ? actual : new("independent-order", actual.Actual, other.Actual);
        });
        await Assert.That(replay).IsEqualTo(first);
    }

    [Test]
    public async Task EveryOperationReachesBothFaultBoundaries()
    {
        foreach (var kind in new[] { "register", "update", "pause", "resume", "unregister" })
        {
            foreach (var fault in new[] { 1, 2 })
            {
                var commands = new List<HistoryCommand>();
                if (kind != "register")
                {
                    commands.Add(new("register:0", "job", 3));
                }

                if (kind == "resume")
                {
                    commands.Add(new("pause:0", "job"));
                }

                commands.Add(new(kind + ":" + fault, "job", 7));
                await Assert.That(Execute(new(new(), commands.ToArray())).FailureClass).IsEqualTo(string.Empty);
            }
        }
    }

    internal static ReplayRecord Artifact(HistoryCase source, HistoryCase? followup, HistoryObservation observation) => new(
        1, "d6a4688eb12b542fadb0dfa656285148ee8239aa", true, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        "Debug/net10.0", "registry-committed-state", "memory commits only on returned store success; durable effects may precede a thrown response", source, followup,
        Epoch.Ticks, null, source.Commands.Select(command => command.Kind + "/" + command.Id).ToArray(), null, observation,
        "dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryTests/*'");

    internal static bool IsValid(HistoryCase history)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in history.Commands)
        {
            var parts = command.Kind.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var fault) || fault is < 0 or > 2
                || command.Id is not ("alpha" or "beta" or "job" or "job-0" or "job-1" or "job-2")
                || parts[0] is not ("register" or "update" or "pause" or "resume" or "unregister")
                || (parts[0] is "register" or "update" && command.Value is < 1 or > 60)
                || (parts[0] == "register") == present.Contains(command.Id))
            {
                return false;
            }

            if (fault == 0 && parts[0] == "register")
            {
                present.Add(command.Id);
            }

            if (fault == 0 && parts[0] == "unregister")
            {
                present.Remove(command.Id);
            }
        }

        return true;
    }

    internal static HistoryObservation Execute(HistoryCase history)
    {
        if (!IsValid(history))
        {
            throw new InvalidDataException("Invalid registry history.");
        }

        var store = new FaultStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(Epoch));
        var memory = new SortedDictionary<string, ModelJob>(StringComparer.Ordinal);
        var durable = new SortedDictionary<string, ModelJob>(StringComparer.Ordinal);
        var expectedAttempts = 0;
        var expectedEffects = 0;
        var expectedFaults = 0;
        var actualFaults = 0;
        var expectedWakes = new List<string>();
        var actualWakes = new List<string>();
        foreach (var command in history.Commands)
        {
            var parts = command.Kind.Split(':');
            var kind = parts[0];
            var fault = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            store.Fault = fault;
            var before = memory.GetValueOrDefault(command.Id);
            var after = kind switch
            {
                "register" => new ModelJob(command.Value, JobState.Running),
                "update" => before! with { Minutes = command.Value },
                "pause" => before! with { State = JobState.Paused },
                "resume" => before! with { State = JobState.Running },
                _ => null,
            };
            var noEffect = (kind == "pause" && before!.State == JobState.Paused) || (kind == "resume" && before!.State == JobState.Running);
            if (!noEffect)
            {
                expectedAttempts++;
                if (fault != 1)
                {
                    expectedEffects++;
                    Apply(durable, command.Id, after);
                }

                if (fault == 0)
                {
                    Apply(memory, command.Id, after);
                    expectedWakes.Add(command.Id + ":" + (kind is "register" or "update" ? "Register" : char.ToUpperInvariant(kind[0]) + kind[1..]));
                }
                else
                {
                    expectedFaults++;
                }
            }

            try
            {
                switch (kind)
                {
                    case "register": registry.RegisterAsync(command.Id, Cadence.Interval(TimeSpan.FromMinutes(command.Value)), MissedFirePolicy.Coalesce, new NoopDispatcher()).GetAwaiter().GetResult(); break;
                    case "update": registry.UpdateAsync(command.Id, Cadence.Interval(TimeSpan.FromMinutes(command.Value)), MissedFirePolicy.Coalesce).GetAwaiter().GetResult(); break;
                    case "pause": registry.PauseAsync(command.Id).GetAwaiter().GetResult(); break;
                    case "resume": registry.ResumeAsync(command.Id).GetAwaiter().GetResult(); break;
                    case "unregister": registry.UnregisterAsync(command.Id).GetAwaiter().GetResult(); break;
                }
            }
            catch (InjectedStoreException)
            {
                actualFaults++;
            }

            while (registry.Commands.TryRead(out var wake))
            {
                actualWakes.Add(wake.JobName + ":" + wake.Kind);
            }

            var expected = Render(memory, durable, expectedAttempts, expectedEffects, expectedFaults, expectedWakes);
            var actualMemory = new SortedDictionary<string, ModelJob>(StringComparer.Ordinal);
            foreach (var job in registry.GetJobs())
            {
                actualMemory[job.Name] = new((long)((IntervalCadence)job.Cadence).Interval.TotalMinutes, job.State);
                if (job.NextFireAt != Epoch.AddMinutes(actualMemory[job.Name].Minutes) || job.LastFiredAt is not null || registry.TryGetDispatcher(job.Name) is null)
                {
                    return new("registry-metadata", "computed next fire and live dispatcher", job.ToString());
                }
            }

            var actualDurable = new SortedDictionary<string, ModelJob>(StringComparer.Ordinal);
            foreach (var job in store.Jobs.Values)
            {
                actualDurable[job.Name] = new((long)((IntervalCadence)job.Cadence).Interval.TotalMinutes, job.State);
            }

            var actual = Render(actualMemory, actualDurable, store.Attempts, store.Effects, actualFaults, actualWakes);
            if (expected != actual)
            {
                return new("registry-commit", expected, actual);
            }
        }

        var snapshot = Render(memory, durable, expectedAttempts, expectedEffects, expectedFaults, expectedWakes);
        return new(string.Empty, snapshot, snapshot);
    }

    private static void Apply(SortedDictionary<string, ModelJob> target, string id, ModelJob? job)
    {
        if (job is null)
        {
            target.Remove(id);
        }
        else
        {
            target[id] = job;
        }
    }

    private static string Render(SortedDictionary<string, ModelJob> memory, SortedDictionary<string, ModelJob> durable, int attempts, int effects, int faults, List<string> wakes)
        => $"memory={string.Join(';', memory)}|durable={string.Join(';', durable)}|attempts={attempts}|effects={effects}|faults={faults}|wakes={string.Join(';', wakes.Order(StringComparer.Ordinal))}";

    private sealed record ModelJob(long Minutes, JobState State);

    internal sealed class InjectedStoreException : Exception;

    internal sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    internal sealed class FaultStore : IScheduleStore
    {
        public Dictionary<string, JobRecord> Jobs { get; } = new(StringComparer.Ordinal);
        public int Fault { get; set; }
        public int Attempts { get; private set; }
        public int Effects { get; private set; }
        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => Mutate(() => Jobs[job.Name] = job);
        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => Mutate(() => Jobs.Remove(jobName));
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>([.. Jobs.Values]);
        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
            => Mutate(() => Jobs[jobName] = Jobs[jobName] with { LastFiredAt = firedAt, NextFireAt = nextFireAt });
        private ValueTask Mutate(Action action)
        {
            Attempts++;
            if (Fault == 1)
            {
                throw new InjectedStoreException();
            }

            action();
            Effects++;
            if (Fault == 2)
            {
                throw new InjectedStoreException();
            }

            return ValueTask.CompletedTask;
        }
    }
}
