using Serval.Server.Auth;

namespace Serval.Server.Tests;

/// <summary>
/// The WebSocket connect ticket's only security property is single use — a ticket that could be
/// replayed would defeat the reason it exists instead of a normal Bearer header. These pin that.
/// </summary>
public class StreamTicketServiceTests
{
    [Fact]
    public void A_minted_ticket_is_consumed_once()
    {
        var tickets = new StreamTicketService();
        string ticket = tickets.Mint("alice", Role.Admin);

        (string UserId, Role Role)? first = tickets.TryConsume(ticket);
        (string UserId, Role Role)? second = tickets.TryConsume(ticket);

        Assert.Equal(("alice", Role.Admin), first);
        Assert.Null(second);
    }

    [Fact]
    public void An_unknown_ticket_is_rejected()
    {
        var tickets = new StreamTicketService();
        Assert.Null(tickets.TryConsume("never-minted"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_ticket_is_rejected(string? ticket)
    {
        var tickets = new StreamTicketService();
        Assert.Null(tickets.TryConsume(ticket));
    }
}
