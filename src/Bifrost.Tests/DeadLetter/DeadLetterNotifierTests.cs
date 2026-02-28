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
    /// Verifies that subscribing overwrites the previous subscriber.
    /// </summary>
    [Test]
    public async Task Subscribe_OverwritesPreviousSubscriber()
    {
        // Arrange
        var notifier = new DeadLetterNotifier<string>();
        var firstCallCount = 0;
        var secondCallCount = 0;

        notifier.Subscribe(_ => firstCallCount++);
        notifier.Subscribe(_ => secondCallCount++);

        var evt = new WorkDeadLetteredEvent<string>("work", null, 3, DateTimeOffset.UtcNow);

        // Act
        notifier.Notify(evt);

        // Assert
        await Assert.That(firstCallCount).IsEqualTo(0);
        await Assert.That(secondCallCount).IsEqualTo(1);
    }
}
