using System.Net.WebSockets;
using Serval.Server.Auth;

namespace Serval.Server.Live;

/// <summary>
/// The shape every ticketed WebSocket route shares: refuse a plain HTTP request, consume the
/// single-use ticket (a browser WebSocket cannot carry an Authorization header, so these routes
/// check the ticket from <c>POST /api/auth/ws-ticket</c> instead of the JWT bearer every other
/// route uses — see <see cref="StreamTicketService"/>), accept the upgrade, and treat the client
/// vanishing as a hang-up rather than an error.
/// </summary>
public static class TicketedWebSocket
{
    /// <param name="gate">Route-specific checks that must reject <em>before</em> the upgrade —
    /// a socket that accepts and then never sends cannot tell the client why.</param>
    public static async Task<IResult> AcceptAsync(
        HttpContext context,
        StreamTicketService tickets,
        Func<WebSocket, CancellationToken, Task> session,
        Func<Task<IResult?>>? gate = null)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest("Expected a WebSocket request.");
        }

        if (tickets.TryConsume(context.Request.Query["ticket"]) is null)
        {
            return Results.Unauthorized();
        }

        if (gate is not null && await gate() is { } refusal)
        {
            return refusal;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        try
        {
            await session(socket, context.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { /* client vanished; nothing to do */ }

        return Results.Empty;
    }

    /// <summary>A WebSocket route renders in the API document as a plain GET that always fails,
    /// and its auth is the ticket check inside <see cref="AcceptAsync"/> rather than the JWT
    /// pipeline — a WebSocket upgrade cannot carry the header that pipeline expects.</summary>
    public static RouteHandlerBuilder AsTicketedWebSocket(this RouteHandlerBuilder route) =>
        route.ExcludeFromDescription().AllowAnonymous();
}
