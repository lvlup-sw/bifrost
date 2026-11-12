#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007, VSTHRD002, VSTHRD003

using System.Reflection;
using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryIdleBarrierTests
{
    [Test]
    public async Task RequestDuringParkMustWaitForNextTick()
    {
        var clock = new ParkClock();
        var store = new InMemoryScheduleStore();
        using var registry = new ScheduleRegistry(store, clock);
        var sink = new SystematicRegistryRestartTests.Sink();
        using var loop = new ScheduleTickLoop(registry, store, clock, new JobDispatcherRouter(sink), sink, NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());
        Task? lateIdle = null;
        Task? lateRequest = null;
        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None);
            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            clock.OnTimer = () =>
            {
                clock.Inner.Advance(TimeSpan.FromMinutes(1));
                lateIdle = loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
                var field = typeof(ScheduleTickLoop).GetField("pendingBarriers", BindingFlags.Instance | BindingFlags.NonPublic)!;
                lateRequest = ((List<TaskCompletionSource>)field.GetValue(loop)!).Last().Task;
                clock.BlockNextRead = true;
            };
            var priorIdle = loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            await clock.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(lateRequest is not null).IsTrue();
            await Assert.That(lateRequest!.IsCompleted).IsFalse();
            clock.Release.Set();
            await priorIdle;
            await lateIdle!;
        }
        finally
        {
            clock.Release.Set();
            await ((IHostedService)loop).StopAsync(CancellationToken.None);
            clock.Release.Dispose();
        }
    }

    private sealed class ParkClock : TimeProvider
    {
        public FakeTimeProvider Inner { get; } = new(SystematicRegistryTests.Epoch);
        public Action? OnTimer { get; set; }
        public bool BlockNextRead { get; set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow()
        {
            if (BlockNextRead)
            {
                BlockNextRead = false;
                Blocked.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Park gate timed out.");
            }

            return Inner.GetUtcNow();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Inner.CreateTimer(callback, state, dueTime, period);
            var action = OnTimer;
            OnTimer = null;
            action?.Invoke();
            return timer;
        }
    }
}
