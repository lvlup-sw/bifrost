using System.Text.Json;

namespace Bifrost.Tests.Systematic;

internal sealed record HistoryCommand(string Kind, string Id, long Value = 0);

internal sealed record HistoryCase(SortedDictionary<string, long> Configuration, HistoryCommand[] Commands, SortedDictionary<string, string>? Parameters = null);

internal sealed record HistoryObservation(string FailureClass, string Expected, string Actual);

internal sealed record ReplayRecord(
    int SchemaVersion,
    string SourceSha,
    bool SourceDirty,
    string Runtime,
    string Build,
    string Invariant,
    string OracleRelation,
    HistoryCase Source,
    HistoryCase? Followup,
    long LogicalClockTicks,
    int? FaultPosition,
    string[] ActorOrder,
    string? UncontrolledScheduleLimitation,
    HistoryObservation Observation,
    string ReplayCommand)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ReplayRecord FromJson(string json)
    {
        var record = JsonSerializer.Deserialize<ReplayRecord>(json, Options)
            ?? throw new InvalidDataException("Missing replay record.");
        if (record.SchemaVersion != 1 || string.IsNullOrWhiteSpace(record.SourceSha)
            || string.IsNullOrWhiteSpace(record.Invariant) || record.Source is null
            || record.Observation is null || string.IsNullOrWhiteSpace(record.Runtime)
            || string.IsNullOrWhiteSpace(record.Build) || string.IsNullOrWhiteSpace(record.ReplayCommand)
            || record.ActorOrder is null
            || (record.ActorOrder.Length == 0 && string.IsNullOrWhiteSpace(record.UncontrolledScheduleLimitation)))
        {
            throw new InvalidDataException("Incomplete or unsupported replay record.");
        }

        return record;
    }

    public HistoryObservation Replay(Func<HistoryCase, bool> isValid, Func<HistoryCase, HistoryCase?, HistoryObservation> execute)
    {
        if (!isValid(Source) || (Followup is not null && !isValid(Followup)))
        {
            throw new InvalidDataException("Replay history is outside the valid domain.");
        }

        return execute(Source, Followup);
    }
}

internal sealed record ReductionResult(HistoryCase History, HistoryObservation Observation, int Evaluations, bool IsOneMinimalWithinCandidates);

internal static class HistoryReducer
{
    public static ReductionResult Reduce(
        HistoryCase initial,
        Func<HistoryCase, bool> isValid,
        Func<HistoryCase, HistoryObservation> evaluate,
        Func<HistoryCase, IEnumerable<HistoryCase>>? simplifyValues = null,
        Func<HistoryCase, IEnumerable<HistoryCase>>? simplifyConfiguration = null,
        int maxCandidates = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);
        if (!isValid(initial))
        {
            throw new ArgumentException("Initial history is outside the valid domain.", nameof(initial));
        }

        var current = initial;
        var observation = evaluate(current);
        if (string.IsNullOrEmpty(observation.FailureClass))
        {
            throw new ArgumentException("Initial history must fail an invariant.", nameof(initial));
        }

        var failureClass = observation.FailureClass;
        var evaluations = 1;
        var candidates = 0;
        while (true)
        {
            var changed = false;
            foreach (var candidate in Candidates(current, simplifyValues, simplifyConfiguration))
            {
                if (++candidates > maxCandidates)
                {
                    return new(current, observation, evaluations, false);
                }

                if (!IsSmaller(candidate, current) || !isValid(candidate))
                {
                    continue;
                }

                var result = evaluate(candidate);
                evaluations++;
                if (!string.Equals(result.FailureClass, failureClass, StringComparison.Ordinal))
                {
                    continue;
                }

                current = candidate;
                observation = result;
                changed = true;
                break;
            }

            if (!changed)
            {
                return new(current, observation, evaluations, true);
            }
        }
    }

    private static IEnumerable<HistoryCase> Candidates(
        HistoryCase history,
        Func<HistoryCase, IEnumerable<HistoryCase>>? values,
        Func<HistoryCase, IEnumerable<HistoryCase>>? configuration)
    {
        for (var index = 0; index < history.Commands.Length; index++)
        {
            yield return history with { Commands = history.Commands.Where((_, position) => position != index).ToArray() };
        }

        foreach (var candidate in values?.Invoke(history) ?? ReduceValues(history))
        {
            yield return candidate;
        }

        foreach (var candidate in configuration?.Invoke(history) ?? ReduceConfiguration(history))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<HistoryCase> ReduceValues(HistoryCase history)
    {
        for (var index = 0; index < history.Commands.Length; index++)
        {
            foreach (var value in SmallerIntegers(history.Commands[index].Value))
            {
                var commands = history.Commands.ToArray();
                commands[index] = commands[index] with { Value = value };
                yield return history with { Commands = commands };
            }
        }
    }

    private static IEnumerable<HistoryCase> ReduceConfiguration(HistoryCase history)
    {
        foreach (var pair in history.Configuration)
        {
            foreach (var value in SmallerIntegers(pair.Value))
            {
                var configuration = new SortedDictionary<string, long>(history.Configuration, StringComparer.Ordinal)
                {
                    [pair.Key] = value,
                };
                yield return history with { Configuration = configuration };
            }
        }
    }

    private static IEnumerable<long> SmallerIntegers(long value)
    {
        if (value != 0)
        {
            yield return 0;
            if (Magnitude(value) > 1)
            {
                yield return Math.Sign(value);
                yield return value / 2;
            }
        }
    }

    private static decimal Magnitude(long value) => Math.Abs((decimal)value);

    private static bool IsSmaller(HistoryCase candidate, HistoryCase current)
    {
        if (candidate.Commands.Length != current.Commands.Length)
        {
            return candidate.Commands.Length < current.Commands.Length;
        }

        var candidateSize = candidate.Commands.Sum(command => Magnitude(command.Value))
            + candidate.Configuration.Sum(pair => Magnitude(pair.Value));
        var currentSize = current.Commands.Sum(command => Magnitude(command.Value))
            + current.Configuration.Sum(pair => Magnitude(pair.Value));
        return candidateSize < currentSize;
    }
}
