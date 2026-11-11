using Bifrost.Autoscaling;
using Bifrost.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Bifrost.Tests.Systematic;

internal class AutoscalingClockInvariantTests
{
    [Test]
    public async Task CooldownAllowsExactlyTheBoundaryAndLater()
    {
        foreach (long elapsed in new long[] { 0, 99, 100, 101 })
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var coordinator = Substitute.For<IAutoscalingCoordinator>();
            coordinator.ActiveWorkerCount.Returns(1);
            using var engine = new AutoscalingEngine(Options.Create(new AutoscalingOptions
            {
                MinWorkers = 1,
                MaxWorkers = 4,
                CooldownPeriod = TimeSpan.FromTicks(100),
            }), coordinator, NullLogger<AutoscalingEngine>.Instance, clock);
            await Assert.That(engine.EvaluateScaling(1, 1.0, 0).Action).IsEqualTo(ScalingAction.ScaleUp);
            clock.Advance(TimeSpan.FromTicks(elapsed));
            var expected = elapsed < 100 ? ScalingAction.None : ScalingAction.ScaleUp;
            await Assert.That(engine.EvaluateScaling(1, 1.0, 0).Action).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task DisposeClearsRunningStateAndPreventsRestart()
    {
        var clock = new FakeTimeProvider();
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(1);
        using var engine = new AutoscalingEngine(Options.Create(new AutoscalingOptions()), coordinator, NullLogger<AutoscalingEngine>.Instance, clock);
        await engine.StartAsync().ConfigureAwait(false);
        await Assert.That(engine.IsRunning).IsTrue();
        engine.Dispose();
        await Assert.That(engine.IsRunning).IsFalse();
        await Assert.That(() => engine.StartAsync()).Throws<ObjectDisposedException>();
        await engine.StopAsync().ConfigureAwait(false);
        engine.Dispose();
    }

    [Test]
    public async Task TimerRecoversAfterFailedActionAndStopsAndDisposes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(1);
        coordinator.GetUtilizationRatio().Returns(1.0);
        int calls = 0;
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return calls == 1 ? Task.FromException(new InvalidOperationException("synthetic action failure")) : Task.CompletedTask;
        });
        using var engine = new AutoscalingEngine(Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 4,
            CheckInterval = TimeSpan.FromSeconds(1),
            CooldownPeriod = TimeSpan.Zero,
        }), coordinator, NullLogger<AutoscalingEngine>.Instance, clock);
        await engine.StartAsync().ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(calls).IsGreaterThanOrEqualTo(2);
        await engine.StopAsync().ConfigureAwait(false);
        int stopped = calls;
        clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.That(calls).IsEqualTo(stopped);
        await engine.StartAsync().ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(calls).IsGreaterThan(stopped);
        engine.Dispose();
        int disposed = calls;
        clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.That(calls).IsEqualTo(disposed);
    }
}
