// =============================================================================
// <copyright file="DeadLetterHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging;
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
    private DeadLetterNotifier<string> _notifier = null!;
    private IOptions<DeadLetterQueueOptions> _options = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _innerHandler = Substitute.For<IWorkHandler<string>>();
        _dlq = Substitute.For<IDeadLetterQueue<string>>();
        _notifier = new DeadLetterNotifier<string>(Substitute.For<ILogger<DeadLetterNotifier<string>>>());
        _options = Options.Create(new DeadLetterQueueOptions { MaxRetries = 3 });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases the notifier so its shutdown flag is observed by later tests.
    /// </summary>
    [After(Test)]
    public Task Cleanup()
    {
        this._notifier.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that when the inner handler succeeds, work is delegated and returns normally.
    /// </summary>
    [Test]
    public async Task HandleAsync_Success_DelegatesAndReturns()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var handler = CreateHandler();

        await handler.HandleAsync("work", CancellationToken.None);

        await _innerHandler.Received(1).HandleAsync("work", Arg.Any<CancellationToken>());
        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that the inner handler is retried MaxRetries+1 times on failure.
    /// </summary>
    /// <remarks>
    /// <c>MaxRetries</c> is 3, so the handler gets 1 initial call + 3 retries = 4 calls.
    /// </remarks>
    [Test]
    public async Task HandleAsync_Failure_RetriesUpToMaxRetries()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        await handler.HandleAsync("work", CancellationToken.None);

        await _innerHandler.Received(4).HandleAsync("work", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that after exhausting retries, the item is dead-lettered.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_DeadLettersAfterExhaustion()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        await handler.HandleAsync("work", CancellationToken.None);

        await _dlq.Received(1).EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that the dead-lettered work has correct context.
    /// </summary>
    /// <remarks>
    /// <c>AttemptCount</c> is 4: 1 initial attempt + 3 retries.
    /// </remarks>
    [Test]
    public async Task HandleAsync_Failure_DeadLetteredWorkHasCorrectContext()
    {
        var exception = new InvalidOperationException("test failure");
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(exception)));
        var handler = CreateHandler();

        DeadLetteredWork<string> capturedItem = default;
        await _dlq.EnqueueAsync(Arg.Do<DeadLetteredWork<string>>(item => capturedItem = item), Arg.Any<CancellationToken>());

        await handler.HandleAsync("work", CancellationToken.None);

        await Assert.That(capturedItem.Work).IsEqualTo("work");
        await Assert.That(capturedItem.Exception).IsEqualTo(exception);
        await Assert.That(capturedItem.AttemptCount).IsEqualTo(4);
        await Assert.That(capturedItem.FailedAt).IsNotDefault();
    }

    /// <summary>
    /// Verifies that the notifier is called when work is dead-lettered.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_NotifiesOnDeadLetter()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();
        var notificationCount = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _notifier.Subscribe(_ =>
        {
            Interlocked.Increment(ref notificationCount);
            notified.TrySetResult();
        });

        await handler.HandleAsync("work", CancellationToken.None);

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(notificationCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that the handler does NOT rethrow after dead-lettering.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_DoesNotRethrow()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = CreateHandler();

        await handler.HandleAsync("work", CancellationToken.None);
        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that a transient failure succeeds on retry without dead-lettering.
    /// </summary>
    [Test]
    public async Task HandleAsync_TransientFailure_SucceedsOnRetry()
    {
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

        await handler.HandleAsync("work", CancellationToken.None);

        await Assert.That(callCount).IsEqualTo(2);
        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that OperationCanceledException is rethrown.
    /// </summary>
    [Test]
    public async Task HandleAsync_Cancellation_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new OperationCanceledException())));
        var handler = CreateHandler();

        await Assert.That(async () => await handler.HandleAsync("work", cts.Token))
            .Throws<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies that DLQ is never called on cancellation.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected.
    /// </remarks>
    [Test]
    public async Task HandleAsync_Cancellation_NeverDeadLetters()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new OperationCanceledException())));
        var handler = CreateHandler();

        try
        {
            await handler.HandleAsync("work", cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _dlq.DidNotReceive().EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that with MaxRetries=0, work is dead-lettered on first failure.
    /// </summary>
    /// <remarks>
    /// The handler gets 1 attempt only, with no retries.
    /// </remarks>
    [Test]
    public async Task HandleAsync_MaxRetries0_DeadLettersImmediately()
    {
        var options = Options.Create(new DeadLetterQueueOptions { MaxRetries = 0 });
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        var handler = new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, options,
            NullLogger<DeadLetterHandler<string>>.Instance);

        await handler.HandleAsync("work", CancellationToken.None);

        await _innerHandler.Received(1).HandleAsync("work", Arg.Any<CancellationToken>());
        await _dlq.Received(1).EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
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
        var type = typeof(DeadLetterHandler<string>);

        await Assert.That(typeof(IWorkHandler<string>).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that when a publish callback is provided and work is dead-lettered,
    /// the callback is invoked with a WorkDeadLetteredEvent.
    /// </summary>
    /// <remarks>
    /// <c>AttemptCount</c> is 4: 1 initial attempt + 3 retries.
    /// </remarks>
    [Test]
    public async Task HandleAsync_Failure_WithPublishCallback_PublishesEvent()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));

        IOrchestratorEvent? capturedEvent = null;
        Action<IOrchestratorEvent> publishCallback = evt => capturedEvent = evt;

        var handler = CreateHandler(publishCallback);

        await handler.HandleAsync("work", CancellationToken.None);

        await Assert.That(capturedEvent).IsNotNull();
        await Assert.That(capturedEvent).IsTypeOf<WorkDeadLetteredEvent<string>>();

        var deadLetteredEvent = (WorkDeadLetteredEvent<string>)capturedEvent!;
        await Assert.That(deadLetteredEvent.Work).IsEqualTo("work");
        await Assert.That(deadLetteredEvent.AttemptCount).IsEqualTo(4);
        await Assert.That(deadLetteredEvent.Exception).IsNotNull();
    }

    /// <summary>
    /// Verifies that when the publish callback is null, dead-lettering still completes normally.
    /// </summary>
    [Test]
    public async Task HandleAsync_Failure_WithNullPublishCallback_StillDeadLetters()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));

        var handler = CreateHandler(eventPublishCallback: null);

        await handler.HandleAsync("work", CancellationToken.None);

        await _dlq.Received(1).EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that the publish callback is not invoked when work succeeds.
    /// </summary>
    [Test]
    public async Task HandleAsync_Success_PublishCallbackNotInvoked()
    {
        _innerHandler.HandleAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        var callbackInvoked = false;
        Action<IOrchestratorEvent> publishCallback = _ => callbackInvoked = true;

        var handler = CreateHandler(publishCallback);

        await handler.HandleAsync("work", CancellationToken.None);

        await Assert.That(callbackInvoked).IsFalse();
    }

    /// <summary>
    /// Verifies that the constructor accepts a null publish callback (optional parameter).
    /// </summary>
    [Test]
    public async Task Constructor_AcceptsNullPublishCallback()
    {
        var handler = new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, _options,
            NullLogger<DeadLetterHandler<string>>.Instance,
            eventPublishCallback: null);

        await Assert.That(handler).IsNotNull();
    }

    private DeadLetterHandler<string> CreateHandler(Action<IOrchestratorEvent>? eventPublishCallback = null)
    {
        return new DeadLetterHandler<string>(
            _innerHandler, _dlq, _notifier, _options,
            NullLogger<DeadLetterHandler<string>>.Instance,
            eventPublishCallback);
    }
}
