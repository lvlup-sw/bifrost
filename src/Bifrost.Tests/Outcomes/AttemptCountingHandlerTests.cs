// =============================================================================
// <copyright file="AttemptCountingHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Handlers;
using Bifrost.Outcomes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TUnit.Core;

namespace Bifrost.Tests.Outcomes;

/// <summary>
/// Verifies that the orchestrator records the attempts an <see cref="IAttemptCountingHandler{TWork}"/>
/// counts, and that the reserved handler decorator band keeps user decorators below it.
/// </summary>
[Property("Category", "Unit")]
public sealed class AttemptCountingHandlerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The terminal outcome uses the counted attempts, and never fewer than the orchestrator's own
    /// attempt loop made.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CountedAttempts_ReachTheOutcomeStage()
    {
        var records = new List<TerminalOutcomeRecord>();
        var handler = new ScriptedCountingHandler();
        await using var orchestrator = new WorkOrchestrator<string>(
            handler,
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 1 }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        orchestrator.Outcomes.Observed = record =>
        {
            lock (records) records.Add(record);
        };

        await Assert.That(orchestrator.Enqueue("three").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("none-then-fail").IsAccepted).IsTrue();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(handler.PlainCalls).IsEqualTo(0);
        await Assert.That(orchestrator.Outcomes.Counters.HandlerAttempts).IsEqualTo(4);
        await Assert.That(records.Select(static r => $"{r.Outcome}:{r.Attempts}"))
            .IsEquivalentTo(["Succeeded:3", "Failed:1"]);
    }

    /// <summary>The automatic order of a user decorator skips the reserved band and keeps negative orders.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AutomaticOrder_IgnoresReservedBand()
    {
        var builder = new ServiceCollection().AddWorkOrchestrator<string>();
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            HandlerDecoratorOrders.HandlerResilience, static (_, inner) => inner));
        builder.WithHandlerDecorator(static (_, inner) => inner, order: -5);
        builder.WithHandlerDecorator(static (_, inner) => inner);

        await Assert.That(builder.HandlerDecorators.Select(static d => d.Order))
            .IsEquivalentTo([HandlerDecoratorOrders.HandlerResilience, -5, -4]);
    }

    private sealed class ScriptedCountingHandler : IAttemptCountingHandler<string>
    {
        private int _plainCalls;

        internal int PlainCalls => Volatile.Read(ref _plainCalls);

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            Interlocked.Increment(ref _plainCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask HandleAsync(string work, HandlerAttemptCounter attempts, CancellationToken ct)
        {
            if (work != "three")
                return ValueTask.FromException(new InvalidOperationException("fails before counting"));

            attempts.Increment();
            attempts.Increment();
            attempts.Increment();
            return ValueTask.CompletedTask;
        }
    }
}
