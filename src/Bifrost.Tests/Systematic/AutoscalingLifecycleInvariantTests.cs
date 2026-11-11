using Bifrost.Autoscaling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Bifrost.Tests.Systematic;

internal class AutoscalingLifecycleInvariantTests
{
    [Test]
    public async Task StopDoesNotCompleteBeforeAnEnteredCallbackFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(1);
        coordinator.GetUtilizationRatio().Returns(1.0);
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        });
        using var engine = new AutoscalingEngine(Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 4,
            CheckInterval = TimeSpan.FromMilliseconds(20),
            CooldownPeriod = TimeSpan.FromHours(1),
        }), coordinator, NullLogger<AutoscalingEngine>.Instance);
        engine.ScalingDecisionMade += (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        await engine.StartAsync().ConfigureAwait(false);
        bool callbackEntered = entered.Wait(TimeSpan.FromSeconds(5));
        bool completedBeforeRelease;
        Task stop;
        try
        {
            stop = engine.StopAsync();
            completedBeforeRelease = stop.IsCompleted;
        }
        finally
        {
            release.Set();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(callbackEntered).IsTrue();
        await Assert.That(completedBeforeRelease).IsFalse();
    }
}
