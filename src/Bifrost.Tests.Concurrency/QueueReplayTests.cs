#pragma warning disable CS1591, SA1600, SA1633

using Bifrost.Concurrency;
using Bifrost.Tests.Systematic;

namespace Bifrost.Tests.Concurrency;

public sealed class QueueReplayTests
{
    private sealed class InjectedFault : Exception;
    private sealed class FaultComparer : IComparer<int>
    {
        internal int Position = int.MaxValue;
        internal int Calls;
        public int Compare(int x, int y)
        {
            if (++this.Calls == this.Position) throw new InjectedFault();
            return x.CompareTo(y);
        }
    }

    private static bool Valid(HistoryCase history) => history.Configuration.TryGetValue("capacity", out var capacity) && capacity is >= 1 and <= 1000
        && history.Configuration.TryGetValue("stripes", out var stripes) && stripes is >= 1 and <= 16
        && history.Configuration.TryGetValue("buffer", out var buffer) && buffer is >= 0 and <= 32
        && history.Commands.All(x => x.Kind is "offer" or "take" or "clear" or "fault" && x.Value is >= int.MinValue and <= int.MaxValue)
        && history.Commands.Where(x => x.Kind == "offer").Select(x => x.Id).Distinct().Count() == history.Commands.Count(x => x.Kind == "offer")
        && history.Commands.Where(x => x.Kind == "fault").All(x => x.Value > 0);

    private static HistoryObservation Execute(HistoryCase source, HistoryCase? followup = null)
    {
        var first = Run(source);
        if (first.FailureClass.Length > 0 || followup is null) return first;
        var second = Run(followup);
        if (second.FailureClass.Length > 0) return second;
        return new(first.Actual == second.Actual ? "" : "QM.translation", first.Actual, second.Actual);
    }

