using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.AgentFramework.Sample;

/// <summary>
/// A scoped unit of work for one turn attempt. It releases a session that the attempt checked
/// out but did not save, for example after a timeout, when Bifrost disposes the attempt's scope.
/// </summary>
/// <param name="store">The app store.</param>
internal sealed class SessionRepository(SessionStore store) : IDisposable
{
    private SessionKey? _checkedOut;

    /// <summary>Loads the saved turn request.</summary>
    /// <param name="turn">The queued turn.</param>
    /// <returns>The turn record.</returns>
    public TurnRecord LoadRequest(AgentTurn turn) => store.LoadRequest(turn.TurnId);

    /// <summary>Checks out the turn's session.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The serialized agent session, or <see langword="null"/> for a new session.</returns>
    public System.Text.Json.JsonElement? CheckOut(SessionKey session)
    {
        var state = store.CheckOut(session);
        _checkedOut = session;
        return state;
    }

    /// <summary>Saves the session update and the reply.</summary>
    /// <param name="turn">The queued turn.</param>
    /// <param name="state">The serialized agent session after the turn.</param>
    /// <param name="reply">The agent reply.</param>
    public void SaveResult(AgentTurn turn, System.Text.Json.JsonElement state, string reply)
    {
        store.SaveResult(turn.TurnId, state, reply);
        _checkedOut = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_checkedOut is { } session)
        {
            store.Abandon(session);
        }
    }
}

/// <summary>The Bifrost handler that runs one background agent turn.</summary>
internal static class TurnHandler
{
    /// <summary>
    /// Loads the turn request and the session, awaits <see cref="AIAgent"/>.RunAsync with the
    /// handler's cancellation token, and saves the session update and the reply before it returns.
    /// </summary>
    /// <param name="services">The attempt's scoped service provider.</param>
    /// <param name="turn">The queued turn.</param>
    /// <param name="ct">
    /// The handler token. It is cancelled by the <c>WithHandlerResilience</c> attempt timeout, by
    /// host shutdown, and by <c>AbortAsync</c>, and is passed to every awaited call.
    /// </param>
    /// <returns>A task that completes when the turn is saved.</returns>
    /// <remarks>
    /// Bifrost holds the session's key slot until this method returns and the scope is disposed,
    /// so the next turn of the same session always loads the state this turn saved.
    /// </remarks>
    public static async ValueTask HandleAsync(IServiceProvider services, AgentTurn turn, CancellationToken ct)
    {
        var repository = services.GetRequiredService<SessionRepository>();
        var agent = services.GetRequiredService<AIAgent>();

        var request = repository.LoadRequest(turn);
        var saved = repository.CheckOut(request.Session);
        var session = saved is { } state
            ? await agent.DeserializeSessionAsync(state, cancellationToken: ct)
            : await agent.CreateSessionAsync(ct);

        var response = await agent.RunAsync(request.Message, session, cancellationToken: ct);

        var updated = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        repository.SaveResult(turn, updated, response.Text);
    }
}
