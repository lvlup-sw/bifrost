// =============================================================================
// <copyright file="DeadLetterSubscriberExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="DeadLetterSubscriberExtensions"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterSubscriberExtensionsTests
{
    /// <summary>
    /// Verifies that WithDeadLetterSubscriber type-based overload registers the subscriber in DI.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_TypeBased_RegistersSubscriber()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string>((work, ct) => ValueTask.CompletedTask)
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
            .WithDeadLetterSubscriber<string, TestDeadLetterSubscriber>();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert - subscriber should be registered and resolvable
        var subscriber = provider.GetService<TestDeadLetterSubscriber>();
        await Assert.That(subscriber).IsNotNull();

        // Cleanup
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that type-based subscriber is invoked when a work item is dead-lettered.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_TypeBased_InvokedOnDeadLetter()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Failing handler to trigger dead-lettering
        builder.WithHandler<string>((_, _) => throw new InvalidOperationException("fail"))
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
            .WithDeadLetterSubscriber<string, TestDeadLetterSubscriber>();
        builder.Build();
        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var subscriber = provider.GetRequiredService<TestDeadLetterSubscriber>();

        // Act - enqueue work that will fail and be dead-lettered
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Wait for dead-lettering to complete
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (subscriber.ReceivedItems.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        // Assert
        await Assert.That(subscriber.ReceivedItems.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(subscriber.ReceivedItems[0].Work).IsEqualTo("test-work");
        await Assert.That(subscriber.ReceivedItems[0].Exception).IsNotNull();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that callback-based subscriber is invoked when a work item is dead-lettered.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_Callback_InvokedOnDeadLetter()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var callbackInvoked = new TaskCompletionSource<DeadLetteredWork<string>>();

        builder.WithHandler<string>((_, _) => throw new InvalidOperationException("fail"))
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
            .WithDeadLetterSubscriber<string>((item, ct) =>
            {
                callbackInvoked.TrySetResult(item);
                return Task.CompletedTask;
            });
        builder.Build();
        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act
        await orchestrator.EnqueueAsync("callback-test").ConfigureAwait(false);

        // Wait for callback
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await callbackInvoked.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Work).IsEqualTo("callback-test");
        await Assert.That(result.Exception).IsNotNull();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that multiple subscribers (type-based + callback) are all invoked.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_Multiple_AllInvoked()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var callbackInvoked = new TaskCompletionSource<DeadLetteredWork<string>>();

        builder.WithHandler<string>((_, _) => throw new InvalidOperationException("fail"))
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
            .WithDeadLetterSubscriber<string, TestDeadLetterSubscriber>()
            .WithDeadLetterSubscriber<string>((item, ct) =>
            {
                callbackInvoked.TrySetResult(item);
                return Task.CompletedTask;
            });
        builder.Build();
        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var subscriber = provider.GetRequiredService<TestDeadLetterSubscriber>();

        // Act
        await orchestrator.EnqueueAsync("multi-test").ConfigureAwait(false);

        // Wait for both to be invoked
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var callbackResult = await callbackInvoked.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        // Also wait for the type-based subscriber
        while (subscriber.ReceivedItems.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        // Assert - both subscribers invoked
        await Assert.That(subscriber.ReceivedItems.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(subscriber.ReceivedItems[0].Work).IsEqualTo("multi-test");
        await Assert.That(callbackResult.Work).IsEqualTo("multi-test");

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that using WithDeadLetterSubscriber without WithDeadLetterQueue throws on build
    /// because the DeadLetterNotifier cannot be resolved.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_WithoutWithDeadLetterQueue_ThrowsOnBuild()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>((_, _) => ValueTask.CompletedTask)
            .WithDeadLetterSubscriber<string, TestDeadLetterSubscriber>();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Act & Assert - resolving the orchestrator triggers post-build actions, which need the notifier
        await Assert.That(() => provider.GetRequiredService<IWorkOrchestrator<string>>())
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that WithDeadLetterSubscriber type-based throws when builder is null.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_NullBuilder_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
                DeadLetterSubscriberExtensions.WithDeadLetterSubscriber<string, TestDeadLetterSubscriber>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithDeadLetterSubscriber callback overload throws when callback is null.
    /// </summary>
    [Test]
    public async Task WithDeadLetterSubscriber_NullCallback_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

        // Act & Assert
        await Assert.That(() =>
                builder.WithDeadLetterSubscriber<string>(
                    (Func<DeadLetteredWork<string>, CancellationToken, Task>)null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// A test dead letter subscriber that records received items.
    /// </summary>
    public class TestDeadLetterSubscriber : IDeadLetterSubscriber<string>
    {
        private readonly List<DeadLetteredWork<string>> _items = [];

        /// <summary>
        /// Gets the list of received dead-lettered items.
        /// </summary>
        public IReadOnlyList<DeadLetteredWork<string>> ReceivedItems => _items;

        /// <inheritdoc/>
        public Task HandleAsync(DeadLetteredWork<string> item, CancellationToken ct)
        {
            _items.Add(item);
            return Task.CompletedTask;
        }
    }
}
