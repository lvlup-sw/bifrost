// =============================================================================
// <copyright file="TerminalOutcomeBuilderOrderTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;
using Bifrost.OpenTelemetry;
using Bifrost.Outcomes;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.Outcomes;

/// <summary>
/// Verifies through the public builder that handler outcomes and dead-letter capture do not depend
/// on the order of the builder calls, and that a saturated key produces only an admission rejection.
/// </summary>
/// <remarks>
/// Each run uses one worker and three items: one that succeeds, one that always fails, and one that
/// fails once and then succeeds. The run is summarized as one fingerprint string: the counters, the
/// dead-letter entries, the published completion and dead-letter events, and how often a custom
/// handler decorator saw each item. Every permutation of the builder calls must give the same
/// fingerprint.
/// </remarks>
[Property("Category", "Integration")]
public sealed class TerminalOutcomeBuilderOrderTests
{
    private const string Expected =
        "succeeded=2 failed=1 canceled=0 abandoned=0 attempts=6 captured=1 captureErrors=0"
        + " | dlq=bad:3 | completed=bad:False,flaky:True,ok:True | deadLettered=bad:3"
        + " | decorator=bad:3,flaky:2,ok:1";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly (string Name, Action<WorkOrchestratorBuilder<string>, Recorder> Apply)[] Steps =
    [
        ("WithDeadLetterQueue", static (builder, _) => builder.WithDeadLetterQueue(static o => o.MaxRetries = 2)),
        ("WithEventStream", static (builder, _) => builder.WithEventStream()),
        ("WithHandlerDecorator", static (builder, recorder) => builder.WithHandlerDecorator<string>((_, inner) => new CountingDecorator(inner, recorder))),
        ("WithOpenTelemetry", static (builder, _) => builder.WithOpenTelemetry()),
    ];

    /// <summary>Every order of the builder calls gives the same outcomes, attempts, capture, and events.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EveryBuilderOrder_GivesIdenticalOutcomesAndCapture()
    {
        var fingerprints = new List<string>();
        foreach (var order in Permutations([0, 1, 2, 3]))
        {
            var label = string.Join(" > ", order.Select(static i => Steps[i].Name));
            var fingerprint = await RunAsync(order);
            fingerprints.Add($"{label} => {fingerprint}");
        }

        await Assert.That(fingerprints).HasCount(24);
        foreach (var fingerprint in fingerprints)
            await Assert.That(fingerprint[(fingerprint.IndexOf(" => ", StringComparison.Ordinal) + 4)..]).IsEqualTo(Expected);
    }

    /// <summary>Calling <c>WithDeadLetterQueue</c> twice keeps one retry layer and one capture.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithDeadLetterQueueTwice_KeepsOneRetryOwner()
    {
        var fingerprint = await RunAsync([0, 0, 1, 2]);

        await Assert.That(fingerprint).IsEqualTo(Expected);
    }

    /// <summary>
    /// A key at its pending limit produces only an admission rejection: nothing is captured and no
    /// outcome is recorded for the rejected item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SaturatedKey_ProducesAdmissionRejectionOnly()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new SemaphoreSlim(0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(new GatedHandler(started, release));
        var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1);
        builder.WithDeadLetterQueue()
            .WithKeyedDispatch(static w => w.Split(':')[0], maxPendingPerKey: 1, maxConcurrencyPerKey: 1)
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator.Enqueue("A:1").IsAccepted).IsTrue();
        await started.Task.WaitAsync(WaitTimeout);
        await Assert.That(orchestrator.Enqueue("A:2").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("A:3").Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That((await orchestrator.EnqueueAsync("A:4")).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        release.Release(2);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();

        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();
        await Assert.That(provider.GetRequiredService<IDeadLetterQueue<string>>().Count).IsEqualTo(0);
        await Assert.That(counters.Succeeded).IsEqualTo(2);
        await Assert.That(counters.Failed).IsEqualTo(0);
        await Assert.That(counters.DeadLetterCaptured).IsEqualTo(0);
        await Assert.That(counters.Abandoned).IsEqualTo(0);
    }

    private static async Task<string> RunAsync(IReadOnlyList<int> order)
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(new ScriptHandler(recorder));
        var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1);
        foreach (var step in order)
            Steps[step].Apply(builder, recorder);

        var publish = builder.EventPublishCallback;
        builder.EventPublishCallback = evt =>
        {
            recorder.Events.Enqueue(evt);
            publish?.Invoke(evt);
        };
        builder.Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        foreach (var work in new[] { "ok", "bad", "flaky" })
            await Assert.That(orchestrator.Enqueue(work).IsAccepted).IsTrue();

        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();

        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();
        var entries = new List<string>();
        await foreach (var entry in provider.GetRequiredService<IDeadLetterQueue<string>>().ReadAllAsync())
            entries.Add($"{entry.Work}:{entry.AttemptCount}");

        return $"succeeded={counters.Succeeded} failed={counters.Failed} canceled={counters.Canceled}"
            + $" abandoned={counters.Abandoned} attempts={counters.HandlerAttempts}"
            + $" captured={counters.DeadLetterCaptured} captureErrors={counters.DeadLetterCaptureErrors}"
            + $" | dlq={Join(entries)}"
            + $" | completed={Join(recorder.Events.OfType<WorkCompletedEvent<string>>().Select(static e => $"{e.Work}:{e.Success}"))}"
            + $" | deadLettered={Join(recorder.Events.OfType<WorkDeadLetteredEvent<string>>().Select(static e => $"{e.Work}:{e.AttemptCount}"))}"
            + $" | decorator={Join(recorder.DecoratorCalls.Select(static p => $"{p.Key}:{p.Value}"))}";
    }

    private static string Join(IEnumerable<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
                yield return [items[i], .. tail];
        }
    }

    private sealed class Recorder
    {
        internal ConcurrentQueue<IOrchestratorEvent> Events { get; } = new();

        internal ConcurrentDictionary<string, int> DecoratorCalls { get; } = new();

        internal ConcurrentDictionary<string, int> HandlerCalls { get; } = new();
    }

    private sealed class ScriptHandler(Recorder recorder) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            var call = recorder.HandlerCalls.AddOrUpdate(work, 1, static (_, count) => count + 1);
            return work switch
            {
                "bad" => ValueTask.FromException(new InvalidOperationException("always fails")),
                "flaky" when call == 1 => ValueTask.FromException(new InvalidOperationException("fails once")),
                _ => ValueTask.CompletedTask,
            };
        }
    }

    private sealed class GatedHandler(TaskCompletionSource started, SemaphoreSlim release) : IWorkHandler<string>
    {
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            started.TrySetResult();
            await release.WaitAsync(ct);
        }
    }

    private sealed class CountingDecorator(IWorkHandler<string> inner, Recorder recorder) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            recorder.DecoratorCalls.AddOrUpdate(work, 1, static (_, count) => count + 1);
            return inner.HandleAsync(work, ct);
        }
    }
}
