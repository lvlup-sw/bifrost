using Bifrost.Core;
using Bifrost.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Hosting;

internal class HostedTeardownRegressionTests
{
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task StopOverlappingDisposeAndRepeatedTeardownRemainSafe(DispatchStrategy strategy)
    {
        var handler = new BlockingHandler();
        await using var orchestrator = new WorkOrchestrator<int>(handler,
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 4, WorkerCount = 1, DispatchStrategy = strategy, FullMode = QueueFullMode.Reject,
            }), NullLogger<WorkOrchestrator<int>>.Instance);
        var service = new WorkOrchestratorHostedService<int>(orchestrator,
            NullLogger<WorkOrchestratorHostedService<int>>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(deadline.Token).ConfigureAwait(false);
        await Assert.That(orchestrator.TryEnqueue(1)).IsTrue();
        await handler.Entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var stopping = service.StopAsync(deadline.Token);
        await Assert.That(stopping.IsCompleted).IsFalse();
        var disposal = orchestrator.DisposeAsync().AsTask();
        await Task.WhenAll(stopping, disposal).WaitAsync(deadline.Token).ConfigureAwait(false);

        // A faulting host can reach Stop after another path has disposed the orchestrator.
        await Task.WhenAll(service.StopAsync(deadline.Token), orchestrator.AbortAsync(deadline.Token),
            orchestrator.DisposeAsync().AsTask()).WaitAsync(deadline.Token).ConfigureAwait(false);
        await Assert.That(orchestrator.GetShutdownToken().IsCancellationRequested).IsTrue();
        await Assert.That(orchestrator.Completion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(0);
    }

    private sealed class BlockingHandler : IWorkHandler<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HandleAsync(int work, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
    }
}
