// =============================================================================
// <copyright file="AutoscalingEngineHostedServiceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="AutoscalingEngineHostedService{TWork}"/> implementation.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingEngineHostedServiceTests
{
    /// <summary>
    /// Verifies that the hosted service implements IHostedService.
    /// </summary>
    [Test]
    public async Task AutoscalingEngineHostedService_ImplementsIHostedService()
    {
        var type = typeof(AutoscalingEngineHostedService<string>);

        await Assert.That(typeof(IHostedService).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that StartAsync calls engine StartAsync.
    /// </summary>
    [Test]
    public async Task StartAsync_CallsEngineStartAsync()
    {
        var engine = Substitute.For<IAutoscalingEngine>();
        var hostedService = new AutoscalingEngineHostedService<string>(new ServiceCollection().AddKeyedSingleton<IAutoscalingEngine>(typeof(string), engine).BuildServiceProvider());

        await hostedService.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await engine.Received(1).StartAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that StopAsync calls engine StopAsync.
    /// </summary>
    [Test]
    public async Task StopAsync_CallsEngineStopAsync()
    {
        var engine = Substitute.For<IAutoscalingEngine>();
        var hostedService = new AutoscalingEngineHostedService<string>(new ServiceCollection().AddKeyedSingleton<IAutoscalingEngine>(typeof(string), engine).BuildServiceProvider());

        await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false);

        await engine.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that cancellation token is passed to engine.
    /// </summary>
    [Test]
    public async Task StartAsync_PassesCancellationToken()
    {
        var engine = Substitute.For<IAutoscalingEngine>();
        var hostedService = new AutoscalingEngineHostedService<string>(new ServiceCollection().AddKeyedSingleton<IAutoscalingEngine>(typeof(string), engine).BuildServiceProvider());
        using var cts = new CancellationTokenSource();

        await hostedService.StartAsync(cts.Token).ConfigureAwait(false);

        await engine.Received(1).StartAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the hosted service is sealed.
    /// </summary>
    [Test]
    public async Task AutoscalingEngineHostedService_IsSealed()
    {
        var type = typeof(AutoscalingEngineHostedService<string>);

        await Assert.That(type.IsSealed).IsTrue();
    }

    /// <summary>
    /// Verifies that constructor throws for null engine.
    /// </summary>
    [Test]
    public async Task Constructor_NullEngine_ThrowsArgumentNullException()
    {
        await Assert.That(() => new AutoscalingEngineHostedService<string>(null!))
            .Throws<ArgumentNullException>();
    }
}
