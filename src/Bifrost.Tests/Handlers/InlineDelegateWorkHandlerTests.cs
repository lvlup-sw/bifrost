// =============================================================================
// <copyright file="InlineDelegateWorkHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Handlers;

using TUnit.Core;

namespace Bifrost.Tests.Handlers;

/// <summary>
/// Tests for <see cref="InlineDelegateWorkHandler{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class InlineDelegateWorkHandlerTests
{
    /// <summary>
    /// Verifies that HandleAsync invokes the provided delegate with the correct arguments.
    /// </summary>
    [Test]
    public async Task HandleAsync_InvokesDelegate()
    {
        string? receivedWork = null;
        var handler = new InlineDelegateWorkHandler<string>((work, ct) =>
        {
            receivedWork = work;
            return ValueTask.CompletedTask;
        });

        await handler.HandleAsync("test-work", CancellationToken.None);

        await Assert.That(receivedWork).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that exceptions thrown by the delegate propagate to the caller.
    /// </summary>
    [Test]
    public async Task HandleAsync_PropagatesException()
    {
        var expectedException = new InvalidOperationException("test error");
        var handler = new InlineDelegateWorkHandler<string>((_, _) =>
            throw expectedException);

        await Assert.That(() => handler.HandleAsync("test-work", CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>()
            .WithMessage("test error");
    }

    /// <summary>
    /// Verifies that the cancellation token is forwarded to the delegate.
    /// </summary>
    [Test]
    public async Task HandleAsync_PassesCancellationToken()
    {
        CancellationToken receivedToken = default;
        var handler = new InlineDelegateWorkHandler<string>((_, ct) =>
        {
            receivedToken = ct;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource();

        await handler.HandleAsync("test-work", cts.Token);

        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when the delegate is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenDelegateNull()
    {
        await Assert.That(() => new InlineDelegateWorkHandler<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that InlineDelegateWorkHandler implements IWorkHandler{TWork}.
    /// </summary>
    [Test]
    public async Task ImplementsIWorkHandler()
    {
        var handler = new InlineDelegateWorkHandler<string>((_, _) => ValueTask.CompletedTask);

        await Assert.That(handler).IsAssignableTo<IWorkHandler<string>>();
    }
}
