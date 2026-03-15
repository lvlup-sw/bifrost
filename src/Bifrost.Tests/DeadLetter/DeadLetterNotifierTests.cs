// =============================================================================
// <copyright file="DeadLetterNotifierTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetterNotifier{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterNotifierTests
{
    /// <summary>
    /// Verifies that Notify with a subscriber invokes the callback.
    /// </summary>
    [Test]
    public async Task Notify_WithSubscriber_InvokesCallback()
    {
        // Arrange
        var notifier = new DeadLetterNotifier<string>();
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
        var notifier = new DeadLetterNotifier<string>();
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
        var notifier = new DeadLetterNotifier<string>();

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
        var notifier = new DeadLetterNotifier<string>();
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
    /// Verifies that subscribing when already subscribed throws InvalidOperationException.
    /// </summary>
    [Test]
    public async Task Subscribe_WhenAlreadySubscribed_ThrowsInvalidOperationException()
    {
        // Arrange
        var notifier = new DeadLetterNotifier<string>();
        notifier.Subscribe(_ => { });

        // Act & Assert
        await Assert.That(() => notifier.Subscribe(_ => { }))
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that after disposing a subscription, re-subscribing is allowed.
    /// </summary>
    [Test]
    public async Task Subscribe_AfterDispose_AllowsResubscription()
    {
        // Arrange
        var notifier = new DeadLetterNotifier<string>();
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
}
