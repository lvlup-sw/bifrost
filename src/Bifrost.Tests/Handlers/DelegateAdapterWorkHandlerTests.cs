// =============================================================================
// <copyright file="DelegateAdapterWorkHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Handlers;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.Handlers;

/// <summary>
/// Tests for <see cref="TaskDelegateWorkHandler{TWork}"/> and <see cref="ScopedDelegateWorkHandler{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class DelegateAdapterWorkHandlerTests
{
    /// <summary>
    /// Verifies that the task adapter passes the work item and token to the delegate and awaits it.
    /// </summary>
    [Test]
    public async Task TaskAdapter_PassesWorkAndToken()
    {
        string? receivedWork = null;
        CancellationToken receivedToken = default;
        var handler = new TaskDelegateWorkHandler<string>(async (work, ct) =>
        {
            await Task.Yield();
            receivedWork = work;
            receivedToken = ct;
        });
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync("item", cts.Token);

        await Assert.That(receivedWork).IsEqualTo("item");
        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that a fault from the task delegate reaches the caller.
    /// </summary>
    [Test]
    public async Task TaskAdapter_PropagatesFault()
    {
        var handler = new TaskDelegateWorkHandler<string>((work, ct) =>
            Task.FromException(new FormatException("bad")));

        await Assert.That(async () => await handler.HandleAsync("item", CancellationToken.None))
            .Throws<FormatException>();
    }

    /// <summary>
    /// Verifies that a null task from the delegate is reported as <see cref="InvalidOperationException"/>.
    /// </summary>
    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD114:Avoid returning a null Task", Justification = "The test checks how a null Task from the delegate is reported.")]
    public async Task TaskAdapter_NullTask_ThrowsInvalidOperationException()
    {
        var handler = new TaskDelegateWorkHandler<string>((work, ct) => null!);

        await Assert.That(async () => await handler.HandleAsync("item", CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that the task adapter rejects a null delegate.
    /// </summary>
    [Test]
    public async Task TaskAdapter_NullDelegate_ThrowsArgumentNullException()
    {
        await Assert.That(() => new TaskDelegateWorkHandler<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the scoped adapter passes its captured provider, the work item and the token.
    /// </summary>
    [Test]
    public async Task ScopedAdapter_PassesProviderWorkAndToken()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? receivedProvider = null;
        string? receivedWork = null;
        CancellationToken receivedToken = default;
        var handler = new ScopedDelegateWorkHandler<string>(provider, (sp, work, ct) =>
        {
            receivedProvider = sp;
            receivedWork = work;
            receivedToken = ct;
            return ValueTask.CompletedTask;
        });
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync("item", cts.Token);

        await Assert.That(receivedProvider).IsSameReferenceAs(provider);
        await Assert.That(receivedWork).IsEqualTo("item");
        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that the scoped adapter rejects null constructor arguments.
    /// </summary>
    [Test]
    public async Task ScopedAdapter_NullArguments_ThrowArgumentNullException()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        await Assert.That(() => new ScopedDelegateWorkHandler<string>(null!, (sp, w, ct) => ValueTask.CompletedTask))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new ScopedDelegateWorkHandler<string>(provider, null!))
            .Throws<ArgumentNullException>();
    }
}
