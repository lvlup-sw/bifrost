// =============================================================================
// <copyright file="ClassifierOptionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for the options-level work classifier delegate (T14, DR-2): a
/// <c>Func&lt;TWork, WorkClass&gt;</c> alternative to per-call tagging, with the
/// precedence rule pinned exactly — a per-call class other than
/// <see cref="WorkClass.Default"/> wins; per-call <see cref="WorkClass.Default"/>
/// defers to the classifier; with no classifier the class stays
/// <see cref="WorkClass.Default"/>.
/// </summary>
public sealed class ClassifierOptionTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies that when work is enqueued with the per-call class left at
    /// <see cref="WorkClass.Default"/>, the classifier decides the envelope's class.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The orchestrator is quiescent (no workers), so the envelopes stay observable. The classifier
    /// routes work with the prefix "batch" to <c>WorkClass.Batch</c>. The per-call class is omitted
    /// (Default), so the classifier decides. Matching work gets the class of the classifier in its
    /// envelope. Work that the classifier maps to Default stays Default.
    /// </remarks>
    [Test]
    public async Task Classifier_AppliedWhenPerCallIsDefault()
    {
        await using var orchestrator = CreateOrchestrator(
            work => work.StartsWith("batch", StringComparison.Ordinal)
                ? WorkClass.Batch
                : WorkClass.Default);

        var classified = await orchestrator.EnqueueAsync("batch-report");
        var unclassified = await orchestrator.EnqueueAsync("plain-item");

        await Assert.That(classified).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(unclassified).IsEqualTo(EnqueueResult.Accepted);

        await Assert.That(orchestrator.TryReadEnvelope(out var batchEnvelope)).IsTrue();
        await Assert.That(batchEnvelope.Work).IsEqualTo("batch-report");
        await Assert.That(batchEnvelope.Class).IsEqualTo(WorkClass.Batch);

        await Assert.That(orchestrator.TryReadEnvelope(out var plainEnvelope)).IsTrue();
        await Assert.That(plainEnvelope.Work).IsEqualTo("plain-item");
        await Assert.That(plainEnvelope.Class).IsEqualTo(WorkClass.Default);
    }

    /// <summary>
    /// Verifies the precedence rule: a per-call class other than
    /// <see cref="WorkClass.Default"/> wins over the classifier — the classifier
    /// must not even be consulted.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The classifier always returns Batch and counts its invocations. The per-call class is
    /// explicit and not Default. The per-call Interactive class must win, and the classifier must
    /// not be invoked.
    /// </remarks>
    [Test]
    public async Task PerCallClass_NonDefault_WinsOverClassifier()
    {
        var classifierCalls = 0;
        await using var orchestrator = CreateOrchestrator(_ =>
        {
            classifierCalls++;
            return WorkClass.Batch;
        });

        var result = await orchestrator.EnqueueAsync("urgent", WorkClass.Interactive);

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(orchestrator.TryReadEnvelope(out var envelope)).IsTrue();
        await Assert.That(envelope.Class).IsEqualTo(WorkClass.Interactive);
        await Assert.That(classifierCalls).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies the no-classifier baseline: with no classifier configured, a
    /// per-call <see cref="WorkClass.Default"/> stays <see cref="WorkClass.Default"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoClassifier_PerCallDefault_StaysDefault()
    {
        await using var orchestrator = CreateOrchestrator(classifier: null);

        var result = await orchestrator.EnqueueAsync("plain");

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(orchestrator.TryReadEnvelope(out var envelope)).IsTrue();
        await Assert.That(envelope.Class).IsEqualTo(WorkClass.Default);
    }

    /// <summary>
    /// Verifies that the synchronous overloads <c>TryEnqueue</c> and <c>Enqueue</c>
    /// consult the classifier under the same precedence rule as
    /// <c>EnqueueAsync</c>: per-call non-Default wins, per-call Default defers to
    /// the classifier.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The classifier maps everything to Batch. With the per-call class Default, the calls
    /// <c>TryEnqueue("try-enqueue")</c>, <c>Enqueue("run")</c>, and <c>TryEnqueue("try-run")</c>
    /// each defer to the classifier. A per-call class that is not Default wins over the classifier
    /// on the synchronous overloads too.
    /// </remarks>
    [Test]
    public async Task Classifier_AppliesAcrossAllEnqueueOverloads()
    {
        await using var orchestrator = CreateOrchestrator(_ => WorkClass.Batch);

        await Assert.That(orchestrator.TryEnqueue("try-enqueue")).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var tryEnvelope)).IsTrue();
        await Assert.That(tryEnvelope.Class).IsEqualTo(WorkClass.Batch);

        orchestrator.Enqueue("run");
        await Assert.That(orchestrator.TryReadEnvelope(out var runEnvelope)).IsTrue();
        await Assert.That(runEnvelope.Class).IsEqualTo(WorkClass.Batch);

        await Assert.That(orchestrator.TryEnqueue("try-run")).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var tryRunEnvelope)).IsTrue();
        await Assert.That(tryRunEnvelope.Class).IsEqualTo(WorkClass.Batch);

        await Assert.That(orchestrator.TryEnqueue("tagged", WorkClass.Interactive)).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var taggedEnvelope)).IsTrue();
        await Assert.That(taggedEnvelope.Class).IsEqualTo(WorkClass.Interactive);
    }

    /// <summary>
    /// Verifies that <c>WithClassifier</c> on the builder threads the delegate
    /// through to the DI-constructed orchestrator: a worker dequeueing classified
    /// work observes the classifier's class.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>This test uses the DI path. Options validation requires <c>WorkerCount</c> &gt;= 1, so
    /// the test observes the class at dequeue through the <c>QueueWaitObserved</c> hook. The
    /// per-call class is omitted, so the classifier that the builder threads through decides. The
    /// dequeued envelope must carry the class of the classifier.</para>
    /// <para><c>WorkOrchestrator.DisposeAsync</c> owns the cleanup. The test disposes the
    /// orchestrator, not the provider, to prevent the double-dispose path.</para>
    /// </remarks>
    [Test]
    public async Task Builder_WithClassifier_ThreadsToOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
        })
        .WithClassifier(work => WorkClass.Batch)
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsTypeOf<WorkOrchestrator<string>>();
        var concrete = (WorkOrchestrator<string>)orchestrator;

        var observed = new TaskCompletionSource<WorkClass>(TaskCreationOptions.RunContinuationsAsynchronously);
        concrete.QueueWaitObserved = (workClass, _) => observed.TrySetResult(workClass);

        var result = await concrete.EnqueueAsync("classify-me");

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        var dequeuedClass = await observed.Task.WaitAsync(WaitTimeout);
        await Assert.That(dequeuedClass).IsEqualTo(WorkClass.Batch);

        await concrete.DisposeAsync();
    }

    /// <summary>
    /// Creates a quiescent <see cref="WorkOrchestrator{TWork}"/> (no workers, FIFO)
    /// over a no-op substitute handler with the supplied classifier, so enqueued
    /// envelopes stay observable via <c>TryReadEnvelope</c>.
    /// </summary>
    /// <param name="classifier">The classifier delegate, or null for none.</param>
    /// <returns>The constructed orchestrator.</returns>
    private static WorkOrchestrator<string> CreateOrchestrator(Func<string, WorkClass>? classifier)
        => new(
            Substitute.For<IWorkHandler<string>>(),
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            timeProvider: null,
            classifier: classifier);
}