    private static HistoryObservation Run(HistoryCase history)
    {
        var fault = new FaultComparer();
        var queue = ConcurrentPriorityQueue<string, int>.Create(new() { Capacity = (int)history.Configuration["capacity"], SubQueueCount = (int)history.Configuration["stripes"], BufferCapacity = (int)history.Configuration["buffer"], Comparer = fault });
        var live = new Dictionary<string, int>();
        var shift = history.Configuration.GetValueOrDefault("translation");
        var drained = new List<string>();
        for (var index = 0; index < history.Commands.Length; index++)
        {
            var command = history.Commands[index];
            if (command.Kind == "fault")
            {
                fault.Calls = 0;
                fault.Position = (int)command.Value;
                continue;
            }

            var armed = fault.Position != int.MaxValue;
            try
            {
                if (command.Kind == "offer")
                {
                    var accepted = queue.TryEnqueue(command.Id, (int)command.Value);
                    if (accepted != (live.Count < history.Configuration["capacity"])) return new("Q2.admission", $"accepted={live.Count < history.Configuration["capacity"]}", $"accepted={accepted}; command={index}");
                    if (accepted) live.Add(command.Id, (int)command.Value);
                }
                else if (command.Kind == "take")
                {
                    var minimum = live.Count == 0 ? 0 : live.Values.Min();
                    if (queue.TryDequeueBestObserved(out var id, out var priority))
                    {
                        if (!live.Remove(id, out var expected) || expected != priority) return new("Q1.removal", "live identity and priority", $"{id}:{priority}; command={index}");
                        if (priority != minimum) return new("Q3.minimum", minimum.ToString(), priority.ToString());
                        drained.Add($"{id}:{priority - shift}");
                    }
                    else if (live.Count > 0)
                    {
                        return new("Q1.empty", "nonempty", $"empty; command={index}");
                    }
                }
                else
                {
                    queue.Clear();
                    live.Clear();
                }
            }
            catch (InjectedFault)
            {
                if (!armed) throw;
            }
            finally
            {
                fault.Position = int.MaxValue;
            }

            var snapshot = queue.ToArray();
            var actual = string.Join(",", snapshot.OrderBy(x => x.Element).Select(x => $"{x.Element}:{x.Priority}"));
            var expectedSnapshot = string.Join(",", live.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
            if (queue.Count != live.Count || snapshot.Length != live.Count || actual != expectedSnapshot)
                return new(armed ? "Q4.snapshot" : "Q1.snapshot", $"{live.Count}|{expectedSnapshot}", $"{queue.Count}|{actual}; command={index}");
        }

        return new("", "model agrees", string.Join(",", drained.Order()));
    }

    private static void Verify(HistoryCase source, HistoryCase? followup = null, string invariant = "Q1", int? fault = null)
    {
        var observation = Execute(source, followup);
        if (observation.FailureClass.Length == 0) return;
        var record = ReplayCapture.Create(invariant, followup is null ? "successful effects match live multiset" : "priority translation preserves normalized drain", source, followup, observation,
            "BIFROST_REPLAY_FILE=<path> dotnet run --project src/Bifrost.Tests.Concurrency -- --treenode-filter '/*/*/QueueReplayTests/ReplaySavedHistory'",
            source.Commands.Select(x => x.Id.Split(':')[0]).ToArray(), "Only command-level actor order is controlled; native internal striping and overlapping thread schedules are not replayed.", fault: fault);
        ReplayCapture.SaveFailure(record);
        if (followup is null && fault is null && invariant == "Q1")
        {
            var reduced = HistoryReducer.Reduce(source, Valid, history => Execute(history));
            ReplayCapture.SaveFailure(record with { Invariant = invariant + "-reduced", Source = reduced.History, Observation = reduced.Observation, ActorOrder = reduced.History.Commands.Select(x => x.Id.Split(':')[0]).ToArray() });
            Console.WriteLine($"Reduced queue history: commands={reduced.History.Commands.Length}, evaluations={reduced.Evaluations}, oneMinimal={reduced.IsOneMinimalWithinCandidates}");
        }

        throw new InvalidOperationException($"{observation.FailureClass}: expected {observation.Expected}; actual {observation.Actual}");
    }

    [Test]
    public void GeneratedSemanticHistories()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var random = new Random(seed);
            var commands = Enumerable.Range(0, 300).Select(step => new HistoryCommand(random.Next(10) switch { < 6 => "offer", < 9 => "take", _ => "clear" }, $"main:{step}", random.Next(-3, 4))).ToArray();
            foreach (var buffer in new[] { 0, 1, 16 }) Verify(new(new() { ["capacity"] = 33, ["stripes"] = 4, ["buffer"] = buffer }, commands));
        }
    }

    [Test]
    public void ControlledActorAndFaultHistories()
    {
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders)
        {
            var commands = Enumerable.Range(0, 12).SelectMany(round => order.Select(actor => new HistoryCommand((round + actor) % 3 == 0 ? "take" : "offer", $"actor{actor}:{round}", round % 3))).ToArray();
            foreach (var buffer in new[] { 0, 1, 16 }) Verify(new(new() { ["capacity"] = 3, ["stripes"] = 2, ["buffer"] = buffer }, commands), invariant: "Q5");
        }

        foreach (var buffer in new[] { 0, 1, 16 })
        {
            for (var position = 1; position <= 32; position++)
            {
                var commands = Enumerable.Range(0, 33).Select(i => new HistoryCommand("offer", $"main:{i}", 33 - i)).Concat([new("fault", "main:fault", position), new("offer", "main:after", -1)]).Concat(Enumerable.Range(0, 34).Select(i => new HistoryCommand("take", $"main:drain{i}"))).ToArray();
                Verify(new(new() { ["capacity"] = 96, ["stripes"] = 1, ["buffer"] = buffer }, commands), invariant: "Q4", fault: 33);
            }
        }
    }

    [Test]
    public void TranslationPersistsBothSemanticHistories()
    {
        for (var seed = 0; seed < 16; seed++)
        {
            var random = new Random(seed);
            var commands = Enumerable.Range(0, 80).Select(i => new HistoryCommand("offer", $"main:{i}", random.Next(-1000, 1001) * 100 + i)).Concat(Enumerable.Range(0, 80).Select(i => new HistoryCommand("take", $"main:drain{i}"))).ToArray();
            var source = new HistoryCase(new() { ["capacity"] = 96, ["stripes"] = 4, ["buffer"] = 16 }, commands);
            var followup = source with { Configuration = new(source.Configuration) { ["translation"] = 1234 }, Commands = commands.Select(x => x.Kind == "offer" ? x with { Value = x.Value + 1234 } : x).ToArray() };
            Verify(source, followup, "QM");
        }
    }

    private static string Fixture() => Path.Combine(AppContext.BaseDirectory, "queue-clear-replay.json");

    [Test]
    public void ActorFaultAndPairedRecordsRoundTrip()
    {
        var configuration = new SortedDictionary<string, long> { ["capacity"] = 4, ["stripes"] = 1, ["buffer"] = 0 };
        var actor = new HistoryCase(configuration, [new("offer", "actor0:0", 3), new("offer", "actor1:0", 2), new("take", "actor2:0")]);
        var fault = actor with { Commands = [new("offer", "actor0:0", 3), new("offer", "actor1:0", 2), new("fault", "actor2:arm", 1), new("offer", "actor2:0", 1), new("take", "actor0:1")] };
        var translated = actor with { Configuration = new(configuration) { ["translation"] = 1234 }, Commands = actor.Commands.Select(x => x.Kind == "offer" ? x with { Value = x.Value + 1234 } : x).ToArray() };
        foreach (var (source, followup, invariant, position) in new (HistoryCase, HistoryCase?, string, int?)[] { (actor, null, "Q5", null), (fault, null, "Q4", 2), (actor, translated, "QM", null) })
        {
            var observed = Execute(source, followup);
            if (observed.FailureClass.Length != 0) throw new InvalidOperationException(observed.ToString());
            var record = ReplayCapture.Create(invariant, "live model and normalized translation", source, followup, observed,
                "BIFROST_REPLAY_FILE=<path> dotnet run --project src/Bifrost.Tests.Concurrency -- --treenode-filter '/*/*/QueueReplayTests/ReplaySavedHistory'",
                source.Commands.Select(x => x.Id.Split(':')[0]).ToArray(), "Only serialized command-level actor order; no overlapping native schedule replay.", fault: position);
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, record.ToJson());
                var restored = ReplayRecord.FromJson(File.ReadAllText(path));
                if (restored.Replay(Valid, Execute) != observed) throw new InvalidOperationException("Semantic record changed on replay.");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public void MalformedNestedHistoryIsRejected()
    {
        var json = File.ReadAllText(Fixture());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Source"]!["Commands"] = null;
        try
        {
            ReplayRecord.FromJson(node.ToJsonString());
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Malformed commands accepted.");
    }

    [Test]
    public void ReplaySavedHistory()
    {
        var path = Environment.GetEnvironmentVariable("BIFROST_REPLAY_FILE") ?? Fixture();
        var record = ReplayRecord.FromJson(File.ReadAllText(path));
        var observation = record.Replay(Valid, Execute);
        if (Environment.GetEnvironmentVariable("BIFROST_REPLAY_EXPECT_FAILURE") == "1")
        {
            if (observation != record.Observation) throw new InvalidOperationException("Saved failure did not reproduce exactly.");
            return;
        }

        if (observation.FailureClass.Length > 0) throw new InvalidOperationException($"{observation.FailureClass}: expected {observation.Expected}; actual {observation.Actual}");
    }
}
