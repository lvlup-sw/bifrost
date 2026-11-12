#pragma warning disable CS1591, SA1600, SA1633

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;
using Bifrost.Tests.Systematic;

namespace Bifrost.Tests.Scheduling;

public sealed class SchedulerReplayTests
{
    private static bool Valid(HistoryCase history) => history.Commands is [{ Kind: "coalesce", Value: >= 1 and <= 10000 }]
        && history.Configuration.TryGetValue("intervalTicks", out var ticks) && ticks is >= 1 and <= 10000000
        && history.Configuration.TryGetValue("lastTicks", out var last) && last is >= 0 and <= 10000000;

    private static HistoryObservation Execute(HistoryCase history, HistoryCase? followup = null)
    {
        var ticks = history.Configuration["intervalTicks"];
        var last = history.Configuration["lastTicks"];
        var now = last + history.Commands[0].Value * ticks;
        var actual = MissedFirePolicyApplier.ComputeMissedFires(new IntervalCadence(TimeSpan.FromTicks(ticks)), new(last, TimeSpan.Zero), new(now, TimeSpan.Zero), MissedFirePolicy.Coalesce);
        var text = string.Join(",", actual.Select(x => x.Ticks));
        return new(text == now.ToString() ? "" : "S2.latest-missed", now.ToString(), text);
    }

    [Test]
    public async Task SavedProductionCounterexample()
    {
        var path = Environment.GetEnvironmentVariable("BIFROST_REPLAY_FILE") ?? Fixture();
        var record = ReplayRecord.FromJson(File.ReadAllText(path));
        await Assert.That(record.Source.Commands[0].Value).IsEqualTo(101);
        await Assert.That(record.Observation.FailureClass).IsEqualTo("S2.latest-missed");
        var result = record.Replay(Valid, Execute);
        if (Environment.GetEnvironmentVariable("BIFROST_REPLAY_EXPECT_FAILURE") == "1")
        {
            await Assert.That(result).IsEqualTo(record.Observation);
            return;
        }

        await Assert.That(result.FailureClass).IsEqualTo("");
        await Assert.That(result.Actual).IsEqualTo(record.Observation.Expected);
    }

    [Test]
    public async Task ReduceProductionBaselineWhenRequested()
    {
        var initial = new HistoryCase(new() { ["intervalTicks"] = 17, ["lastTicks"] = 7919 }, [new("coalesce", "interval", 500)]);
        if (Environment.GetEnvironmentVariable("BIFROST_REDUCE_BASELINE") != "1")
        {
            await Assert.That(Execute(initial).FailureClass).IsEqualTo(string.Empty);
            await Assert.That(() => HistoryReducer.Reduce(initial, Valid, x => Execute(x))).Throws<ArgumentException>();
            return;
        }

        var reduction = HistoryReducer.Reduce(initial, Valid, x => Execute(x),
            history => Enumerable.Range(1, checked((int)history.Commands[0].Value - 1)).Select(count => history with { Commands = [history.Commands[0] with { Value = count }] }));
        await Assert.That(reduction.History.Commands[0].Value).IsEqualTo(101);
        await Assert.That(reduction.History.Configuration["intervalTicks"]).IsEqualTo(1);
        await Assert.That(reduction.History.Configuration["lastTicks"]).IsEqualTo(0);
        await Assert.That(reduction.IsOneMinimalWithinCandidates).IsTrue();
        var record = ReplayCapture.Create("S2", "latest missed = last + floor((now-last)/interval)*interval", reduction.History, null, reduction.Observation,
            "dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SchedulerReplayTests/SavedProductionCounterexample'", ["scheduler"], clock: 101);
        File.WriteAllText(Environment.GetEnvironmentVariable("BIFROST_REPLAY_FILE") ?? Fixture(), record.ToJson());
        Console.WriteLine($"Reduced production baseline: evaluations={reduction.Evaluations}, count=101, expected={reduction.Observation.Expected}, actual={reduction.Observation.Actual}");
    }

    private static string Fixture() => Path.Combine(AppContext.BaseDirectory, "scheduler-coalesce-replay.json");
}
