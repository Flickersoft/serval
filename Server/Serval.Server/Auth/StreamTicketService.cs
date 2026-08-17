using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Serval.Server.Auth;

/// <summary>
/// Single-use, short-lived tickets for the three WebSocket endpoints (dashboard, live events,
/// audio levels — see their handlers under <c>Dashboard/</c> and <c>Live/</c>) that a browser
/// cannot attach an <c>Authorization</c> header to. A client with a valid access token exchanges
/// it here for a ticket, then passes the ticket as <c>?ticket=</c> on the socket URL; the handler
/// consumes it atomically on the way in, so a ticket that leaks into a log or browser history is
/// already useless by the time anyone could reuse it.
///
/// In-memory rather than Mongo: nothing in this codebase supports running more than one Server
/// instance, so there is no cross-instance ticket store to build. A future move to multiple
/// instances would need this to move to something shared (e.g. Redis) alongside a load balancer
/// capable of sticky WebSocket routing.
/// </summary>
public sealed class StreamTicketService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new();

    public string Mint(string userId, Role role)
    {
        string ticket = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = new Entry(userId, role, DateTimeOffset.UtcNow + Ttl);
        return ticket;
    }

    /// <summary>
    /// Consumes the ticket if present and unexpired. Removal is atomic (<see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey,out TValue)"/>),
    /// which is what makes the ticket single-use — there is no separate "mark used" step to race.
    /// </summary>
    public (string UserId, Role Role)? TryConsume(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out Entry entry))
        {
            return null;
        }

        return entry.ExpiresAt > DateTimeOffset.UtcNow ? (entry.UserId, entry.Role) : null;
    }

    /// <summary>Drops expired-but-never-consumed tickets so a client that mints and never
    /// connects doesn't leak memory. Called on an interval by <see cref="StreamTicketSweepWorker"/>.</summary>
    public void SweepExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string key, Entry entry) in _tickets)
        {
            if (entry.ExpiresAt <= now)
            {
                _tickets.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct Entry(string UserId, Role Role, DateTimeOffset ExpiresAt);
}

/// <summary>Periodic cleanup for <see cref="StreamTicketService"/> — the tickets are already
/// individually capped at 30 seconds, so this only needs to run occasionally.</summary>
public sealed class StreamTicketSweepWorker(StreamTicketService tickets) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            tickets.SweepExpired();
        }
        while (await TaskWaits.SafeWaitAsync(timer, stoppingToken));
    }
}
