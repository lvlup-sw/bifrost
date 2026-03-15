// =============================================================================
// <copyright file="ScopedHandlerProxyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Handlers;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Handlers;

/// <summary>
/// Tests for <see cref="ScopedHandlerProxy{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class ScopedHandlerProxyTests
{
    private IServiceScopeFactory _scopeFactory = null!;
    private IServiceScope _scope = null!;
    private IServiceProvider _scopedServiceProvider = null!;
    private IWorkHandler<string> _innerHandler = null!;

    /// <summary>
    /// Sets up test dependencies for each test.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope, IAsyncDisposable>();
        _scopedServiceProvider = Substitute.For<IServiceProvider>();
        _innerHandler = Substitute.For<IWorkHandler<string>>();

        _scope.ServiceProvider.Returns(_scopedServiceProvider);
        _scopedServiceProvider.GetService(typeof(IWorkHandler<string>)).Returns(_innerHandler);
        _scopeFactory.CreateScope().Returns(_scope);
        _innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that HandleAsync creates a scope via the scope factory.
    /// </summary>
    [Test]
    public async Task HandleAsync_CreatesScope()
    {
        // Arrange
        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act
        await proxy.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        _scopeFactory.Received(1).CreateScope();
    }

    /// <summary>
    /// Verifies that HandleAsync resolves IWorkHandler from the scope's service provider.
    /// </summary>
    [Test]
    public async Task HandleAsync_ResolvesHandlerFromScope()
    {
        // Arrange
        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act
        await proxy.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        _scopedServiceProvider.Received(1).GetService(typeof(IWorkHandler<string>));
    }

    /// <summary>
    /// Verifies that the scope is disposed after the handler completes.
    /// </summary>
    [Test]
    public async Task HandleAsync_DisposesScope()
    {
        // Arrange
        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act
        await proxy.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        await ((IAsyncDisposable)_scope).Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the scope is disposed even when the handler throws an exception.
    /// </summary>
    [Test]
    public async Task HandleAsync_DisposesScope_OnException()
    {
        // Arrange
        _innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("handler error")));
        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act & Assert
        await Assert.That(() => proxy.HandleAsync("work", CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();

        await ((IAsyncDisposable)_scope).Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that each call to HandleAsync creates a fresh scope.
    /// </summary>
    [Test]
    public async Task HandleAsync_FreshScopePerCall()
    {
        // Arrange
        var scope1 = Substitute.For<IServiceScope, IAsyncDisposable>();
        var scope2 = Substitute.For<IServiceScope, IAsyncDisposable>();
        var sp1 = Substitute.For<IServiceProvider>();
        var sp2 = Substitute.For<IServiceProvider>();

        scope1.ServiceProvider.Returns(sp1);
        scope2.ServiceProvider.Returns(sp2);
        sp1.GetService(typeof(IWorkHandler<string>)).Returns(_innerHandler);
        sp2.GetService(typeof(IWorkHandler<string>)).Returns(_innerHandler);

        _scopeFactory.CreateScope().Returns(scope1, scope2);

        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act
        await proxy.HandleAsync("work1", CancellationToken.None).ConfigureAwait(false);
        await proxy.HandleAsync("work2", CancellationToken.None).ConfigureAwait(false);

        // Assert
        _scopeFactory.Received(2).CreateScope();
    }

    /// <summary>
    /// Verifies that work and cancellation token are forwarded to the resolved handler.
    /// </summary>
    [Test]
    public async Task HandleAsync_PassesWorkAndCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var proxy = new ScopedHandlerProxy<string>(_scopeFactory);

        // Act
        await proxy.HandleAsync("my-work", cts.Token).ConfigureAwait(false);

        // Assert
        await _innerHandler.Received(1).HandleAsync("my-work", cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when scope factory is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenScopeFactoryNull()
    {
        // Act & Assert
        await Assert.That(() => new ScopedHandlerProxy<string>(null!))
            .Throws<ArgumentNullException>();
    }
}
