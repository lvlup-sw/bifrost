// =============================================================================
// <copyright file="KeyedDispatchExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// End-to-end tests for <see cref="KeyedDispatchExtensions.WithKeyedDispatch"/> through the public
/// builder and DI path: argument checks, the options it sets, per-key order and progress beside a
/// saturated key, both admission limits, the comparer, and rejection of keyed dispatch combined
/// with class priority at build, on resolution, and on host start.
/// </summary>
/// <remarks>
/// Handlers are gated per item with <see cref="TaskCompletionSource"/>, so each test decides when
/// an item finishes. Bounded waits only guard against hangs.
/// </remarks>
[Property("Category", "Unit")]
public class KeyedDispatchExtensionsTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A null builder or selector is rejected.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithKeyedDispatch_NullArguments_Throw()
    {
        var builder = new ServiceCollection().AddWorkOrchestrator<Turn>();

        await Assert.That(() => KeyedDispatchExtensions.WithKeyedDispatch<Turn, string>(null!, static t => t.TenantId, 1, 1))
            .Throws<ArgumentNullException>();
        await Assert.That(() => builder.WithKeyedDispatch<Turn, string>(null!, 1, 1))
            .Throws<ArgumentNullException>();
    }

    /// <summary>A limit below one is rejected when the method is called.</summary>
    /// <param name="maxPendingPerKey">The per-key pending limit.</param>
    /// <param name="maxConcurrencyPerKey">The per-key concurrency limit.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(0, 1)]
    [Arguments(-1, 1)]
    [Arguments(1, 0)]
    [Arguments(1, -1)]
    public async Task WithKeyedDispatch_NonPositiveLimit_Throws(int maxPendingPerKey, int maxConcurrencyPerKey)
    {
        var builder = new ServiceCollection().AddWorkOrchestrator<Turn>();

        await Assert.That(() => builder.WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey, maxConcurrencyPerKey))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The method returns the builder and selects the keyed strategy with Reject full mode, even
    /// when the earlier configure action asked for Wait.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithKeyedDispatch_SetsKeyedStrategyAndRejectMode()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<Turn>(static o => o.FullMode = QueueFullMode.Wait);

        var result = builder.WithKeyedDispatch(static t => (t.TenantId, t.SessionId), maxPendingPerKey: 8, maxConcurrencyPerKey: 1);
        builder.WithHandler(static (_, _) => ValueTask.CompletedTask).Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<Turn>());
        await Assert.That(result).IsSameReferenceAs(builder);
        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.Keyed);
        await Assert.That(options.FullMode).IsEqualTo(QueueFullMode.Reject);
    }

    /// <summary>
    /// With the design sample's tuple key and a cap of one, a session's turns run one at a time in
    /// admission order, and another session uses the free worker meanwhile.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TupleKey_RunsSessionInOrder_AndOtherSessionProgresses()
    {
        var gate = new TurnGate();
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o => o.WorkerCount = 2)
            .WithKeyedDispatch(static t => (t.TenantId, t.SessionId), maxPendingPerKey: 8, maxConcurrencyPerKey: 1)
            .WithHandler(gate.HandleAsync)
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Turn>>();

        await AdmitAsync(orchestrator, new Turn("t1", "s1", 1), new Turn("t1", "s1", 2), new Turn("t1", "s1", 3));
        await gate.WaitStartedAsync(new Turn("t1", "s1", 1));
        await AdmitAsync(orchestrator, new Turn("t1", "s2", 1));
        await gate.WaitStartedAsync(new Turn("t1", "s2", 1));

        await Assert.That(gate.HasStarted(new Turn("t1", "s1", 2))).IsFalse();
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);
        await Assert.That(((WorkOrchestrator<Turn>)orchestrator).Ordering).IsEqualTo(DispatchStrategy.Keyed);

        gate.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        var session1 = gate.Starts().Where(static t => t.SessionId == "s1").Select(static t => t.Seq);
        await Assert.That(string.Join(",", session1)).IsEqualTo("1,2,3");
        await Assert.That(gate.MaxRunning(("t1", "s1"))).IsEqualTo(1);
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Through the decorated chain, admission returns at once with a distinct reason for each
    /// limit, the global limit wins when both are full, and only the capacity rejection is
    /// dead-lettered.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdmissionLimits_HaveDistinctReasons_AndOnlyCapacityIsDeadLettered()
    {
        var gate = new TurnGate();
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o =>
            {
                o.Capacity = 3;
                o.WorkerCount = 1;
            })
            .WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey: 2, maxConcurrencyPerKey: 1)
            .WithHandler(gate.HandleAsync)
            .WithDeadLetterQueue()
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Turn>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<Turn>>();

        await AdmitAsync(orchestrator, new Turn("A", "s", 1));
        await gate.WaitStartedAsync(new Turn("A", "s", 1));
        await AdmitAsync(orchestrator, new Turn("A", "s", 2), new Turn("A", "s", 3));

        var keyFull = await orchestrator.EnqueueAsync(new Turn("A", "s", 4));
        await AdmitAsync(orchestrator, new Turn("B", "s", 1));
        var bothFull = await orchestrator.EnqueueAsync(new Turn("A", "s", 5));
        var globalFull = orchestrator.Enqueue(new Turn("C", "s", 1));

        await Assert.That(keyFull.Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That(bothFull.Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That(globalFull.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        gate.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        var deadLettered = new List<Turn>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            deadLettered.Add(entry.Work);
        }

        await Assert.That(deadLettered).IsEquivalentTo(new Turn[] { new Turn("A", "s", 5), new Turn("C", "s", 1) });
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// A per-key limit larger than the capacity is allowed; the global limit then applies first.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PerKeyLimitAboveCapacity_IsAllowed_AndGlobalLimitApplies()
    {
        var gate = new TurnGate();
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o =>
            {
                o.Capacity = 2;
                o.WorkerCount = 1;
            })
            .WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey: 8, maxConcurrencyPerKey: 1)
            .WithHandler(gate.HandleAsync)
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Turn>>();

        await AdmitAsync(orchestrator, new Turn("A", "s", 1));
        await gate.WaitStartedAsync(new Turn("A", "s", 1));
        await AdmitAsync(orchestrator, new Turn("A", "s", 2), new Turn("A", "s", 3));

        await Assert.That(orchestrator.Enqueue(new Turn("A", "s", 4)).Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        gate.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();
    }

    /// <summary>The comparer decides which items share a key.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Comparer_GroupsKeysThatItTreatsAsEqual()
    {
        var gate = new TurnGate();
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o => o.WorkerCount = 1)
            .WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey: 1, maxConcurrencyPerKey: 1, StringComparer.OrdinalIgnoreCase)
            .WithHandler(gate.HandleAsync)
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Turn>>();

        await AdmitAsync(orchestrator, new Turn("tenant", "s", 1));
        await gate.WaitStartedAsync(new Turn("tenant", "s", 1));
        await AdmitAsync(orchestrator, new Turn("TENANT", "s", 2));

        await Assert.That(orchestrator.Enqueue(new Turn("Tenant", "s", 3)).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);

        gate.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();
    }

    /// <summary>A second call replaces the limits of the first.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SecondCall_ReplacesFirst()
    {
        var gate = new TurnGate();
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o => o.WorkerCount = 1)
            .WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey: 8, maxConcurrencyPerKey: 4)
            .WithKeyedDispatch(static t => t.TenantId, maxPendingPerKey: 1, maxConcurrencyPerKey: 1)
            .WithHandler(gate.HandleAsync)
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Turn>>();

        await AdmitAsync(orchestrator, new Turn("A", "s", 1));
        await gate.WaitStartedAsync(new Turn("A", "s", 1));
        await AdmitAsync(orchestrator, new Turn("A", "s", 2));

        await Assert.That(orchestrator.Enqueue(new Turn("A", "s", 3)).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);

        gate.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();
    }

    /// <summary>Keyed dispatch with priority dispatch is rejected at build, in either call order.</summary>
    /// <param name="keyedFirst">Whether <c>WithKeyedDispatch</c> is called first.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Build_KeyedWithPriorityDispatch_Throws(bool keyedFirst)
    {
        var builder = CreateServices().AddWorkOrchestrator<Turn>()
            .WithHandler(static (_, _) => ValueTask.CompletedTask);
        if (keyedFirst)
            builder.WithKeyedDispatch(static t => t.TenantId, 1, 1).UsePriorityDispatch(DispatchStrategy.PriorityExact);
        else
            builder.UsePriorityDispatch(DispatchStrategy.PriorityRelaxed).WithKeyedDispatch(static t => t.TenantId, 1, 1);

        var error = await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();

        await Assert.That(error!.Message).Contains("UsePriorityDispatch");
    }

    /// <summary>Keyed dispatch with a work classifier is rejected at build, in either call order.</summary>
    /// <param name="keyedFirst">Whether <c>WithKeyedDispatch</c> is called first.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Build_KeyedWithClassifier_Throws(bool keyedFirst)
    {
        var builder = CreateServices().AddWorkOrchestrator<Turn>()
            .WithHandler(static (_, _) => ValueTask.CompletedTask);
        if (keyedFirst)
            builder.WithKeyedDispatch(static t => t.TenantId, 1, 1).WithClassifier(static _ => WorkClass.Batch);
        else
            builder.WithClassifier(static _ => WorkClass.Batch).WithKeyedDispatch(static t => t.TenantId, 1, 1);

        var error = await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();

        await Assert.That(error!.Message).Contains("WithClassifier");
    }

    /// <summary>
    /// A priority strategy set through options is kept, not overwritten, and fails options
    /// validation when the orchestrator is resolved.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_PriorityStrategyWithKeyed_FailsValidation()
    {
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o =>
            {
                o.DispatchStrategy = DispatchStrategy.PriorityExact;
                o.FullMode = QueueFullMode.Reject;
            })
            .WithKeyedDispatch(static t => t.TenantId, 1, 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .Build();
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.That(() => provider.GetRequiredService<IWorkOrchestrator<Turn>>())
            .Throws<OptionsValidationException>();

        await Assert.That(error!.Message).Contains("cannot be combined with class priority");
    }

    /// <summary>Wait full mode set after keyed dispatch fails options validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_WaitModeAfterKeyed_FailsValidation()
    {
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>()
            .WithKeyedDispatch(static t => t.TenantId, 1, 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .Build();
        services.Configure<WorkOrchestratorOptions>(WorkOptions.Name<Turn>(), static o => o.FullMode = QueueFullMode.Wait);
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.That(() => provider.GetRequiredService<IWorkOrchestrator<Turn>>())
            .Throws<OptionsValidationException>();

        await Assert.That(error!.Message).Contains("supports only QueueFullMode.Reject");
    }

    /// <summary>The keyed strategy without <c>WithKeyedDispatch</c> fails options validation.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_KeyedStrategyWithoutWithKeyedDispatch_FailsValidation()
    {
        var services = CreateServices();
        services.AddWorkOrchestrator<Turn>(static o =>
            {
                o.DispatchStrategy = DispatchStrategy.Keyed;
                o.FullMode = QueueFullMode.Reject;
            })
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .Build();
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.That(() => provider.GetRequiredService<IWorkOrchestrator<Turn>>())
            .Throws<OptionsValidationException>();

        await Assert.That(error!.Message).Contains("WithKeyedDispatch");
    }

    /// <summary>A keyed configuration conflict stops the host on start.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ValidateOnStart_KeyedConflict_FailsHostStart()
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddWorkOrchestrator<Turn>(static o =>
            {
                o.DispatchStrategy = DispatchStrategy.PriorityRelaxed;
                o.FullMode = QueueFullMode.Reject;
            })
            .WithKeyedDispatch(static t => t.TenantId, 1, 1)
            .WithHandler(static (_, _) => ValueTask.CompletedTask)
            .Build();
        using var host = hostBuilder.Build();

        await Assert.That(() => host.StartAsync()).Throws<OptionsValidationException>();
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static async Task AdmitAsync(IWorkOrchestrator<Turn> orchestrator, params Turn[] turns)
    {
        foreach (var turn in turns)
        {
            var result = await orchestrator.EnqueueAsync(turn);
            if (!result.IsAccepted)
                throw new InvalidOperationException($"Setup admission of {turn} was rejected with {result.Reason}.");
        }
    }

    /// <summary>A unit of work keyed by tenant and session.</summary>
    /// <param name="TenantId">The tenant.</param>
    /// <param name="SessionId">The session within the tenant.</param>
    /// <param name="Seq">The turn's position within the session.</param>
    private sealed record Turn(string TenantId, string SessionId, int Seq);

    /// <summary>Gates each turn until the test opens it and records starts and concurrency.</summary>
    private sealed class TurnGate
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Turn, TaskCompletionSource> _started = [];
        private readonly Dictionary<Turn, TaskCompletionSource> _release = [];
        private readonly List<Turn> _starts = [];
        private readonly Dictionary<(string, string), int> _running = [];
        private readonly Dictionary<(string, string), int> _maxRunning = [];
        private bool _openAll;

        public async ValueTask HandleAsync(Turn turn, CancellationToken ct)
        {
            Task release;
            var key = (turn.TenantId, turn.SessionId);
            lock (_gate)
            {
                _starts.Add(turn);
                var running = _running.GetValueOrDefault(key) + 1;
                _running[key] = running;
                _maxRunning[key] = Math.Max(_maxRunning.GetValueOrDefault(key), running);
                Signal(_started, turn).TrySetResult();
                release = _openAll ? Task.CompletedTask : Signal(_release, turn).Task;
            }

            try
            {
                await release.WaitAsync(ct);
            }
            finally
            {
                lock (_gate) _running[key]--;
            }
        }

        public Task WaitStartedAsync(Turn turn)
        {
            lock (_gate) return Signal(_started, turn).Task.WaitAsync(WaitTimeout);
        }

        public bool HasStarted(Turn turn)
        {
            lock (_gate) return _starts.Contains(turn);
        }

        public void OpenAll()
        {
            lock (_gate)
            {
                _openAll = true;
                foreach (var signal in _release.Values) signal.TrySetResult();
            }
        }

        public List<Turn> Starts()
        {
            lock (_gate) return [.. _starts];
        }

        public int MaxRunning((string TenantId, string SessionId) key)
        {
            lock (_gate) return _maxRunning.GetValueOrDefault(key);
        }

        private static TaskCompletionSource Signal(Dictionary<Turn, TaskCompletionSource> signals, Turn turn)
        {
            if (!signals.TryGetValue(turn, out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signals[turn] = signal;
            }

            return signal;
        }
    }
}
