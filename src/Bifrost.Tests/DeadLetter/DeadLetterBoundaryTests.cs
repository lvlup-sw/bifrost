// =============================================================================
// <copyright file="DeadLetterBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Decorators;
using Bifrost.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;

namespace Bifrost.Tests.DeadLetter;

/// <summary>Exercises cancellation, correlation, and policy input boundaries.</summary>
public class DeadLetterBoundaryTests
{
    /// <summary>Either of the two supplied tokens can stop a drain without removing an item.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EitherDrainTokenCancels(bool cancelEnumerationToken)
    {
        var queue = new DeadLetterQueue<int>(Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<int>>.Instance);
        await queue.EnqueueAsync(new(1, null, 1, DateTimeOffset.UtcNow, null)).ConfigureAwait(false);
        using var read = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        await using var reader = queue.ReadAllAsync(read.Token).GetAsyncEnumerator(enumeration.Token);
        await (cancelEnumerationToken ? enumeration : read).CancelAsync().ConfigureAwait(false);
        await Assert.That(async () => await reader.MoveNextAsync().ConfigureAwait(false)).Throws<OperationCanceledException>();
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    /// <summary>Rejection retains the correlation supplied by the producer in the DLQ and notification.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectionPreservesCorrelation(bool asynchronous)
    {
        var inner = Substitute.For<IWorkOrchestrator<int>>();
        inner.Enqueue(Arg.Any<int>(), Arg.Any<WorkEnqueueOptions>()).Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
        inner.EnqueueAsync(Arg.Any<int>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
        var queue = new DeadLetterQueue<int>(Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<int>>.Instance);
        var notifier = new DeadLetterNotifier<int>(NullLogger<DeadLetterNotifier<int>>.Instance);
        var notified = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = notifier.Subscribe(evt => notified.TrySetResult(evt.CorrelationId));
        await using var routing = new RejectionRoutingOrchestrator<int>(inner, queue, notifier, NullLogger<RejectionRoutingOrchestrator<int>>.Instance);
        var options = new WorkEnqueueOptions(CorrelationId: "request-42");
        var result = asynchronous ? await routing.EnqueueAsync(1, options).ConfigureAwait(false) : routing.Enqueue(1, options);
        await Assert.That(result.IsAccepted).IsFalse();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().ConfigureAwait(false)).IsTrue();
        await Assert.That(reader.Current.CorrelationId).IsEqualTo("request-42");
        await Assert.That(await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).IsEqualTo("request-42");
    }

    /// <summary>A negative retry count cannot silently disable HTTP resilience.</summary>
    [Test]
    public async Task NegativeHttpRetriesAreRejected()
    {
        await Assert.That(() => BifrostHttpResilienceExtensions.ConfigurePipeline(
            new ResiliencePipelineBuilder<HttpResponseMessage>(), new BifrostHttpResiliencePolicy { MaxRetries = -1 }))
            .Throws<ArgumentOutOfRangeException>();
    }
}
