// =============================================================================
// <copyright file="HandlerResilienceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.Outcomes;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Polly.Timeout;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Verifies <c>WithHandlerResilience</c> through the public builder: transient retries, permanent
/// failures, cooperative timeouts, host cancellation, attempt counts, a fresh scope per attempt,
/// one retry owner next to the dead-letter queue, and key slots held across attempts and delays.
/// </summary>
/// <remarks>
/// Each test drains or aborts the orchestrator before it reads the terminal outcome counters, so
/// the counters are final. Bounded waits only guard against hangs. The slot tests read the keyed
/// backlog while the handler or the retry delay is gated, when the queue state cannot change.
/// </remarks>
[Property("Category", "Integration")]
public sealed class HandlerResilienceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A transient failure is retried, and the item succeeds with two counted attempts.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TransientFailure_IsRetriedThenSucceeds()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler((_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? ValueTask.FromException(new TransientException())
                    : ValueTask.CompletedTask),
            builder => builder.WithHandlerResilience(Transient(maxRetries: 2)));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Counters.Failed).IsEqualTo(0);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(2);
    }

    /// <summary>A failure the predicate does not classify as transient is not retried.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PermanentFailure_IsNotRetried()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException(new InvalidOperationException("permanent"));
            }),
            builder => builder.WithHandlerResilience(Transient(maxRetries: 3)),
            builder => builder.WithDeadLetterQueue(static o => o.MaxRetries = 0));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(1);
        var entries = await harness.DeadLettersAsync();
        await Assert.That(entries).HasCount(1);
        await Assert.That(entries[0].AttemptCount).IsEqualTo(1);
        await Assert.That(entries[0].Exception).IsTypeOf<InvalidOperationException>();
    }

    /// <summary>
    /// Exhausted retries end in one failure that the dead-letter queue captures once with every
    /// attempt counted.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ExhaustedRetries_AreCapturedOnceWithAllAttempts()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException(new TransientException());
            }),
            builder => builder.WithDeadLetterQueue(static o => o.MaxRetries = 0),
            builder => builder.WithHandlerResilience(Transient(maxRetries: 2)));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(3);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(1);
        var entries = await harness.DeadLettersAsync();
        await Assert.That(entries).HasCount(1);
        await Assert.That(entries[0].AttemptCount).IsEqualTo(3);
    }

    /// <summary>
    /// The attempt timeout cancels the handler token, and the item fails with a timeout that is not
    /// retried by default.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Timeout_CancelsAttemptAndFails()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler(async (_, ct) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }),
            builder => builder.WithDeadLetterQueue(static o => o.MaxRetries = 0),
            builder => builder.WithHandlerResilience(static o =>
            {
                o.AttemptTimeout = TimeSpan.FromMilliseconds(50);
                o.MaxRetries = 2;
                o.RetryDelay = TimeSpan.Zero;
                o.ShouldRetry = static ex => ex is TransientException;
            }));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.Canceled).IsEqualTo(0);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(1);
        var entries = await harness.DeadLettersAsync();
        await Assert.That(entries).HasCount(1);
        await Assert.That(entries[0].Exception).IsTypeOf<TimeoutRejectedException>();
    }

    /// <summary>A timeout that the predicate classifies as transient is retried.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Timeout_ClassifiedTransient_IsRetried()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler(async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }),
            builder => builder.WithHandlerResilience(static o =>
            {
                o.AttemptTimeout = TimeSpan.FromMilliseconds(50);
                o.MaxRetries = 1;
                o.RetryDelay = TimeSpan.Zero;
                o.ShouldRetry = static ex => ex is TimeoutRejectedException;
            }));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(2);
    }

    /// <summary>
    /// Host cancellation is never retried, even when the predicate would retry everything, and the
    /// predicate is not called.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task HostCancellation_IsNotRetried()
    {
        var calls = 0;
        var predicateCalls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness.Create(
            builder => builder.WithHandler(async (_, ct) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }),
            builder => builder.WithHandlerResilience(o =>
            {
                o.MaxRetries = 3;
                o.RetryDelay = TimeSpan.Zero;
                o.ShouldRetry = _ =>
                {
                    Interlocked.Increment(ref predicateCalls);
                    return true;
                };
            }));
        await using var provider = harness.Provider;
        await Assert.That(harness.Orchestrator.Enqueue("job").IsAccepted).IsTrue();
        await started.Task.WaitAsync(WaitTimeout);

        await harness.Orchestrator.AbortAsync().WaitAsync(WaitTimeout);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(predicateCalls).IsEqualTo(0);
        await Assert.That(harness.Counters.Canceled).IsEqualTo(1);
        await Assert.That(harness.Counters.Failed).IsEqualTo(0);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(1);
    }

    /// <summary>
    /// An abort during a retry delay ends the item as canceled: the transient failure that started
    /// the delay is dropped, nothing is dead-lettered, and the handler is not called again.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AbortDuringRetryDelay_SettlesCanceled_WithoutCapture()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var retryClassified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException(new TransientException());
            })
            .WithDeadLetterQueue(static o => o.MaxRetries = 0)
            .WithHandlerResilience(o =>
            {
                o.MaxRetries = 3;
                o.RetryDelay = TimeSpan.FromSeconds(30);
                o.ShouldRetry = ex =>
                {
                    retryClassified.TrySetResult();
                    return ex is TransientException;
                };
            })
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();

        await Assert.That(orchestrator.Enqueue("job").IsAccepted).IsTrue();
        await retryClassified.Task.WaitAsync(WaitTimeout);
        await orchestrator.AbortAsync().WaitAsync(WaitTimeout);

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        await Assert.That(counters.Canceled).IsEqualTo(1);
        await Assert.That(counters.Failed).IsEqualTo(0);
        await Assert.That(counters.DeadLetterCaptured).IsEqualTo(0);
        await Assert.That(counters.HandlerAttempts).IsEqualTo(1);
    }

    /// <summary>
    /// Exponential backoff doubles the retry delay and caps each delay at
    /// <see cref="HandlerResilienceOptions.MaxRetryDelay"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// With a 40-second base delay the uncapped delays would be 40, 80, and 160 seconds. The fake
    /// clock advances one second per poll, so each gap between attempt starts is the delay, give or
    /// take a poll that lands before the timer is set.
    /// </remarks>
    [Test]
    public async Task ExponentialBackoff_DoublesDelay_CappedAtMaxRetryDelay()
    {
        var time = new FakeTimeProvider();
        var starts = new ConcurrentQueue<DateTimeOffset>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler((_, _) =>
            {
                starts.Enqueue(time.GetUtcNow());
                return starts.Count <= 3 ? ValueTask.FromException(new TransientException()) : ValueTask.CompletedTask;
            })
            .WithHandlerResilience(static o =>
            {
                o.MaxRetries = 3;
                o.RetryDelay = TimeSpan.FromSeconds(40);
                o.UseExponentialBackoff = true;
                o.ShouldRetry = static ex => ex is TransientException;
            })
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator.Enqueue("job").IsAccepted).IsTrue();
        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return starts.Count == 4;
        });
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        var at = starts.ToArray();
        var gaps = Enumerable.Range(1, 3).Select(i => (at[i] - at[i - 1]).TotalSeconds).ToArray();
        await Assert.That(gaps[0]).IsBetween(40d, 42d);
        await Assert.That(gaps[1]).IsBetween(60d, 62d);
        await Assert.That(gaps[2]).IsBetween(60d, 62d);
    }

    /// <summary>
    /// Each attempt of a scoped handler runs in a fresh DI scope, and each scope is disposed before
    /// the next attempt creates its own.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScopedHandler_GetsFreshScopePerAttempt()
    {
        var log = new ScopeLog();
        var harness = Harness.Create(
            builder =>
            {
                builder.Services.AddSingleton(log);
                builder.Services.AddScoped<ScopeProbe>();
                builder.WithScopedHandler((sp, _, _) =>
                {
                    var probe = sp.GetRequiredService<ScopeProbe>();
                    log.Add($"attempt:{probe.Id}");
                    return probe.Id < 3 ? ValueTask.FromException(new TransientException()) : ValueTask.CompletedTask;
                });
            },
            builder => builder.WithHandlerResilience(Transient(maxRetries: 2)));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(string.Join(",", log.Entries))
            .IsEqualTo("create:1,attempt:1,dispose:1,create:2,attempt:2,dispose:2,create:3,attempt:3,dispose:3");
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(3);
    }

    /// <summary>
    /// Every order of the builder calls gives the same outcomes, attempts, capture, decorator calls,
    /// and scopes: handler resilience is the one retry owner, user decorators run inside it, and
    /// each attempt gets a fresh scope.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EveryBuilderOrder_HasOneRetryOwner()
    {
        const string expected =
            "succeeded=2 failed=2 canceled=0 attempts=7 captured=2"
            + " | dlq=always:3,bad:1"
            + " | decorator=always:3,bad:1,flaky:2,ok:1"
            + " | scopes=always:3,bad:1,flaky:2,ok:1";
        var results = new List<string>();
        foreach (var order in Permutations([0, 1, 2, 3]))
            results.Add(await RunOrderAsync(order));

        await Assert.That(results).HasCount(24);
        foreach (var result in results)
            await Assert.That(result).IsEqualTo(expected);
    }

    /// <summary>
    /// A non-zero dead-letter retry count next to handler resilience fails options validation, in
    /// either builder order, with a message that names the fix.
    /// </summary>
    /// <param name="resilienceFirst">Whether <c>WithHandlerResilience</c> is called first.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DeadLetterRetries_WithHandlerResilience_FailValidation(bool resilienceFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask);
        if (resilienceFirst)
            builder.WithHandlerResilience().WithDeadLetterQueue();
        else
            builder.WithDeadLetterQueue().WithHandlerResilience();
        builder.Build();
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.That(() => provider.GetRequiredService<IWorkOrchestrator<string>>())
            .Throws<OptionsValidationException>();

        await Assert.That(error!.Message).Contains("DeadLetterQueueOptions.MaxRetries must be 0");
        await Assert.That(error.Message).Contains("WithDeadLetterQueue(o => o.MaxRetries = 0)");
    }

    /// <summary>The retry-owner conflict stops the host on start.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterRetries_WithHandlerResilience_FailHostStart()
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .WithDeadLetterQueue(static o => o.MaxRetries = 2)
            .WithHandlerResilience()
            .Build();
        using var host = hostBuilder.Build();

        var error = await Assert.That(() => host.StartAsync()).Throws<OptionsValidationException>();

        await Assert.That(error!.Message).Contains("MaxRetries must be 0");
    }

    /// <summary>Retries without a transient predicate fail options validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_RetriesWithoutPredicate_FailValidation()
    {
        var error = await ResolveWithOptionsAsync(static o => o.MaxRetries = 1);

        await Assert.That(error).Contains("ShouldRetry is required");
    }

    /// <summary>Out-of-range retries, delay, and timeout each fail options validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_OutOfRangeValues_FailValidation()
    {
        var error = await ResolveWithOptionsAsync(static o =>
        {
            o.MaxRetries = HandlerResilienceOptions.MaxRetriesLimit + 1;
            o.ShouldRetry = static _ => true;
            o.RetryDelay = TimeSpan.FromMinutes(2);
            o.AttemptTimeout = TimeSpan.FromMilliseconds(1);
        });

        await Assert.That(error).Contains("MaxRetries must be between 0 and 10");
        await Assert.That(error).Contains("RetryDelay must be between");
        await Assert.That(error).Contains("AttemptTimeout must be between");
    }

    /// <summary>A negative retry count fails options validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_NegativeRetries_FailValidation()
    {
        var error = await ResolveWithOptionsAsync(static o => o.MaxRetries = -1);

        await Assert.That(error).Contains("MaxRetries must be between 0 and 10");
    }

    /// <summary>The defaults turn retries off, set no timeout, and pass validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_Defaults_DisableRetries()
    {
        var options = new HandlerResilienceOptions();
        var result = new HandlerResilienceOptionsValidator().Validate("x", options);

        await Assert.That(options.MaxRetries).IsEqualTo(0);
        await Assert.That(options.AttemptTimeout).IsNull();
        await Assert.That(options.ShouldRetry).IsNull();
        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Calling <c>WithHandlerResilience</c> twice adds one decorator in the reserved band, and both
    /// configure actions apply in call order.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CalledTwice_AddsOneDecoratorAndMergesConfiguration()
    {
        var calls = 0;
        var harness = Harness.Create(
            builder => builder.WithHandler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException(new TransientException());
            }),
            builder => builder.WithHandlerResilience(static o =>
            {
                o.MaxRetries = 5;
                o.RetryDelay = TimeSpan.Zero;
            }),
            builder => builder.WithHandlerResilience(static o =>
            {
                o.MaxRetries = 1;
                o.ShouldRetry = static ex => ex is TransientException;
            }));
        await using var provider = harness.Provider;

        await harness.RunAsync("job");

        await Assert.That(harness.HandlerDecoratorOrders).IsEquivalentTo([HandlerDecoratorOrders.HandlerResilience]);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(2);
    }

    /// <summary>A user decorator added after handler resilience still runs inside it and sees every attempt.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DecoratorAddedAfterResilience_RunsInsideIt()
    {
        var decoratorCalls = 0;
        var handlerCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler((_, _) => Interlocked.Increment(ref handlerCalls) == 1
                ? ValueTask.FromException(new TransientException())
                : ValueTask.CompletedTask)
            .WithHandlerResilience(Transient(maxRetries: 1))
            .WithHandlerDecorator((_, inner) => new CallbackDecorator(inner, () => Interlocked.Increment(ref decoratorCalls)));
        var userOrder = builder.HandlerDecorators.Single(static d => !HandlerDecoratorOrders.IsReserved(d.Order)).Order;
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator.Enqueue("job").IsAccepted).IsTrue();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(userOrder).IsEqualTo(0);
        await Assert.That(decoratorCalls).IsEqualTo(2);
        await Assert.That(handlerCalls).IsEqualTo(2);
    }

    /// <summary>An explicit handler decorator order in the reserved band is rejected.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithHandlerDecorator_ReservedOrder_Throws()
    {
        var builder = new ServiceCollection().AddWorkOrchestrator<string>();

        await Assert.That(() => builder.WithHandlerDecorator(static (_, inner) => inner, HandlerDecoratorOrders.ReservedBandStart))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => builder.WithHandlerDecorator(static (_, inner) => inner, int.MaxValue))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(builder.HandlerDecorators).IsEmpty();
    }

    /// <summary>
    /// The key slot stays held during a retry delay: another item for the same key at concurrency
    /// one does not start, while an item for another key runs on a free worker.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task KeySlot_IsHeldDuringRetryDelay()
    {
        var time = new FakeTimeProvider();
        var starts = new ConcurrentQueue<string>();
        var firstA1 = 0;
        var retryClassified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b1Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 2)
            .WithKeyedDispatch(static w => w.Split(':')[0], maxPendingPerKey: 4, maxConcurrencyPerKey: 1)
            .WithHandler((work, _) =>
            {
                starts.Enqueue(work);
                if (work == "B:1") b1Done.TrySetResult();
                return work == "A:1" && Interlocked.Increment(ref firstA1) == 1
                    ? ValueTask.FromException(new TransientException())
                    : ValueTask.CompletedTask;
            })
            .WithHandlerResilience(o =>
            {
                o.MaxRetries = 1;
                o.RetryDelay = TimeSpan.FromSeconds(30);
                o.ShouldRetry = ex =>
                {
                    retryClassified.TrySetResult();
                    return ex is TransientException;
                };
            })
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = (WorkOrchestrator<string>)provider.GetRequiredService<IWorkOrchestrator<string>>();
        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();

        await Assert.That(orchestrator.Enqueue("A:1").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("A:2").IsAccepted).IsTrue();
        await retryClassified.Task.WaitAsync(WaitTimeout);
        await Assert.That(orchestrator.Enqueue("B:1").IsAccepted).IsTrue();
        await b1Done.Task.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => orchestrator.PendingCount == 1);

        var backlog = orchestrator.Backlog;
        await Assert.That(backlog.Pending).IsEqualTo(1);
        await Assert.That(backlog.Ready).IsEqualTo(0);
        await Assert.That(backlog.Blocked).IsEqualTo(1);
        await Assert.That(starts.Contains("A:2")).IsFalse();

        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(30));
            return starts.Count(static s => s == "A:1") == 2;
        });
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", starts)).IsEqualTo("A:1,B:1,A:1,A:2");
        await Assert.That(counters.Succeeded).IsEqualTo(3);
        await Assert.That(counters.HandlerAttempts).IsEqualTo(4);
    }

    /// <summary>
    /// A handler that ignores the timeout's cancellation keeps its key slot until it exits, and the
    /// next item for the key starts only after that.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task HandlerIgnoringTimeout_KeepsKeySlotUntilExit()
    {
        var events = new ConcurrentQueue<string>();
        var tokenCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new SemaphoreSlim(0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 2)
            .WithKeyedDispatch(static w => w.Split(':')[0], maxPendingPerKey: 4, maxConcurrencyPerKey: 1)
            .WithHandler(async (work, ct) =>
            {
                events.Enqueue($"start:{work}");
                if (work == "A:1")
                {
                    using var registration = ct.Register(() => tokenCanceled.TrySetResult());
                    await release.WaitAsync(CancellationToken.None);
                }

                events.Enqueue($"end:{work}");
            })
            .WithHandlerResilience(static o => o.AttemptTimeout = TimeSpan.FromMilliseconds(50))
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = (WorkOrchestrator<string>)provider.GetRequiredService<IWorkOrchestrator<string>>();
        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();

        await Assert.That(orchestrator.Enqueue("A:1").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("A:2").IsAccepted).IsTrue();
        await tokenCanceled.Task.WaitAsync(WaitTimeout);

        var backlog = orchestrator.Backlog;
        await Assert.That(backlog.Ready).IsEqualTo(0);
        await Assert.That(backlog.Blocked).IsEqualTo(1);
        await Assert.That(events.Contains("start:A:2")).IsFalse();

        release.Release();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", events)).IsEqualTo("start:A:1,end:A:1,start:A:2,end:A:2");
        await Assert.That(counters.Succeeded).IsEqualTo(2);
        await Assert.That(counters.HandlerAttempts).IsEqualTo(2);
    }

    /// <summary>
    /// Called through the plain handler interface, the decorator still runs its pipeline: a direct
    /// caller gets the same retries.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PlainHandleAsync_RunsPipeline()
    {
        var calls = 0;
        var inner = new CallbackHandler(() => Interlocked.Increment(ref calls) == 1
            ? ValueTask.FromException(new TransientException())
            : ValueTask.CompletedTask);
        var options = new HandlerResilienceOptions();
        Transient(maxRetries: 1)(options);
        IWorkHandler<string> handler = new ResilientWorkHandler<string>(
            inner, options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);

        await handler.HandleAsync("job", CancellationToken.None);

        await Assert.That(calls).IsEqualTo(2);
    }

    /// <summary>A predicate that throws is treated as "not transient", and the original failure is kept.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingPredicate_KeepsOriginalFailure()
    {
        var calls = 0;
        var inner = new CallbackHandler(() =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromException(new TransientException());
        });
        var options = new HandlerResilienceOptions
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
            ShouldRetry = static _ => throw new InvalidOperationException("predicate bug"),
        };
        var handler = new ResilientWorkHandler<string>(
            inner, options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);
        var counter = new Bifrost.Handlers.HandlerAttemptCounter();

        await Assert.That(async () => await handler.HandleAsync("job", counter, CancellationToken.None))
            .Throws<TransientException>();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(counter.Count).IsEqualTo(1);
    }

    private static Action<HandlerResilienceOptions> Transient(int maxRetries)
        => o =>
        {
            o.MaxRetries = maxRetries;
            o.RetryDelay = TimeSpan.Zero;
            o.ShouldRetry = static ex => ex is TransientException;
        };

    private static async Task<string> ResolveWithOptionsAsync(Action<HandlerResilienceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .WithHandlerResilience(configure)
            .Build();
        await using var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<IWorkOrchestrator<string>>();
        }
        catch (OptionsValidationException error)
        {
            return error.Message;
        }

        throw new InvalidOperationException("Expected options validation to fail.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    private static async Task<string> RunOrderAsync(IReadOnlyList<int> order)
    {
        var decoratorCalls = new ConcurrentDictionary<string, int>();
        var handlerCalls = new ConcurrentDictionary<string, int>();
        var scopes = new ConcurrentDictionary<string, ConcurrentBag<int>>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ScopeLog());
        services.AddScoped<ScopeProbe>();
        var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1);
        Action<WorkOrchestratorBuilder<string>>[] steps =
        [
            static b => b.WithDeadLetterQueue(static o => o.MaxRetries = 0),
            static b => b.WithHandlerResilience(Transient(maxRetries: 2)),
            b => b.WithHandlerDecorator((_, inner) => new CallbackDecorator(inner, work => decoratorCalls.AddOrUpdate(work, 1, static (_, n) => n + 1))),
            b => b.WithScopedHandler((sp, work, _) =>
            {
                scopes.GetOrAdd(work, static _ => []).Add(sp.GetRequiredService<ScopeProbe>().Id);
                var call = handlerCalls.AddOrUpdate(work, 1, static (_, n) => n + 1);
                return work switch
                {
                    "bad" => ValueTask.FromException(new InvalidOperationException("permanent")),
                    "always" => ValueTask.FromException(new TransientException()),
                    "flaky" when call == 1 => ValueTask.FromException(new TransientException()),
                    _ => ValueTask.CompletedTask,
                };
            }),
        ];
        foreach (var step in order)
            steps[step](builder);
        builder.Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        foreach (var work in new[] { "ok", "bad", "flaky", "always" })
            await Assert.That(orchestrator.Enqueue(work).IsAccepted).IsTrue();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();

        var counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();
        var entries = new List<string>();
        await foreach (var entry in provider.GetRequiredService<IDeadLetterQueue<string>>().ReadAllAsync())
            entries.Add($"{entry.Work}:{entry.AttemptCount}");

        return $"succeeded={counters.Succeeded} failed={counters.Failed} canceled={counters.Canceled}"
            + $" attempts={counters.HandlerAttempts} captured={counters.DeadLetterCaptured}"
            + $" | dlq={Join(entries)}"
            + $" | decorator={Join(decoratorCalls.Select(static p => $"{p.Key}:{p.Value}"))}"
            + $" | scopes={Join(scopes.Select(static p => $"{p.Key}:{p.Value.Distinct().Count()}"))}";
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

    private sealed class Harness
    {
        private Harness(ServiceProvider provider, IReadOnlyList<int> handlerDecoratorOrders)
        {
            Provider = provider;
            Orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
            Counters = provider.GetRequiredService<TerminalOutcomeCounters<string>>();
            HandlerDecoratorOrders = handlerDecoratorOrders;
        }

        internal ServiceProvider Provider { get; }

        internal IWorkOrchestrator<string> Orchestrator { get; }

        internal TerminalOutcomeCounters<string> Counters { get; }

        internal IReadOnlyList<int> HandlerDecoratorOrders { get; }

        internal static Harness Create(params Action<WorkOrchestratorBuilder<string>>[] steps)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var builder = services.AddWorkOrchestrator<string>(static o => o.WorkerCount = 1);
            foreach (var step in steps)
                step(builder);
            var orders = builder.HandlerDecorators.Select(static d => d.Order).ToList();
            builder.Build();
            return new Harness(services.BuildServiceProvider(), orders);
        }

        internal async Task RunAsync(string work)
        {
            await Assert.That(Orchestrator.Enqueue(work).IsAccepted).IsTrue();
            await Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        }

        internal async Task<List<DeadLetteredWork<string>>> DeadLettersAsync()
        {
            var entries = new List<DeadLetteredWork<string>>();
            await Orchestrator.DisposeAsync();
            await foreach (var entry in Provider.GetRequiredService<IDeadLetterQueue<string>>().ReadAllAsync())
                entries.Add(entry);
            return entries;
        }
    }

    private sealed class TransientException : Exception
    {
        public TransientException()
            : base("transient")
        {
        }
    }

    private sealed class ScopeLog
    {
        private int _nextId;

        internal ConcurrentQueue<string> Entries { get; } = new();

        internal int NextId() => Interlocked.Increment(ref _nextId);

        internal void Add(string entry) => Entries.Enqueue(entry);
    }

    private sealed class ScopeProbe : IAsyncDisposable
    {
        private readonly ScopeLog _log;

        public ScopeProbe(ScopeLog log)
        {
            _log = log;
            Id = log.NextId();
            log.Add($"create:{Id}");
        }

        internal int Id { get; }

        public ValueTask DisposeAsync()
        {
            _log.Add($"dispose:{Id}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackDecorator(IWorkHandler<string> inner, Action<string> onCall) : IWorkHandler<string>
    {
        public CallbackDecorator(IWorkHandler<string> inner, Action onCall)
            : this(inner, _ => onCall())
        {
        }

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            onCall(work);
            return inner.HandleAsync(work, ct);
        }
    }

    private sealed class CallbackHandler(Func<ValueTask> callback) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct) => callback();
    }
}
