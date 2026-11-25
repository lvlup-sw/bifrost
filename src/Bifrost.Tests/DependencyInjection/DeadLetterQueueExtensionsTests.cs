// =============================================================================
// <copyright file="DeadLetterQueueExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="DeadLetterQueueExtensions"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterQueueExtensionsTests
{
    /// <summary>
    /// Verifies that WithDeadLetterQueue registers the DLQ service.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_RegistersDlqService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithDeadLetterQueue();
        builder.Build();
        var provider = services.BuildServiceProvider();

        var dlq = provider.GetService<IDeadLetterQueue<string>>();
        await Assert.That(dlq).IsNotNull();

        await provider.GetRequiredService<IWorkOrchestrator<string>>().DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue registers the notifier service as concrete type.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_RegistersNotifierService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithDeadLetterQueue();
        builder.Build();
        var provider = services.BuildServiceProvider();

        var notifier = provider.GetService<DeadLetterNotifier<string>>();
        await Assert.That(notifier).IsNotNull();

        await provider.GetRequiredService<IWorkOrchestrator<string>>().DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue registers options.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithDeadLetterQueue();
        builder.Build();
        var provider = services.BuildServiceProvider();

        var options = provider.GetService<Microsoft.Extensions.Options.IOptions<DeadLetterQueueOptions>>();
        await Assert.That(options).IsNotNull();
        await Assert.That(options!.Value.Capacity).IsEqualTo(1000);

        await provider.GetRequiredService<IWorkOrchestrator<string>>().DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue applies custom options.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_AppliesCustomOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithDeadLetterQueue(opts =>
        {
            opts.Capacity = 500;
            opts.MaxRetries = 5;
        });
        builder.Build();
        var provider = services.BuildServiceProvider();

        var options = Options.Create(provider.GetRequiredService<IOptionsMonitor<DeadLetterQueueOptions>>().Get(WorkOptions.Name<string>()));
        await Assert.That(options.Value.Capacity).IsEqualTo(500);
        await Assert.That(options.Value.MaxRetries).IsEqualTo(5);

        await provider.GetRequiredService<IWorkOrchestrator<string>>().DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue wraps handler and routes failures to DLQ.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_WrapsHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var failingHandler = Substitute.For<IWorkHandler<string>>();
        failingHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("fail"))));
        services.AddSingleton(failingHandler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 0);
        builder.Build();
        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        await orchestrator.EnqueueAsync("test-work");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dlq.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        await Assert.That(dlq.Count).IsGreaterThanOrEqualTo(1);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue throws when builder is null.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_ThrowsWhenBuilderNull()
    {
        await Assert.That(() => DeadLetterQueueExtensions.WithDeadLetterQueue<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithDeadLetterQueue returns the same builder for chaining.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_ReturnsSameBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        var result = builder.WithDeadLetterQueue();

        await Assert.That(result).IsEqualTo(builder);
    }
}
