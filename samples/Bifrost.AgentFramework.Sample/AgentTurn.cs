namespace Bifrost.AgentFramework.Sample;

/// <summary>
/// The queued work item: a reference to one saved agent turn. The message text and the session
/// stay in the app's store; the queue carries only the IDs it needs to route and load the turn.
/// </summary>
/// <param name="TenantId">The tenant that owns the session.</param>
/// <param name="SessionId">The agent session the turn belongs to.</param>
/// <param name="TurnId">The saved turn request to run.</param>
internal sealed record AgentTurn(string TenantId, string SessionId, Guid TurnId);

/// <summary>Identifies one agent session within a tenant.</summary>
/// <param name="TenantId">The tenant that owns the session.</param>
/// <param name="SessionId">The session ID.</param>
internal readonly record struct SessionKey(string TenantId, string SessionId)
{
    /// <inheritdoc/>
    public override string ToString() => $"{TenantId}/{SessionId}";
}
