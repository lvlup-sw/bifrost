using System.Numerics;

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests.Systematic;

internal class PriorityScalingInvariantTests
{
    [Test]
    public async Task PriorityUnitsPreserveExactRepresentableOrdering()
    {
        foreach (long frequency in new long[] { 4, 1000, 10_000_000, 1_000_000_000 })
        {
            foreach (int quarterSeconds in new[] { 0, 1, 3, 4, 119, 240 })
            {
                var options = new PriorityDispatchOptions
                {
                    InteractiveBoostWindow = TimeSpan.FromTicks(quarterSeconds * (TimeSpan.TicksPerSecond / 4)),
                    BatchPenaltyWindow = TimeSpan.FromTicks(quarterSeconds * (TimeSpan.TicksPerSecond / 4)),
                };
                var boosts = PriorityKey.Precompute(options, frequency);
                long expectedBoost = (long)((BigInteger)options.InteractiveBoostWindow.Ticks * frequency / TimeSpan.TicksPerSecond);
                foreach (var workClass in new[] { WorkClass.Interactive, WorkClass.Default, WorkClass.Batch })
                {
                    foreach (long seconds in new long[] { 0, 1, 100, 1000 })
                    {
                        long timestamp = checked(seconds * frequency);
                        long signedBoost = workClass == WorkClass.Interactive ? expectedBoost : workClass == WorkClass.Batch ? -expectedBoost : 0;
                        var envelope = new WorkEnvelope<string>("synthetic", workClass, timestamp);
                        await Assert.That(PriorityKey.Compute(in envelope, in boosts)).IsEqualTo(timestamp - signedBoost);
                        var shifted = new WorkEnvelope<string>("renamed", workClass, timestamp + frequency * 7);
                        await Assert.That(PriorityKey.Compute(in shifted, in boosts) - PriorityKey.Compute(in envelope, in boosts)).IsEqualTo(frequency * 7);
                    }
                }
            }
        }
    }

    [Test]
    public async Task AdmissionThresholdsMatchFloorAndUrgencyOrder()
    {
        foreach (int capacity in new[] { 1, 2, 3, 7, 8, 9, 31, 32, 33 })
        {
            foreach (double batch in new[] { 0.125, 0.25, 0.5, 0.75, 1.0 })
            {
                foreach (double normal in new[] { batch, 1.0 })
                {
                    var options = new PriorityDispatchOptions
                    {
                        BatchAdmissionWatermark = batch,
                        DefaultAdmissionWatermark = normal,
                        InteractiveAdmissionWatermark = 1.0,
                    };
                    var thresholds = AdmissionThresholds.Precompute(options, capacity);
                    foreach (var workClass in new[] { WorkClass.Batch, WorkClass.Default, WorkClass.Interactive })
                    {
                        double fraction = workClass == WorkClass.Batch ? batch : workClass == WorkClass.Default ? normal : 1.0;
                        long expected = (long)Math.Floor((decimal)fraction * capacity);
                        await Assert.That(thresholds.ThresholdFor(workClass)).IsEqualTo(expected);
                    }

                    for (int occupancy = 0; occupancy <= capacity + 1; occupancy++)
                    {
                        bool batchAdmits = occupancy < thresholds.ThresholdFor(WorkClass.Batch);
                        bool defaultAdmits = occupancy < thresholds.ThresholdFor(WorkClass.Default);
                        bool interactiveAdmits = occupancy < thresholds.ThresholdFor(WorkClass.Interactive);
                        await Assert.That(!batchAdmits || defaultAdmits).IsTrue();
                        await Assert.That(!defaultAdmits || interactiveAdmits).IsTrue();
                    }
                }
            }
        }
    }

    [Test]
    public async Task ScalingDecisionsMatchBoundaryTable()
    {
        foreach (int initial in new[] { 1, 3, 5 })
        {
            foreach (int step in new[] { 1, 3, 10 })
            {
                foreach (int workers in Enumerable.Range(initial, 13 - initial))
                {
                    foreach (double utilization in new[] { 0.0, Math.BitDecrement(0.25), 0.25, Math.BitIncrement(0.25), 0.5, Math.BitDecrement(0.75), 0.75, Math.BitIncrement(0.75), 1.0 })
                    {
                        var coordinator = Substitute.For<IAutoscalingCoordinator>();
                        coordinator.ActiveWorkerCount.Returns(initial);
                        using var engine = new AutoscalingEngine(Options.Create(new AutoscalingOptions
                        {
                            MinWorkers = 2,
                            MaxWorkers = 12,
                            LowWatermark = 0.25,
                            HighWatermark = 0.75,
                            ScaleUpStep = step,
                            ScaleDownStep = step,
                            CooldownPeriod = TimeSpan.Zero,
                        }), coordinator, NullLogger<AutoscalingEngine>.Instance);
                        int minimum = Math.Max(2, initial);
                        int movement = utilization > 0.75 ? Math.Min(step, 12 - workers)
                            : utilization < 0.25 ? -Math.Min(step, Math.Max(0, workers - minimum)) : 0;
                        var expectedAction = movement > 0 ? ScalingAction.ScaleUp : movement < 0 ? ScalingAction.ScaleDown : ScalingAction.None;
                        var actual = engine.EvaluateScaling(workers, utilization, 0);
                        await Assert.That(actual.Action).IsEqualTo(expectedAction);
                        await Assert.That(actual.TargetWorkers).IsEqualTo(workers + movement);
                    }
                }
            }
        }
    }
}