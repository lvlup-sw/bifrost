#pragma warning disable CS1591, SA1600

using System.Collections.Concurrent;
using Bifrost.Core;
using Bifrost.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Bifrost.Tests.Systematic;

public sealed class LifecycleIdentityTests
{
    [Test]
    public async Task O2_R1_RealAdmissionsHaveDistinctAttemptsExecutionsAndOutcomes()
    {
        var handler = new RecordingHandler();
        await using var actual = new WorkOrchestrator<Payload>(handler,
            Options.Create(new WorkOrchestratorOptions { Capacity = 4, WorkerCount = 0 }),
            NullLogger<WorkOrchestrator<Payload>>.Instance);
        var worker = actual.CreateWorkerFunction()("worker-alpha", CancellationToken.None);
        var inner = Substitute.For<IWorkOrchestrator<Payload>>();
        var admissions = new List<Admission>();
        inner.EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(info => InjectAfterEffect(info.Arg<Payload>(), info.Arg<CancellationToken>()));
        async ValueTask<EnqueueResult> InjectAfterEffect(Payload work, CancellationToken token)
        {
            var attemptId = $"admission-attempt-{admissions.Count}";
            var result = await actual.EnqueueAsync(work with { AcceptedAttempt = attemptId }, ct: token).ConfigureAwait(false);
            admissions.Add(new("work-shared", "call-one", attemptId, result.IsAccepted));
            if (admissions.Count < 3) throw new TimeoutException("synthetic post-effect admission failure");
            return result;
        }

        var wrapper = new ResilientOrchestrator<Payload>(inner,
            Options.Create(new ResiliencySettings { RetryCount = 2, RetryIntervalSeconds = 0, UseExponentialBackoff = false }),
            NullLogger<ResilientOrchestrator<Payload>>.Instance);
        var outcome = await wrapper.EnqueueAsync(new("work-shared", "unassigned")).ConfigureAwait(false);
        await actual.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!outcome.IsAccepted || admissions.Count != 3 || handler.Executions.Count != 3)
            throw new InvalidOperationException("One call can admit three effects; handler failures do not trigger admission retries.");
        var events = handler.Executions.ToArray();
        if (events.Select(e => e.ExecutionId).Distinct().Count() != 3
            || events.Select(e => e.WorkerId).Distinct().Single() != "worker-alpha"
            || events.Any(e => e.WorkId != "work-shared" || e.Outcome != "handler-failed")
            || !admissions.Select(a => a.AttemptId).Order().SequenceEqual(events.Select(e => e.AdmissionAttemptId).Order()))
            throw new InvalidOperationException("Admission, attempt, worker, work, execution and outcome identities must remain distinct.");
    }

    public sealed record Payload(string WorkId, string AcceptedAttempt);
    private sealed record Admission(string WorkId, string CallId, string AttemptId, bool Accepted);
    private sealed record Execution(string WorkId, string AdmissionAttemptId, string ExecutionId, string WorkerId, string Outcome);

    private sealed class RecordingHandler : IWorkHandler<Payload>
    {
        internal ConcurrentQueue<Execution> Executions { get; } = new();

        public ValueTask HandleAsync(Payload work, CancellationToken ct)
        {
            Executions.Enqueue(new(work.WorkId, work.AcceptedAttempt, $"execution-{Executions.Count}", "worker-alpha", "handler-failed"));
            return ValueTask.FromException(new InvalidOperationException("synthetic handler failure"));
        }
    }
}
