// =============================================================================
// <copyright file="ProcessRecoveryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.TickEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>Real process termination against a controlled, single-record file store.</summary>
public class ProcessRecoveryTests
{
    private const string DirectoryVariable = "BIFROST_PROCESS_PROBE_DIRECTORY";
    private const string CutVariable = "BIFROST_PROCESS_PROBE_CUT";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Recovery respects the last persisted checkpoint and permits replay before it.</summary>
    [Test]
    [Arguments("before-dispatch", 1)]
    [Arguments("after-dispatch", 2)]
    [Arguments("before-checkpoint", 2)]
    [Arguments("after-checkpoint", 1)]
    public async Task KillAndRestartPreservesOccurrence(string cut, int expectedAttempts)
    {
        var directory = Directory.CreateTempSubdirectory("bifrost-process-recovery-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var store = new FileStore(directory, "none");
            await store.SaveAsync(NewRecord(Epoch), deadline.Token).ConfigureAwait(false);
            using (var child = StartChild(directory, cut))
            {
                var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = child.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    while (!File.Exists(Path.Combine(directory, "reached")))
                    {
                        if (child.HasExited) throw new InvalidOperationException(await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
                        await Task.Delay(20, deadline.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            var saved = (await store.LoadAllAsync(deadline.Token).ConfigureAwait(false)).Single();
            await Assert.That(saved.LastFiredAt).IsEqualTo(cut == "after-checkpoint" ? Epoch.AddMinutes(1) : Epoch);
            using var recovery = StartChild(directory, "none");
            var recoveryOutput = recovery.StandardOutput.ReadToEndAsync(deadline.Token);
            var recoveryError = recovery.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await recovery.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (recovery.ExitCode != 0) throw new InvalidOperationException(await recoveryOutput.ConfigureAwait(false) + await recoveryError.ConfigureAwait(false));
            }
            finally
            {
                if (!recovery.HasExited) recovery.Kill(entireProcessTree: true);
                await recovery.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var attempts = await File.ReadAllLinesAsync(Path.Combine(directory, "attempts"), deadline.Token).ConfigureAwait(false);
            await Assert.That(attempts.Length).IsEqualTo(expectedAttempts);
            await Assert.That(attempts.Distinct().Single()).IsEqualTo(Epoch.AddMinutes(1).UtcTicks.ToString(CultureInfo.InvariantCulture));
            await Assert.That((await store.LoadAllAsync(deadline.Token).ConfigureAwait(false)).Single().LastFiredAt).IsEqualTo(Epoch.AddMinutes(1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Child entry point selected explicitly by the parent process.</summary>
    [Test]
    public async Task ProcessProbeChild()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null) return;
        var cut = Environment.GetEnvironmentVariable(CutVariable)!;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleStore>(new FileStore(directory, cut));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Epoch.AddMinutes(1).AddSeconds(10)));
        // Synchronous fixture dispatch pins the before/after effect boundary. Production
        // dispatch is asynchronous; these tests do not assert exactly-once application effects.
        services.AddSingleton<IJobDispatcherRouter>(new BoundaryRouter(directory, cut));
        services.AddScheduler(builder => builder.AddInlineJob("job").Every(TimeSpan.FromMinutes(1))
            .WithMissedFirePolicy(MissedFirePolicy.FireAllMissed).Run((context, _) =>
            {
                File.AppendAllLines(Path.Combine(directory, "attempts"), [context.FireTime.UtcTicks.ToString(CultureInfo.InvariantCulture)]);
                return ValueTask.CompletedTask;
            }));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<ScheduleTickLoop>().WaitForIdleAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        foreach (var service in hosted.Reverse()) await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static Process StartChild(string directory, string cut)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(ProcessRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add("/*/*/ProcessRecoveryTests/ProcessProbeChild");
        start.Environment[DirectoryVariable] = directory;
        start.Environment[CutVariable] = cut;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start recovery probe.");
    }

    private static JobRecord NewRecord(DateTimeOffset last)
        => new("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed, JobState.Running,
            last, last.AddMinutes(1), "inline", null, new Dictionary<string, string>());

    private static void ReachCut(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "reached"), "ready");
        // This is the externally observed kill boundary, never released in the child.
        using var parked = new ManualResetEventSlim();
        parked.Wait();
    }

    private sealed class BoundaryRouter(string directory, string cut) : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            if (cut == "before-dispatch") ReachCut(directory);
#pragma warning disable VSTHRD002 // Deliberately pin the fixture effect before checkpoint handoff.
            dispatcher.DispatchAsync(context, ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            if (cut == "after-dispatch") ReachCut(directory);
        }
    }

    // Minimal single-job fixture, not a production store. Flush file contents before
    // atomic rename; this tests process death, not power failure or directory durability.
    private sealed class FileStore(string directory, string cut) : IScheduleStore
    {
        private string RecordPath => Path.Combine(directory, "record");

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var ticks = long.Parse(File.ReadAllText(RecordPath), CultureInfo.InvariantCulture);
            return ValueTask.FromResult<IReadOnlyList<JobRecord>>([NewRecord(new DateTimeOffset(ticks, TimeSpan.Zero))]);
        }

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var temporary = RecordPath + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                using var writer = new StreamWriter(file, leaveOpen: true);
#pragma warning disable VSTHRD103 // The crash fixture requires a synchronous durable flush before its kill boundary.
                writer.Write((job.LastFiredAt ?? Epoch).UtcTicks.ToString(CultureInfo.InvariantCulture));
                writer.Flush();
                file.Flush(flushToDisk: true);
#pragma warning restore VSTHRD103
            }

            File.Move(temporary, RecordPath, overwrite: true);
            return ValueTask.CompletedTask;
        }

        public async ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
        {
            if (cut == "before-checkpoint") ReachCut(directory);
            await SaveAsync(NewRecord(firedAt), ct).ConfigureAwait(false);
            if (cut == "after-checkpoint") ReachCut(directory);
        }

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => throw new NotSupportedException("The probe never deletes jobs.");
    }
}
