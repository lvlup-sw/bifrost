#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007, VSTHRD002

using System.Collections.Concurrent;
using Bifrost.Tests.Systematic;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryRestartReplayTests
{
    [Test]
    [Arguments(0, "after-run")]
    [Arguments(1, "after-run")]
    [Arguments(2, "after-run")]
    [Arguments(0, "before-checkpoint")]
    [Arguments(0, "after-checkpoint")]
    public async Task PersistedRestartHistoryExecutesComponent(int fault, string cut)
        => await PersistAndReplay(fault, cut, false);

    private static async Task PersistAndReplay(int fault, string cut, bool temporary)
    {
        var source = new HistoryCase(new() { ["checkpointFault"] = fault },
            [new("register", "job", 1), new("advance", "job", 1), new("freeze", cut), new("restart", "job", 1)]);
        var expected = Expected(source);
        var order = new List<string> { "register", "advance", "synchronous-dispatch", "checkpoint-before" };
        if (cut == "before-checkpoint") order.Add("freeze:" + cut);
        if (fault != 1)
        {
            order.Add("checkpoint-effect");
            order.Add("checkpoint-after");
            if (cut == "after-checkpoint") order.Add("freeze:" + cut);
        }

        if (fault != 0) order.Add("checkpoint-throw:" + fault);
        if (cut == "after-run") order.Add("freeze:" + cut);
        order.Add("restart");
        var record = SystematicRegistryTests.Artifact(source, null, new("", expected, expected)) with
        {
            Invariant = "registry-restart-checkpoint",
            OracleRelation = "successful handoff precedes checkpoint; durable frozen acknowledgment suppresses replay",
            FaultPosition = fault == 0 ? null : 1,
            ActorOrder = order.ToArray(),
            UncontrolledScheduleLimitation = "synthetic durable image; inline synchronous router; no OS crash or real storage",
            ReplayCommand = "BIFROST_REGISTRY_RESTART_REPLAY=/absolute/path.json dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryRestartReplayTests/ReplaySavedRestartHistory'",
        };
        var directory = (temporary ? null : Environment.GetEnvironmentVariable("BIFROST_REGISTRY_ARTIFACTS")) ?? Path.Combine(Path.GetTempPath(), "bifrost-registry-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"restart-{fault}-{cut}.json");
        record = SystematicRegistryTests.WithReplayPath(record, path, "BIFROST_REGISTRY_RESTART_REPLAY", "SystematicRegistryRestartReplayTests/ReplaySavedRestartHistory");
        await File.WriteAllTextAsync(path, record.ToJson());
        try
        {
            var actual = await ReplayFile(path);
            await Assert.That(actual).IsEqualTo(record.Observation);
        }
        finally
        {
            if (temporary || Environment.GetEnvironmentVariable("BIFROST_REGISTRY_ARTIFACTS") is null)
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    public async Task ReplaySavedRestartHistory()
    {
        var path = Environment.GetEnvironmentVariable("BIFROST_REGISTRY_RESTART_REPLAY");
        if (path is null)
        {
            await PersistAndReplay(0, "before-checkpoint", true);
            return;
        }

        var record = ReplayRecord.FromJson(await File.ReadAllTextAsync(path));
        await Assert.That(await ReplayFile(path)).IsEqualTo(record.Observation);
    }

    private static async Task<HistoryObservation> ReplayFile(string path)
    {
        var record = ReplayRecord.FromJson(await File.ReadAllTextAsync(path));
        if (record.Invariant != "registry-restart-checkpoint" || record.Followup is not null
            || record.LogicalClockTicks != SystematicRegistryTests.Epoch.Ticks)
        {
            throw new InvalidDataException("Unsupported restart replay metadata.");
        }

        return record.Replay(IsValid, (source, _) => Execute(source).GetAwaiter().GetResult());
    }

    private static bool IsValid(HistoryCase source) => source.Parameters is null && source.Configuration.Count == 1
        && source.Configuration.TryGetValue("checkpointFault", out var fault) && fault is >= 0 and <= 2
        && source.Commands.Length == 4
        && source.Commands[0] == new HistoryCommand("register", "job", 1)
        && source.Commands[1] == new HistoryCommand("advance", "job", 1)
        && source.Commands[2].Kind == "freeze" && source.Commands[2].Value == 0
        && source.Commands[2].Id is "after-run" or "before-checkpoint" or "after-checkpoint"
        && (fault == 0 || source.Commands[2].Id == "after-run")
        && source.Commands[3] == new HistoryCommand("restart", "job", 1);

    private static string Expected(HistoryCase source)
    {
        var committed = source.Configuration["checkpointFault"] != 1 && source.Commands[2].Id != "before-checkpoint";
        return $"handoff=1;durable={(committed ? 1 : 0)};replay={(committed ? 0 : 1)};first=1";
    }

    private static async Task<HistoryObservation> Execute(HistoryCase source)
    {
        var attempts = new ConcurrentQueue<string>();
        var store = new SystematicRegistryRestartTests.DurableStore { Fault = (int)source.Configuration["checkpointFault"] };
        SystematicRegistryRestartTests.DurableStore? image = null;
        var handoffAtCheckpoint = -1;
        var cut = source.Commands[2].Id;
        store.BeforeCheckpoint = () =>
        {
            handoffAtCheckpoint = attempts.Count;
            if (cut == "before-checkpoint") image = store.Clone();
        };
        store.AfterCheckpoint = () =>
        {
            if (cut == "after-checkpoint") image = store.Clone();
        };
        await using (var first = await SystematicRegistryRestartTests.Process.StartAsync(store, SystematicRegistryTests.Epoch, attempts, false, router: new InlineRouter()))
        {
            first.Clock.Advance(TimeSpan.FromMinutes(1));
            await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            if (cut == "after-run") image = store.Clone();
        }

        if (image is null) return new("restart-cut-not-reached", Expected(source), "no image");
        var durable = image.Record!.LastFiredAt == SystematicRegistryTests.Epoch.AddMinutes(1);
        var replayed = new ConcurrentQueue<string>();
        await using var second = await SystematicRegistryRestartTests.Process.StartAsync(image, SystematicRegistryTests.Epoch.AddMinutes(1), replayed, false);
        var actual = $"handoff={handoffAtCheckpoint};durable={(durable ? 1 : 0)};replay={replayed.Count};first={attempts.Count}";
        return new(actual == Expected(source) ? "" : "restart-checkpoint-order", Expected(source), actual);
    }

    private sealed class InlineRouter : Bifrost.Scheduling.Dispatch.IJobDispatcherRouter
    {
        public void Dispatch(Bifrost.Scheduling.Core.IJobDispatcher dispatcher, Bifrost.Scheduling.Core.JobFireContext context, CancellationToken ct)
            => dispatcher.DispatchAsync(context, ct).GetAwaiter().GetResult();
    }
}
