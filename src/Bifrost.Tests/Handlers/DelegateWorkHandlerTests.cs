// =============================================================================
// <copyright file="DelegateWorkHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Handlers;

namespace Bifrost.Tests.Handlers;

/// <summary>
/// Unit tests for the <see cref="DelegateWorkHandler"/> and <see cref="CancellableDelegateWorkHandler"/> classes.
/// </summary>
/// <remarks>
/// Tests cover delegate execution, null validation, async behavior, cancellation token propagation,
/// and cancellation request handling.
/// </remarks>
public class DelegateWorkHandlerTests
{
    /// <summary>
    /// Verifies that DelegateWorkHandler executes the provided delegate.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler and a flag-setting delegate, acts by calling HandleAsync,
    /// asserts the delegate was invoked by checking the flag.
    /// </remarks>
    [Test]
    public async Task DelegateWorkHandler_HandleAsync_ExecutesDelegate()
    {
        var handler = new DelegateWorkHandler();
        var executed = false;
        Func<Task> work = () =>
        {
            executed = true;
            return Task.CompletedTask;
        };

        await handler.HandleAsync(work, CancellationToken.None);

        await Assert.That(executed).IsTrue();
    }

    /// <summary>
    /// Verifies that DelegateWorkHandler throws ArgumentNullException when work delegate is null.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler, acts by calling HandleAsync with null, asserts ArgumentNullException is thrown.
    /// Guard clause validation ensures callers provide valid delegates.
    /// </remarks>
    [Test]
    public async Task DelegateWorkHandler_HandleAsync_ThrowsWhenWorkNull()
    {
        var handler = new DelegateWorkHandler();

        await Assert.That(() => handler.HandleAsync(null!, CancellationToken.None).AsTask())
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that DelegateWorkHandler properly awaits async delegates to completion.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler and a delayed delegate that appends to an order list, acts by calling HandleAsync
    /// then appending to the list, asserts the delegate completed before the subsequent append.
    /// </remarks>
    [Test]
    public async Task DelegateWorkHandler_HandleAsync_AwaitsAsyncDelegate()
    {
        var handler = new DelegateWorkHandler();
        var completedOrder = new List<int>();

        Func<Task> work = async () =>
        {
            await Task.Delay(10);
            completedOrder.Add(1);
        };

        await handler.HandleAsync(work, CancellationToken.None);
        completedOrder.Add(2);

        await Assert.That(completedOrder).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// Verifies that CancellableDelegateWorkHandler passes the cancellation token to the delegate.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler with a delegate that captures the received token, acts by calling HandleAsync,
    /// asserts the captured token matches the provided CancellationTokenSource token.
    /// </remarks>
    [Test]
    public async Task CancellableDelegateWorkHandler_HandleAsync_PassesCancellationToken()
    {
        var handler = new CancellableDelegateWorkHandler();
        using var cts = new CancellationTokenSource();
        CancellationToken receivedToken = default;

        Func<CancellationToken, Task> work = ct =>
        {
            receivedToken = ct;
            return Task.CompletedTask;
        };

        await handler.HandleAsync(work, cts.Token);

        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that CancellableDelegateWorkHandler throws ArgumentNullException when work delegate is null.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler, acts by calling HandleAsync with null, asserts ArgumentNullException is thrown.
    /// Guard clause validation ensures callers provide valid delegates.
    /// </remarks>
    [Test]
    public async Task CancellableDelegateWorkHandler_HandleAsync_ThrowsWhenWorkNull()
    {
        var handler = new CancellableDelegateWorkHandler();

        await Assert.That(() => handler.HandleAsync(null!, CancellationToken.None).AsTask())
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that CancellableDelegateWorkHandler respects cancellation requests.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler and pre-cancelled token with a delegate that checks cancellation,
    /// acts by calling HandleAsync, asserts OperationCanceledException is thrown.
    /// </remarks>
    [Test]
    public async Task CancellableDelegateWorkHandler_HandleAsync_RespectsCancellation()
    {
        var handler = new CancellableDelegateWorkHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<CancellationToken, Task> work = async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        };

        await Assert.That(() => handler.HandleAsync(work, cts.Token).AsTask())
            .Throws<OperationCanceledException>();
    }
}