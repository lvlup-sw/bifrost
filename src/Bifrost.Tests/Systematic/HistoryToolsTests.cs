#pragma warning disable CS1591, SA1600

namespace Bifrost.Tests.Systematic;

public sealed class HistoryToolsTests
{
    [Test]
    public async Task SavedSyntheticCounterexample_ReplaysExecutionAndOracle()
    {
        var record = ReplayRecord.FromJson(File.ReadAllText(FixturePath()));
        var actual = record.Replay(IsValid, (source, _) => Compare(source));
        await Assert.That(actual).IsEqualTo(record.Observation);
        await Assert.That(actual.FailureClass).IsEqualTo("synthetic-fifo-order");
    }

    [Test]
    public async Task Replay_RoundTripsAllMetadataAndFollowup()
    {
        var record = ReplayRecord.FromJson(File.ReadAllText(FixturePath())) with
        {
            Followup = Generate(4),
            LogicalClockTicks = long.MaxValue,
        };
        var copy = ReplayRecord.FromJson(record.ToJson());
        await Assert.That(copy.ToJson()).IsEqualTo(record.ToJson());
    }

    [Test]
    public async Task Replay_RejectsInvalidHistoryBeforeExecution()
    {
        var record = ReplayRecord.FromJson(File.ReadAllText(FixturePath()));
        var executed = false;
        await Assert.That(() => record.Replay(_ => false, (_, _) =>
        {
            executed = true;
            return new("unexpected", "", "");
        })).Throws<InvalidDataException>();
        await Assert.That(executed).IsFalse();
    }

    [Test]
    public async Task Replay_RequiresActorOrderOrExplicitScheduleLimitation()
    {
        var record = ReplayRecord.FromJson(File.ReadAllText(FixturePath())) with { ActorOrder = [] };
        await Assert.That(() => ReplayRecord.FromJson(record.ToJson())).Throws<InvalidDataException>();
        record = record with { UncontrolledScheduleLimitation = "Thread interleaving is uncontrolled; only commands replay." };
        await Assert.That(ReplayRecord.FromJson(record.ToJson()).UncontrolledScheduleLimitation)
            .IsEqualTo(record.UncontrolledScheduleLimitation);
    }

    [Test]
    public async Task Reduction_RejectsInvalidCandidatesBeforeExecution()
    {
        var invalidExecutions = 0;
        var reduced = HistoryReducer.Reduce(Generate(5), IsValid, history =>
        {
            if (!IsValid(history))
            {
                invalidExecutions++;
                throw new InvalidOperationException("Invalid history reached execution.");
            }

            return Compare(history);
        });
        await Assert.That(invalidExecutions).IsEqualTo(0);
        await Assert.That(IsValid(reduced.History)).IsTrue();
        await Assert.That(reduced.History.Commands.Length).IsEqualTo(3);
        await Assert.That(reduced.IsOneMinimalWithinCandidates).IsTrue();
    }

    [Test]
    public async Task Reduction_PreservesFailureClassInsteadOfAnyFailure()
    {
        var history = Generate(2);
        HistoryObservation Evaluate(HistoryCase candidate) => candidate.Commands.Length < 3
            ? new("other-failure", "x", "y")
            : new("target-failure", "x", "z");
        var reduced = HistoryReducer.Reduce(history, _ => true, Evaluate);
        await Assert.That(reduced.History.Commands.Length).IsEqualTo(3);
        await Assert.That(reduced.Observation.FailureClass).IsEqualTo("target-failure");
    }

    [Test]
    public async Task Reduction_IsDeterministicAndOneMinimalOnlyWithinCandidateSet()
    {
        var first = HistoryReducer.Reduce(Generate(6), IsValid, Compare);
        var second = HistoryReducer.Reduce(Generate(6), IsValid, Compare);
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(first)).IsEqualTo(System.Text.Json.JsonSerializer.Serialize(second));
        foreach (var index in Enumerable.Range(0, first.History.Commands.Length))
        {
            var candidate = first.History with
            {
                Commands = first.History.Commands.Where((_, position) => position != index).ToArray(),
            };
            await Assert.That(IsValid(candidate) && Compare(candidate).FailureClass == first.Observation.FailureClass).IsFalse();
        }

        var bounded = HistoryReducer.Reduce(Generate(6), IsValid, Compare, maxCandidates: 1);
        await Assert.That(bounded.IsOneMinimalWithinCandidates).IsFalse();
    }

    [Test]
    public async Task Reduction_SimplifiesValuesAndConfigurationAndHandlesLongMinimum()
    {
        var history = new HistoryCase(new() { ["capacity"] = 16 }, [new("add", "synthetic-0", long.MinValue)]);
        var reduced = HistoryReducer.Reduce(history, value => value.Configuration["capacity"] >= 1 && value.Commands.Length == 1,
            _ => new("synthetic-failure", "1", "0"));
        await Assert.That(reduced.History.Configuration["capacity"]).IsEqualTo(1L);
        await Assert.That(reduced.History.Commands[0].Value).IsEqualTo(0L);
    }

    private static HistoryCase Generate(int count)
    {
        var commands = Enumerable.Range(0, count)
            .Select(index => new HistoryCommand("enqueue", $"synthetic-{index}", index + 1L)).ToList();
        commands.Add(new("dequeue", "synthetic-result"));
        return new(new() { ["capacity"] = count }, commands.ToArray());
    }

    private static bool IsValid(HistoryCase history)
    {
        if (!history.Configuration.TryGetValue("capacity", out var capacity) || capacity < 1)
        {
            return false;
        }

        var pending = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in history.Commands)
        {
            if (!command.Id.StartsWith("synthetic-", StringComparison.Ordinal))
            {
                return false;
            }

            if (command.Kind == "enqueue")
            {
                if (!ids.Add(command.Id) || ++pending > capacity)
                {
                    return false;
                }
            }
            else if (command.Kind != "dequeue" || --pending < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Model(HistoryCase history)
    {
        var pending = new List<string>();
        var result = new List<string>();
        foreach (var command in history.Commands)
        {
            if (command.Kind == "enqueue")
            {
                pending.Add(command.Id);
            }
            else
            {
                result.Add(pending[0]);
                pending.RemoveAt(0);
            }
        }

        return result.ToArray();
    }

    private static string[] ExecuteSyntheticLifoMutant(HistoryCase history)
    {
        var pending = new Stack<string>();
        var result = new List<string>();
        foreach (var command in history.Commands)
        {
            if (command.Kind == "enqueue")
            {
                pending.Push(command.Id);
            }
            else
            {
                result.Add(pending.Pop());
            }
        }

        return result.ToArray();
    }

    private static HistoryObservation Compare(HistoryCase history)
    {
        var expected = string.Join(",", Model(history));
        var actual = string.Join(",", ExecuteSyntheticLifoMutant(history));
        return new(expected == actual ? string.Empty : "synthetic-fifo-order", expected, actual);
    }

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Systematic", "synthetic-fifo-counterexample.json");
}
