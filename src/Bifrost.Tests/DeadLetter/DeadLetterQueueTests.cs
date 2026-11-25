// =============================================================================
// <copyright file="DeadLetterQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetterQueue{TWork}"/> channel-backed implementation.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterQueueTests
{
    /// <summary>
    /// Verifies that constructor throws when options is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenOptionsNull()
    {
        await Assert.That(() => new DeadLetterQueue<string>(null!, NullLogger<DeadLetterQueue<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that enqueueing an item increases the count.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_IncreasesCount()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);
        var item = new DeadLetteredWork<string>("work", null, 1, DateTimeOffset.UtcNow, null);

        await dlq.EnqueueAsync(item);

        await Assert.That(dlq.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that enqueueing multiple items tracks count correctly.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_MultipleItems_TracksCount()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        for (int i = 0; i < 3; i++)
        {
            var item = new DeadLetteredWork<string>($"work-{i}", null, 1, DateTimeOffset.UtcNow, null);
            await dlq.EnqueueAsync(item);
        }

        await Assert.That(dlq.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that ReadAllAsync drains the queue.
    /// </summary>
    [Test]
    public async Task ReadAllAsync_DrainsQueue()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        for (int i = 0; i < 3; i++)
        {
            var item = new DeadLetteredWork<string>($"work-{i}", null, 1, DateTimeOffset.UtcNow, null);
            await dlq.EnqueueAsync(item);
        }

        var results = new List<DeadLetteredWork<string>>();
        await foreach (var item in dlq.ReadAllAsync())
        {
            results.Add(item);
        }

        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(dlq.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ReadAllAsync on an empty queue returns empty.
    /// </summary>
    [Test]
    public async Task ReadAllAsync_EmptyQueue_ReturnsEmpty()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        var results = new List<DeadLetteredWork<string>>();
        await foreach (var item in dlq.ReadAllAsync())
        {
            results.Add(item);
        }

        await Assert.That(results.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ReadAllAsync preserves insertion order.
    /// </summary>
    [Test]
    public async Task ReadAllAsync_PreservesOrder()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("A", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("B", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("C", null, 1, DateTimeOffset.UtcNow, null));

        var results = new List<DeadLetteredWork<string>>();
        await foreach (var item in dlq.ReadAllAsync())
        {
            results.Add(item);
        }

        await Assert.That(results[0].Work).IsEqualTo("A");
        await Assert.That(results[1].Work).IsEqualTo("B");
        await Assert.That(results[2].Work).IsEqualTo("C");
    }

    /// <summary>
    /// Verifies that ReadAllAsync preserves failure context.
    /// </summary>
    [Test]
    public async Task ReadAllAsync_PreservesFailureContext()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);
        var exception = new InvalidOperationException("test failure");
        var failedAt = DateTimeOffset.UtcNow;

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("work", exception, 5, failedAt, "corr-1"));

        var results = new List<DeadLetteredWork<string>>();
        await foreach (var item in dlq.ReadAllAsync())
        {
            results.Add(item);
        }

        await Assert.That(results[0].Exception).IsEqualTo(exception);
        await Assert.That(results[0].AttemptCount).IsEqualTo(5);
        await Assert.That(results[0].FailedAt).IsEqualTo(failedAt);
        await Assert.That(results[0].CorrelationId).IsEqualTo("corr-1");
    }

    /// <summary>
    /// Verifies that count is initially zero.
    /// </summary>
    [Test]
    public async Task Count_InitiallyZero()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        await Assert.That(dlq.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that when at capacity, the oldest item is dropped.
    /// </summary>
    /// <remarks>
    /// The capacity is 3 and the test enqueues 4 items. The oldest item is dropped, and the newest
    /// 3 remain.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_AtCapacity_DropsOldest()
    {
        var options = Options.Create(new DeadLetterQueueOptions { Capacity = 3 });
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("oldest", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("middle1", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("middle2", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("newest", null, 1, DateTimeOffset.UtcNow, null));

        var results = new List<DeadLetteredWork<string>>();
        await foreach (var item in dlq.ReadAllAsync())
        {
            results.Add(item);
        }

        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(results[0].Work).IsEqualTo("middle1");
        await Assert.That(results[1].Work).IsEqualTo("middle2");
        await Assert.That(results[2].Work).IsEqualTo("newest");
    }

    /// <summary>
    /// Verifies that DeadLetterQueue implements IDeadLetterQueue.
    /// </summary>
    [Test]
    public async Task ImplementsIDeadLetterQueue()
    {
        var type = typeof(DeadLetterQueue<string>);

        await Assert.That(typeof(IDeadLetterQueue<string>).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that DroppedCount is initially zero.
    /// </summary>
    [Test]
    public async Task DroppedCount_Initially_ReturnsZero()
    {
        var options = Options.Create(new DeadLetterQueueOptions());
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        await Assert.That(dlq.DroppedCount).IsEqualTo(0L);
    }

    /// <summary>
    /// Verifies that enqueueing at capacity increments DroppedCount.
    /// </summary>
    /// <remarks>
    /// The test fills the queue to capacity. Then it enqueues one more item, which drops the
    /// oldest.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_AtCapacity_IncrementsDroppedCount()
    {
        var options = Options.Create(new DeadLetterQueueOptions { Capacity = 2 });
        var logger = Substitute.For<ILogger<DeadLetterQueue<string>>>();
        var dlq = new DeadLetterQueue<string>(options, logger);

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("first", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("second", null, 1, DateTimeOffset.UtcNow, null));

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("third", null, 1, DateTimeOffset.UtcNow, null));

        await Assert.That(dlq.DroppedCount).IsEqualTo(1L);
    }

    /// <summary>
    /// Verifies that enqueueing below capacity does not increment DroppedCount.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_BelowCapacity_DoesNotIncrementDroppedCount()
    {
        var options = Options.Create(new DeadLetterQueueOptions { Capacity = 5 });
        var dlq = new DeadLetterQueue<string>(options, NullLogger<DeadLetterQueue<string>>.Instance);

        await dlq.EnqueueAsync(new DeadLetteredWork<string>("first", null, 1, DateTimeOffset.UtcNow, null));
        await dlq.EnqueueAsync(new DeadLetteredWork<string>("second", null, 1, DateTimeOffset.UtcNow, null));

        await Assert.That(dlq.DroppedCount).IsEqualTo(0L);
    }
}
