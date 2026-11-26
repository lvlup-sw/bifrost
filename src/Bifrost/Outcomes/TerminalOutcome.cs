// =============================================================================
// <copyright file="TerminalOutcome.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Outcomes;

/// <summary>The one final result of an accepted work item.</summary>
/// <remarks>
/// Every accepted item ends with exactly one of these values. The value describes the item, not
/// one handler attempt: an item that fails twice and then succeeds on a dead-letter retry is
/// <see cref="Succeeded"/>, and its attempts are counted separately.
/// </remarks>
internal enum TerminalOutcome
{
    /// <summary>The last handler attempt returned normally.</summary>
    Succeeded,

    /// <summary>
    /// The last handler attempt threw. An <see cref="OperationCanceledException"/> that the handler
    /// throws while its own token is not cancelled (for example its own timeout) is a failure too.
    /// </summary>
    Failed,

    /// <summary>
    /// A handler attempt threw <see cref="OperationCanceledException"/> after the handler token was
    /// cancelled, for example by <c>AbortAsync</c>. A canceled item is never retried or captured.
    /// </summary>
    Canceled,

    /// <summary>The item was accepted but no worker will ever start it.</summary>
    Abandoned,
}
