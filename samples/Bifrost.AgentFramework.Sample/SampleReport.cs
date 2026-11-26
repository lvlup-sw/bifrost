using Bifrost.Core;

namespace Bifrost.AgentFramework.Sample;

/// <summary>Prints each session's transcript and checks the keyed dispatch guarantees.</summary>
internal static class SampleReport
{
    /// <summary>Prints the transcripts and returns whether every check passed.</summary>
    /// <param name="store">The app store after the orchestrator drained.</param>
    /// <returns><see langword="true"/> when every check passed.</returns>
    /// <remarks>
    /// The checks: each session completed its accepted turns in submission order; the reply to the
    /// session's n-th completed turn saw n user messages, so each turn loaded the state the turn
    /// before it saved; every rejection was <see cref="RejectionReason.KeyLimitExceeded"/>; no
    /// accepted turn was left unfinished; and more than one session ran at the same time.
    /// </remarks>
    public static bool Print(SessionStore store)
    {
        var failures = new List<string>();
        foreach (var (session, completed, other) in store.Snapshot())
        {
            Console.WriteLine();
            Console.WriteLine($"session {session}: {completed.Count} completed, {other.Count} not run");
            for (var i = 0; i < completed.Count; i++)
            {
                var turn = completed[i];
                Console.WriteLine($"  #{turn.Sequence,-2} {turn.Message} -> {turn.Reply}");
                if (i > 0 && completed[i - 1].Sequence > turn.Sequence)
                {
                    failures.Add($"{session}: turn {turn.Sequence} finished after turn {completed[i - 1].Sequence}.");
                }

                if (turn.Reply is null || !turn.Reply.StartsWith($"[{i + 1}]", StringComparison.Ordinal))
                {
                    failures.Add($"{session}: turn {turn.Sequence} did not see the {i} earlier turn(s) of its session.");
                }
            }

            foreach (var turn in other)
            {
                Console.WriteLine($"  #{turn.Sequence,-2} {turn.Message} -> {turn.Status} {turn.Reason}");
                if (turn.Status != TurnStatus.Rejected || turn.Reason != RejectionReason.KeyLimitExceeded)
                {
                    failures.Add($"{session}: turn {turn.Sequence} ended as {turn.Status} {turn.Reason}.");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"peak concurrent sessions: {store.PeakConcurrentSessions}");
        if (store.PeakConcurrentSessions < 2)
        {
            failures.Add("Sessions did not run in parallel.");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"FAIL: {failure}");
        }

        Console.WriteLine(failures.Count == 0 ? "PASS" : "FAIL");
        return failures.Count == 0;
    }
}
