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
    [Test]
    public async Task Classifier_AppliedWhenPerCallIsDefault()
    {
        // Arrange — quiescent orchestrator (no workers) so envelopes stay observable;
        // the classifier routes work prefixed "batch" to WorkClass.Batch.
        await using var orchestrator = CreateOrchestrator(
            work => work.StartsWith("batch", StringComparison.Ordinal)
                ? WorkClass.Batch
                : WorkClass.Default);

        // Act — per-call class omitted (Default), so the classifier decides.
        var classified = await orchestrator.EnqueueAsync("batch-report").ConfigureAwait(false);
        var unclassified = await orchestrator.EnqueueAsync("plain-item").ConfigureAwait(false);

        // Assert — matching work is enveloped with the classifier's class; work the
        // classifier maps to Default stays Default.
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
    [Test]
    public async Task PerCallClass_NonDefault_WinsOverClassifier()
    {
        // Arrange — the classifier insists on Batch and counts its invocations.
        var classifierCalls = 0;
        await using var orchestrator = CreateOrchestrator(_ =>
        {
            classifierCalls++;
            return WorkClass.Batch;
        });

        // Act — the per-call class is explicit and non-Default.
        var result = await orchestrator.EnqueueAsync("urgent", WorkClass.Interactive).ConfigureAwait(false);

        // Assert — per-call Interactive wins; the classifier was never invoked.
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
        // Arrange — no classifier supplied.
        await using var orchestrator = CreateOrchestrator(classifier: null);

        // Act
        var result = await orchestrator.EnqueueAsync("plain").ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(orchestrator.TryReadEnvelope(out var envelope)).IsTrue();
        await Assert.That(envelope.Class).IsEqualTo(WorkClass.Default);
    }

    /// <summary>
    /// Verifies that every enqueue overload — <c>TryEnqueue</c>, <c>Run</c>, and
    /// <c>TryRun</c> — consults the classifier under the same precedence rule as
    /// <c>EnqueueAsync</c>: per-call non-Default wins, per-call Default defers to
    /// the classifier.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Classifier_AppliesAcrossAllEnqueueOverloads()
    {
        // Arrange — a classifier that maps everything to Batch.
        await using var orchestrator = CreateOrchestrator(_ => WorkClass.Batch);

        // Act / Assert — TryEnqueue with per-call Default defers to the classifier.
        await Assert.That(orchestrator.TryEnqueue("try-enqueue")).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var tryEnvelope)).IsTrue();
        await Assert.That(tryEnvelope.Class).IsEqualTo(WorkClass.Batch);

        // Act / Assert — Run with per-call Default defers to the classifier.
        orchestrator.Run("run");
        await Assert.That(orchestrator.TryReadEnvelope(out var runEnvelope)).IsTrue();
        await Assert.That(runEnvelope.Class).IsEqualTo(WorkClass.Batch);

        // Act / Assert — TryRun with per-call Default defers to the classifier.
        await Assert.That(orchestrator.TryRun("try-run")).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var tryRunEnvelope)).IsTrue();
        await Assert.That(tryRunEnvelope.Class).IsEqualTo(WorkClass.Batch);

        // Act / Assert — per-call non-Default wins over the classifier on the
        // synchronous overloads too.
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
    [Test]
    public async Task Builder_WithClassifier_ThreadsToOrchestrator()
    {
        // Arrange — DI path (options validation requires WorkerCount >= 1, so the
        // class is observed at dequeue via the QueueWaitObserved hook).
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

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsTypeOf<WorkOrchestrator<string>>();
        var concrete = (WorkOrchestrator<string>)orchestrator;

        var observed = new TaskCompletionSource<WorkClass>(TaskCreationOptions.RunContinuationsAsynchronously);
        concrete.QueueWaitObserved = (workClass, _) => observed.TrySetResult(workClass);

        // Act — per-call class omitted, so the threaded classifier decides.
        var result = await concrete.EnqueueAsync("classify-me").ConfigureAwait(false);

        // Assert — the dequeued envelope carries the classifier's class.
        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        var dequeuedClass = await observed.Task.WaitAsync(WaitTimeout).ConfigureAwait(false);
        await Assert.That(dequeuedClass).IsEqualTo(WorkClass.Batch);
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
