// =============================================================================
// <copyright file="ScheduledDispatchOutcome.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// Carries one fire's admission result from a dispatcher back to the tick loop
/// without changing <see cref="IJobDispatcher"/> or treating rejection as an exception.
/// The loop sets the ambient state for the duration of a single <c>DispatchAsync</c>.
/// </summary>
internal static class ScheduledDispatchOutcome
{
    private static readonly AsyncLocal<State?> Ambient = new();

    /// <summary>
    /// Starts tracking the fire on the current async flow.
    /// </summary>
    /// <param name="observation">The outcome the caller reads after the dispatcher returns.</param>
    /// <returns>A disposable that restores the previous ambient state.</returns>
    public static IDisposable Begin(out Observation observation)
    {
        var state = new State();
        var prior = Ambient.Value;
        Ambient.Value = state;
        observation = new Observation(state);
        return new Restore(prior);
    }

    /// <summary>
    /// Marks the current fire as an admission rejection. No effect when the tick
    /// loop is not tracking the fire.
    /// </summary>
    /// <param name="reason">The rejection reason text, when the orchestrator supplied one.</param>
    public static void Reject(string? reason)
    {
        var state = Ambient.Value;
        if (state is null)
        {
            return;
        }

        state.Rejected = true;
        state.Reason = reason;
    }

    /// <summary>The admission result of the fire that <see cref="Begin"/> started.</summary>
    internal readonly struct Observation
    {
        private readonly State state;

        internal Observation(State state) => this.state = state;

        /// <summary>Gets a value indicating whether admission was rejected.</summary>
        public bool Rejected => this.state is not null && this.state.Rejected;

        /// <summary>Gets the rejection reason text, when one was supplied.</summary>
        public string? Reason => this.state?.Reason;
    }

    internal sealed class State
    {
        public bool Rejected { get; set; }

        public string? Reason { get; set; }
    }

    private sealed class Restore(State? prior) : IDisposable
    {
        public void Dispose() => Ambient.Value = prior;
    }
}
