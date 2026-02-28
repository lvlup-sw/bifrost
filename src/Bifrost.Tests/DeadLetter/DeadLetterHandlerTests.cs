// =============================================================================
// <copyright file="DeadLetterHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetterHandler{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterHandlerTests
{
    private IWorkHandler<string> _innerHandler = null!;
    private IDeadLetterQueue<string> _dlq = null!;
    private IDeadLetterNotifier<string> _notifier = null!;
    private IOptions<DeadLetterQueueOptions> _options = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _innerHandler = Substitute.For<IWorkHandler<string>>();
        _dlq = Substitute.For<IDeadLetterQueue<string>>();
        _notifier = Substitute.For<IDeadLetterNotifier<string>>();
        _options = Options.Create(new DeadLetterQueueOptions { MaxRetries = 3 });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that when the inner handler succeeds, work is delegated and returns normally.
    /// </summary>
    [Test]
    public async Task HandleAsync_Success_DelegatesAndReturns()
    {
        // Arrange
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await _innerHandler.Received(1).HandleAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the inner handler is retried MaxRetries+1 times on failure.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_RetriesUpToMaxRetries()
    {
        // Arrange
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert - MaxRetries=3, so 1 initial + 3 retries = 4 total calls
        await _innerHandler.Received(4).HandleAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that after exhausting retries, the item is dead-lettered.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_DeadLettersAfterExhaustion()
    {
        // Arrange
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await _dlq.Received(1).EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the dead-lettered work has correct context.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_DeadLetteredWorkHasCorrectContext()
    {
        // Arrange
        var exception = new InvalidOperationException("test failure");
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(exception)));
        var handler = CreateHandler();

        DeadLetteredWork<string> capturedItem = default;
        await _dlq.EnqueueAsync(Arg.Do<DeadLetteredWork<string>>(item => capturedItem = item), Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(capturedItem.Work).IsEqualTo("work");
        await Assert.That(capturedItem.Exception).IsEqualTo(exception);
        await Assert.That(capturedItem.AttemptCount).IsEqualTo(4); // 1 initial + 3 retries
        await Assert.That(capturedItem.FailedAt).IsNotDefault();
    }

    /// <summary>
    /// Verifies that the notifier is called when work is dead-lettered.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_NotifiesOnDeadLetter()
    {
        // Arrange
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        _notifier.Received(1).Notify(Arg.Any<WorkDeadLetteredEvent<string>>());
    }

    /// <summary>
    /// Verifies that the handler does NOT rethrow after dead-lettering.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_DoesNotRethrow()
    {
        // Arrange
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        // Act & Assert - should not throw
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);
        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that a transient failure succeeds on retry without dead-lettering.
    /// </summary>
    [Test]
    public async Task HandleAsync_TransientFailure_SucceedsOnRetry()
    {
        // Arrange
        var callCount = 0;
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new ValueTask(Task.FromException(new InvalidOperationException("transient")));
                }

                return ValueTask.CompletedTask;
            });
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(callCount).IsEqualTo(2);
        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that OperationCanceledException is rethrown.
    /// </summary>
    [Test]
    public async Task HandleAsync_Cancellation_Rethrows()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new OperationCanceledException())));
        var handler = CreateHandler();

        // Act & Assert
        await Assert.That(async () => await handler.HandleAsync("work", cts.Token).ConfigureAwait(false))
            .Throws<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies that DLQ is never called on cancellation.
    /// </summary>
    [Test]
    public async Task HandleAsync_Cancellation_NeverDeadLetters()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new OperationCanceledException())));
        var handler = CreateHandler();

        // Act
        try
        {
            await handler.HandleAsync("work", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that with MaxRetries=0, work is dead-lettered on first failure.
    /// </summary>
    [Test]
    public async Task HandleAsync_MaxRetries0_DeadLettersImmediately()
    {
        // Arrange
        var options = Options.Create(new DeadLetterQueueOptions { MaxRetries = 0 });
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, options,
            NullLogger<DeadLetterHandler<string>>.Instance);

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert - 1 attempt only (no retries)
        await _innerHandler.Received(1).HandleAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await _dlq.Received(1).EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that constructor throws when inner is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenInnerNull()
    {
        await Assert.That(() => new DeadLetterHandler<string>(
            null!, _dlq, _notifier, _options,
            NullLogger<DeadLetterHandler<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws when DLQ is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenDlqNull()
    {
        await Assert.That(() => new DeadLetterHandler<string>(
            _innerHandler, null!, _notifier, _options,
            NullLogger<DeadLetterHandler<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws when notifier is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenNotifierNull()
    {
        await Assert.That(() => new DeadLetterHandler<string>(
            _innerHandler, _dlq, null!, _options,
            NullLogger<DeadLetterHandler<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws when options is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenOptionsNull()
    {
        await Assert.That(() => new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, null!,
            NullLogger<DeadLetterHandler<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws when logger is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenLoggerNull()
    {
        await Assert.That(() => new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, _options, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that DeadLetterHandler implements IWorkHandler.
    /// </summary>
    [Test]
    public async Task ImplementsIWorkHandler()
    {
        // Arrange
        var type = typeof(DeadLetterHandler<string>);

        // Assert
        await Assert.That(typeof(IWorkHandler<string>).IsAssignableFrom(type)).IsTrue();
    }

    private DeadLetterHandler<string> CreateHandler()
    {
        return new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, _options,
            NullLogger<DeadLetterHandler<string>>.Instance);
    }
}
