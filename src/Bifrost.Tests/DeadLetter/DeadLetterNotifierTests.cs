// =============================================================================
// <copyright file="DeadLetterNotifierTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetterNotifier{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterNotifierTests
{
    private static DeadLetterNotifier<string> CreateNotifier()
        => new(Substitute.For<ILogger<DeadLetterNotifier<string>>>());

    /// <summary>
    /// Verifies that Notify with a subscriber invokes the callback.
    /// </summary>
    [Test]
    public async Task Notify_WithSubscriber_InvokesCallback()
    {
        // Arrange
        var notifier = CreateNotifier();
        WorkDeadLetteredEvent<string>? received = null;
        notifier.Subscribe(evt => received = evt);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);

        // Assert
        await Assert.That(received).IsEqualTo(evt);
    }

    /// <summary>
    /// Verifies that Notify without a subscriber does not throw.
    /// </summary>
    [Test]
    public async Task Notify_WithoutSubscriber_DoesNotThrow()
    {
        // Arrange
        var notifier = CreateNotifier();
        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act - should not throw
        notifier.Notify(evt);

        // Assert - if we get here, no exception was thrown
        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that Subscribe returns a disposable.
    /// </summary>
    [Test]
    public async Task Subscribe_ReturnsDisposable()
    {
        // Arrange
        var notifier = CreateNotifier();

        // Act
        var subscription = notifier.Subscribe(_ => { });

        // Assert
        await Assert.That(subscription).IsNotNull();
    }

    /// <summary>
    /// Verifies that disposing the subscription removes the subscriber.
    /// </summary>
    [Test]
    public async Task Subscribe_Dispose_RemovesSubscriber()
    {
        // Arrange
        var notifier = CreateNotifier();
        var callCount = 0;
        var subscription = notifier.Subscribe(_ => callCount++);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);
        subscription.Dispose();
        notifier.Notify(evt);

        // Assert
        await Assert.That(callCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that multiple subscribers can be registered without throwing.
    /// Replaces the old single-subscriber restriction test.
    /// </summary>
    [Test]
    public async Task Subscribe_Multiple_DoesNotThrow()
    {
        // Arrange
        var notifier = CreateNotifier();
        notifier.Subscribe(_ => { });

        // Act & Assert - second Subscribe should NOT throw
        var secondSubscription = notifier.Subscribe(_ => { });
        await Assert.That(secondSubscription).IsNotNull();
    }

    /// <summary>
    /// Verifies that after disposing a subscription, re-subscribing is allowed.
    /// </summary>
    [Test]
    public async Task Subscribe_AfterDispose_AllowsResubscription()
    {
        // Arrange
        var notifier = CreateNotifier();
        var firstSubscription = notifier.Subscribe(_ => { });

        // Act
        firstSubscription.Dispose();
        var secondCallCount = 0;
        var secondSubscription = notifier.Subscribe(_ => secondCallCount++);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);
        notifier.Notify(evt);

        // Assert
        await Assert.That(secondCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that Notify invokes all registered subscribers when multiple are registered.
    /// </summary>
    [Test]
    public async Task Notify_WithMultipleSubscribers_InvokesAllCallbacks()
    {
        // Arrange
        var notifier = CreateNotifier();
        var callCount1 = 0;
        var callCount2 = 0;
        var callCount3 = 0;
        notifier.Subscribe(_ => callCount1++);
        notifier.Subscribe(_ => callCount2++);
        notifier.Subscribe(_ => callCount3++);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);

        // Allow fire-and-forget tasks to complete
        await Task.Delay(100).ConfigureAwait(false);

        // Assert
        await Assert.That(callCount1).IsEqualTo(1);
        await Assert.That(callCount2).IsEqualTo(1);
        await Assert.That(callCount3).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that when one subscriber throws, other subscribers are still invoked.
    /// </summary>
    [Test]
    public async Task Notify_SubscriberThrows_OtherSubscribersStillCalled()
    {
        // Arrange
        var notifier = CreateNotifier();
        var beforeCallCount = 0;
        var afterCallCount = 0;

        notifier.Subscribe(_ => beforeCallCount++);
        notifier.SubscribeAsync(_ => throw new InvalidOperationException("subscriber error"));
        notifier.Subscribe(_ => afterCallCount++);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);

        // Allow fire-and-forget tasks to complete
        await Task.Delay(100).ConfigureAwait(false);

        // Assert - both non-throwing subscribers should have been called
        await Assert.That(beforeCallCount).IsEqualTo(1);
        await Assert.That(afterCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that when a subscriber throws, the exception does not propagate to the caller of Notify.
    /// </summary>
    [Test]
    public async Task Notify_SubscriberThrows_DoesNotPropagateToCallerSynchronously()
    {
        // Arrange
        var notifier = CreateNotifier();
        notifier.SubscribeAsync(_ => throw new InvalidOperationException("subscriber error"));

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act - should NOT throw
        notifier.Notify(evt);

        // Allow fire-and-forget tasks to complete
        await Task.Delay(100).ConfigureAwait(false);

        // Assert - if we get here, no exception was thrown synchronously
        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that the async Subscribe overload works with async callbacks.
    /// </summary>
    [Test]
    public async Task SubscribeAsync_WithAsyncCallback_InvokesCallback()
    {
        // Arrange
        var notifier = CreateNotifier();
        WorkDeadLetteredEvent<string>? received = null;
        notifier.SubscribeAsync(async evt =>
        {
            await Task.Yield();
            received = evt;
        });

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);

        // Allow fire-and-forget tasks to complete
        await Task.Delay(100).ConfigureAwait(false);

        // Assert
        await Assert.That(received).IsEqualTo(evt);
    }
}
