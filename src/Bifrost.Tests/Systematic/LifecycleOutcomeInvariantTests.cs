#pragma warning disable CS1591, SA1600

using System.Collections.Concurrent;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Systematic;

public sealed class LifecycleOutcomeInvariantTests
{
    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("cancellation")]
    public async Task O2_ProductionOutcomesAndWorkerExitRemainDistinct(string outcome)
    {
        var handler = new GatedHandler(outcome);
        var outcomes = new ConcurrentQueue<WorkCompletedEvent<string>>();
        var states = new ConcurrentQueue<bool>();
        var tracked = EventStreamOrchestrator<string>.CreateCompletionTrackingHandler(handler,
            evt => outcomes.Enqueue((WorkCompletedEvent<string>)evt));
        await using var actual = new WorkOrchestrator<string>(tracked,
            Options.Create(new WorkOrchestratorOptions { Capacity = 2, WorkerCount = 0 }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        var worker = actual.CreateWorkerFunction(states.Enqueue)("observed-worker", CancellationToken.None);
        try
        {
            await Assert.That(actual.Enqueue("blocked").IsAccepted).IsTrue();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(actual.Enqueue("pending").IsAccepted).IsTrue();
            await Assert.That(actual.PendingCount).IsEqualTo(1);
            await Assert.That(actual.ActiveWorkers).IsEqualTo(1);
            await Assert.That(outcomes.Count).IsEqualTo(0);
            await Assert.That(states.ToArray()).IsEquivalentTo(new[] { true });
            actual.Complete();
            await Assert.That(actual.Completion.IsCompleted).IsFalse();
            await Assert.That(actual.Enqueue("rejected").IsAccepted).IsFalse();
            if (outcome == "cancellation")
            {
                await actual.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else
            {
                handler.Release.TrySetResult();
                await actual.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(actual.Completion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(actual.ActiveWorkers).IsEqualTo(0);
            await Assert.That(actual.PendingCount).IsEqualTo(outcome == "cancellation" ? 1 : 0);
            var events = outcomes.ToArray();
            await Assert.That(events.Length).IsEqualTo(outcome == "cancellation" ? 1 : 2);
            await Assert.That(events.Single(e => e.Work == "blocked").Success).IsEqualTo(outcome == "success");
            await Assert.That(events.Count(e => e.Work == "pending" && e.Success)).IsEqualTo(outcome == "cancellation" ? 0 : 1);
            await Assert.That(events.Any(e => e.Work == "rejected")).IsFalse();
            var expectedStates = outcome == "cancellation" ? new[] { true, false } : new[] { true, false, true, false };
            await Assert.That(states.ToArray().SequenceEqual(expectedStates)).IsTrue();
        }
        finally
        {
            handler.Release.TrySetResult();
        }
    }

    private sealed class GatedHandler(string outcome) : IWorkHandler<string>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            if (work != "blocked") return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            if (outcome == "failure") throw new InvalidOperationException("synthetic handler failure");
        }
    }
}
