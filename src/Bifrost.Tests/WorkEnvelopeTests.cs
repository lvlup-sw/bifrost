// =============================================================================
// <copyright file="WorkEnvelopeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests;

/// <summary>
/// A work handler that signals a completion source with the first item it handles,
/// enabling deterministic enqueue round-trip assertions.
/// </summary>
internal sealed class SignalingWorkHandler : IWorkHandler<string>
{
    private readonly TaskCompletionSource<string> _handled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task that completes with the first work item passed to <see cref="HandleAsync"/>.
    /// </summary>
    public Task<string> Handled => _handled.Task;

    /// <summary>
    /// Handles work by recording the first item and completing immediately.
    /// </summary>
    /// <param name="work">The work item to handle.</param>
    /// <param name="ct">Cancellation token to observe.</param>
    /// <returns>A completed ValueTask.</returns>
    public ValueTask HandleAsync(string work, CancellationToken ct)
    {
        _handled.TrySetResult(work);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Tests for the <see cref="WorkEnvelope{TWork}"/> contract type and the
/// <see cref="WorkOrchestrator{TWork}"/> TimeProvider injection seam (T12, DR-2).
/// </summary>
/// <remarks>
/// T12 scope is deliberately narrow: the envelope type exists and the orchestrator
/// accepts an injected <see cref="TimeProvider"/>. Envelope wrapping on the enqueue
/// path is T13; these tests intentionally do not assert timestamping behavior yet.
/// </remarks>
public class WorkEnvelopeTests
{
    /// <summary>
    /// Verifies that constructing an envelope exposes the work item, work class,
    /// and enqueue timestamp exactly as provided.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task WorkEnvelope_Construct_CapturesProvidedValues()
    {
        // Arrange
        var timestamp = 123_456_789L;

        // Act
        var envelope = new WorkEnvelope<string>("x", WorkClass.Batch, timestamp);

        // Assert
        await Assert.That(envelope.Work).IsEqualTo("x");
        await Assert.That(envelope.Class).IsEqualTo(WorkClass.Batch);
        await Assert.That(envelope.EnqueuedAtTicks).IsEqualTo(timestamp);
    }

    /// <summary>
    /// Verifies that the envelope is a readonly record struct: a value type whose
    /// instance fields are all init-only (immutable after construction).
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task WorkEnvelope_IsReadonlyRecordStruct()
    {
        // Arrange
        var type = typeof(WorkEnvelope<int>);

        // Act
        var isValueType = type.IsValueType;
        var fields = type.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var allInitOnly = fields.All(static f => f.IsInitOnly);

        // Assert
        await Assert.That(isValueType).IsTrue();
        await Assert.That(fields).IsNotEmpty();
        await Assert.That(allInitOnly).IsTrue();
    }

    /// <summary>
    /// Verifies that the orchestrator remains constructible without a TimeProvider
    /// argument (backward compatible) and still round-trips an enqueued item.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task Orchestrator_DefaultCtor_UsesSystemTimeProvider()
    {
        // Arrange
        var handler = new SignalingWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions());

        // Act
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance);
        await orchestrator.EnqueueAsync("ping").ConfigureAwait(false);
        var handled = await handler.Handled.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Assert
        await Assert.That(handled).IsEqualTo("ping");
    }

    /// <summary>
    /// Verifies that the orchestrator accepts an injected <see cref="FakeTimeProvider"/>
    /// and round-trips an enqueued item. Timestamping observability arrives with T13;
    /// construction plus round-trip is the full T12 contract.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task Orchestrator_AcceptsInjectedTimeProvider()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var handler = new SignalingWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions());

        // Act
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance, fakeTime);
        await orchestrator.EnqueueAsync("ping").ConfigureAwait(false);
        var handled = await handler.Handled.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Assert
        await Assert.That(handled).IsEqualTo("ping");
    }
}
