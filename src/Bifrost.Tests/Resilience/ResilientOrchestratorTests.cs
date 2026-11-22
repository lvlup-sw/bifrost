// =============================================================================
// <copyright file="ResilientOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Resilience;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResilientOrchestrator{TWork}"/> decorator.
/// </summary>
[Property("Category", "Unit")]
public class ResilientOrchestratorTests
{
    private IWorkOrchestrator<string> _inner = null!;
    private IOptions<ResiliencySettings> _options = null!;
    private ILogger<ResilientOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _inner = Substitute.For<IWorkOrchestrator<string>>();
        _options = Options.Create(new ResiliencySettings());
        _logger = NullLogger<ResilientOrchestrator<string>>.Instance;

        // Setup default property values
        _inner.PendingCount.Returns(0);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies constructor throws on null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        await Assert.That(() => new ResilientOrchestrator<string>(null!, _options, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null options.
    /// </summary>
    [Test]
    public async Task Constructor_NullOptions_Throws()
    {
        await Assert.That(() => new ResilientOrchestrator<string>(_inner, null!, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null logger.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        await Assert.That(() => new ResilientOrchestrator<string>(_inner, _options, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies EnqueueAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_DelegatesToInner()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).Returns(EnqueueResult.Accepted);

        var result = await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies EnqueueAsync retries on transient failure.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_TransientFailure_Retries()
    {
        var callCount = 0;
        var settings = new ResiliencySettings
        {
            RetryCount = 2,
            RetryIntervalSeconds = 0,
            UseExponentialBackoff = false,
            TimeoutIntervalSeconds = 10
        };
        var options = Options.Create(settings);
        var orchestrator = new ResilientOrchestrator<string>(_inner, options, _logger);

        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new HttpRequestException("Transient failure");
                }

                return new ValueTask<EnqueueResult>(EnqueueResult.Accepted);
            });

        var result = await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(callCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies TryEnqueue delegates to inner.
    /// </summary>
    [Test]
    public async Task TryEnqueue_DelegatesToInner()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.Enqueue("work").Returns(EnqueueResult.Accepted);

        var result = orchestrator.TryEnqueue("work");

        await Assert.That(result).IsTrue();
        _inner.Received(1).Enqueue("work");
    }

    /// <summary>
    /// Verifies TryEnqueue returns false when inner returns false.
    /// </summary>
    [Test]
    public async Task TryEnqueue_InnerReturnsFalse_ReturnsFalse()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.Enqueue("work").Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));

        var result = orchestrator.TryEnqueue("work");

        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Verifies PendingCount delegates to inner.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        _inner.PendingCount.Returns(42);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        var result = orchestrator.PendingCount;

        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies ActiveWorkers delegates to inner.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        _inner.ActiveWorkers.Returns(8);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        var result = orchestrator.ActiveWorkers;

        await Assert.That(result).IsEqualTo(8);
    }

    /// <summary>
    /// Verifies Capacity delegates to inner.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        _inner.Capacity.Returns(256);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        var result = orchestrator.Capacity;

        await Assert.That(result).IsEqualTo(256);
    }

    /// <summary>
    /// Verifies a rejected admission outcome propagates through the resilience wrapper.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_Rejected_PropagatesResult()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Rejected(RejectionReason.Shutdown));

        var result = await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>
    /// Verifies StopAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.AbortAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await orchestrator.AbortAsync().ConfigureAwait(false);

        await _inner.Received(1).AbortAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies DisposeAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        await orchestrator.DisposeAsync().ConfigureAwait(false);

        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies decorator implements IWorkOrchestrator.
    /// </summary>
    [Test]
    public async Task ResilientOrchestrator_ImplementsIWorkOrchestrator()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        await Assert.That(orchestrator).IsAssignableTo<IWorkOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies DrainAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task DrainAsync_ForwardsToInner()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.DrainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await orchestrator.DrainAsync().ConfigureAwait(false);

        await _inner.Received(1).DrainAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that TryRun bypasses resilience policies and delegates directly to inner.
    /// Synchronous channel writes are CPU-bound and do not experience transient failures,
    /// so resilience policies (retry, timeout, circuit breaker) are not applicable.
    /// </summary>
    [Test]
    public async Task TryRun_BypassesResiliencePolicy()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.Enqueue("work").Returns(EnqueueResult.Accepted);

        var result = orchestrator.TryEnqueue("work");

        // Assert - TryRun delegates directly (exactly once), no retry wrapping
        await Assert.That(result).IsTrue();
        _inner.Received(1).Enqueue("work");
    }

    /// <summary>
    /// Verifies that Run bypasses resilience policies and delegates directly to inner.
    /// Synchronous channel writes are CPU-bound and do not experience transient failures,
    /// so resilience policies (retry, timeout, circuit breaker) are not applicable.
    /// </summary>
    [Test]
    public async Task Run_BypassesResiliencePolicy()
    {
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        orchestrator.Enqueue("work");

        // Assert - Run delegates directly (exactly once), no retry wrapping
        _inner.Received(1).Enqueue("work");
        var callReceived = true;
        await Assert.That(callReceived).IsTrue();
    }
}
