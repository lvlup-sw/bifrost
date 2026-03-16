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
        // Arrange
        string? receivedWork = null;
        var handler = new InlineDelegateWorkHandler<string>((work, ct) =>
        {
            receivedWork = work;
            return ValueTask.CompletedTask;
        });

        // Act
        await handler.HandleAsync("test-work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(receivedWork).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that exceptions thrown by the delegate propagate to the caller.
    /// </summary>
    [Test]
    public async Task HandleAsync_PropagatesException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("test error");
        var handler = new InlineDelegateWorkHandler<string>((_, _) =>
            throw expectedException);

        // Act & Assert
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
        // Arrange
        CancellationToken receivedToken = default;
        var handler = new InlineDelegateWorkHandler<string>((_, ct) =>
        {
            receivedToken = ct;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource();

        // Act
        await handler.HandleAsync("test-work", cts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when the delegate is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenDelegateNull()
    {
        // Act & Assert
        await Assert.That(() => new InlineDelegateWorkHandler<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that InlineDelegateWorkHandler implements IWorkHandler{TWork}.
    /// </summary>
    [Test]
    public async Task ImplementsIWorkHandler()
    {
        // Arrange
        var handler = new InlineDelegateWorkHandler<string>((_, _) => ValueTask.CompletedTask);

        // Assert
        await Assert.That(handler).IsAssignableTo<IWorkHandler<string>>();
    }
}
