// =============================================================================
// <copyright file="ResilientOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

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
        _inner.Writer.Returns(Channel.CreateUnbounded<string>().Writer);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies constructor throws on null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        // Act & Assert
        await Assert.That(() => new ResilientOrchestrator<string>(null!, _options, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null options.
    /// </summary>
    [Test]
    public async Task Constructor_NullOptions_Throws()
    {
        // Act & Assert
        await Assert.That(() => new ResilientOrchestrator<string>(_inner, null!, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null logger.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        // Act & Assert
        await Assert.That(() => new ResilientOrchestrator<string>(_inner, _options, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies EnqueueAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_DelegatesToInner()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        // Assert
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies EnqueueAsync retries on transient failure.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_TransientFailure_Retries()
    {
        // Arrange
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

        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new HttpRequestException("Transient failure");
                }

                return ValueTask.CompletedTask;
            });

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        // Assert
        await Assert.That(callCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies TryEnqueue delegates to inner.
    /// </summary>
    [Test]
    public async Task TryEnqueue_DelegatesToInner()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.TryEnqueue("work").Returns(true);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsTrue();
        _inner.Received(1).TryEnqueue("work");
    }

    /// <summary>
    /// Verifies TryEnqueue returns false when inner returns false.
    /// </summary>
    [Test]
    public async Task TryEnqueue_InnerReturnsFalse_ReturnsFalse()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.TryEnqueue("work").Returns(false);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Verifies PendingCount delegates to inner.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        // Arrange
        _inner.PendingCount.Returns(42);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        // Act
        var result = orchestrator.PendingCount;

        // Assert
        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies ActiveWorkers delegates to inner.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        // Arrange
        _inner.ActiveWorkers.Returns(8);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        // Act
        var result = orchestrator.ActiveWorkers;

        // Assert
        await Assert.That(result).IsEqualTo(8);
    }

    /// <summary>
    /// Verifies Capacity delegates to inner.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        // Arrange
        _inner.Capacity.Returns(256);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        // Act
        var result = orchestrator.Capacity;

        // Assert
        await Assert.That(result).IsEqualTo(256);
    }

    /// <summary>
    /// Verifies Writer delegates to inner.
    /// </summary>
    [Test]
    public async Task Writer_DelegatesToInner()
    {
        // Arrange
        var expectedWriter = Channel.CreateUnbounded<string>().Writer;
        _inner.Writer.Returns(expectedWriter);
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        // Act
        var result = orchestrator.Writer;

        // Assert
        await Assert.That(result).IsEqualTo(expectedWriter);
    }

    /// <summary>
    /// Verifies StopAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        await orchestrator.StopAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies DisposeAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies decorator implements IWorkOrchestrator.
    /// </summary>
    [Test]
    public async Task ResilientOrchestrator_ImplementsIWorkOrchestrator()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);

        // Assert
        await Assert.That(orchestrator).IsAssignableTo<IWorkOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies DrainAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task DrainAsync_ForwardsToInner()
    {
        // Arrange
        var orchestrator = new ResilientOrchestrator<string>(_inner, _options, _logger);
        _inner.DrainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        await orchestrator.DrainAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).DrainAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }
}