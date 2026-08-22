using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.GoogleHome;

namespace Serval.Server.Tests;

/// <summary>
/// The credential on the one route Google's cloud calls with nothing else to identify it.
///
/// <para>This is the closest thing in the codebase to <c>StreamTicketServiceTests</c>, and the two
/// differences from that service are what these tests are mostly about: a ticket here is good for
/// a few uses rather than exactly one, and it names a camera.</para>
/// </summary>
public class CameraStreamTicketServiceTests
{
    private static CameraStreamTicketService Service(
        int ttlSeconds = 120, TimeProvider? time = null)
    {
        var options = new ServerOptions();
        options.GoogleHome.SignalingTicketSeconds = ttlSeconds;
        return new CameraStreamTicketService(
            new StaticOptionsMonitor<ServerOptions>(options), time ?? TimeProvider.System);
    }

    [Fact]
    public void A_fresh_ticket_names_its_camera()
    {
        CameraStreamTicketService tickets = Service();

        string ticket = tickets.Mint("front-door");

        Assert.Equal("front-door", tickets.TrySpend(ticket));
    }

    /// <summary>
    /// <b>Not single-use, unlike the WebSocket ticket this otherwise copies.</b> Google retries a
    /// failed signaling POST, so consuming the ticket on first sight would turn one momentary
    /// go2rtc hiccup into a stream request that can never succeed — the retry would arrive with a
    /// credential that had already been spent.
    /// </summary>
    [Fact]
    public void A_ticket_survives_a_retry()
    {
        CameraStreamTicketService tickets = Service();
        string ticket = tickets.Mint("front-door");

        Assert.Equal("front-door", tickets.TrySpend(ticket));
        Assert.Equal("front-door", tickets.TrySpend(ticket));
    }

    /// <summary>
    /// But the budget is small and real, which is what caps a ticket found in a reverse-proxy log
    /// at a few views of one camera rather than unlimited views for its whole window.
    /// </summary>
    [Fact]
    public void A_ticket_runs_out()
    {
        CameraStreamTicketService tickets = Service();
        string ticket = tickets.Mint("front-door");

        Assert.Equal("front-door", tickets.TrySpend(ticket));
        Assert.Equal("front-door", tickets.TrySpend(ticket));
        Assert.Equal("front-door", tickets.TrySpend(ticket));

        Assert.Null(tickets.TrySpend(ticket));
    }

    /// <summary>
    /// A ticket stops working when its window closes — the bound that makes one found in a
    /// reverse-proxy log worth nothing an hour later.
    /// </summary>
    [Fact]
    public void An_expired_ticket_is_refused()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        CameraStreamTicketService tickets = Service(ttlSeconds: 120, clock);

        string ticket = tickets.Mint("front-door");

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal("front-door", tickets.TrySpend(ticket));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tickets.TrySpend(ticket));
    }

    /// <summary>
    /// The configured window is honoured rather than a hardcoded one, and it is clamped: a
    /// deployment asking for a day-long signaling ticket gets fifteen minutes, and one asking for
    /// zero gets ten seconds rather than a ticket that is dead on arrival.
    /// </summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(60, 60)]
    [InlineData(100_000, 900)]
    public void The_configured_window_is_clamped(int configured, int effective)
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        CameraStreamTicketService tickets = Service(configured, clock);

        string ticket = tickets.Mint("front-door");

        clock.Advance(TimeSpan.FromSeconds(effective - 1));
        Assert.Equal("front-door", tickets.TrySpend(ticket));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tickets.TrySpend(ticket));
    }

    /// <summary>Expired tickets are actually released rather than accumulating until a restart.</summary>
    [Fact]
    public void The_sweep_drops_expired_tickets()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        CameraStreamTicketService tickets = Service(ttlSeconds: 120, clock);

        tickets.Mint("front-door");
        tickets.Mint("garage");
        Assert.Equal(2, tickets.Count);

        clock.Advance(TimeSpan.FromSeconds(121));
        tickets.SweepExpired();

        Assert.Equal(0, tickets.Count);
    }

    [Fact]
    public void An_unknown_ticket_is_refused()
    {
        CameraStreamTicketService tickets = Service();
        tickets.Mint("front-door");

        Assert.Null(tickets.TrySpend("not-a-ticket"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_ticket_at_all_is_refused(string? ticket) =>
        Assert.Null(Service().TrySpend(ticket));

    /// <summary>
    /// Two cameras never share a ticket. The signaling URL carries no camera id, so the ticket is
    /// the only thing that names one — a collision here would stream the wrong room to a display.
    /// </summary>
    [Fact]
    public void A_ticket_is_bound_to_one_camera()
    {
        CameraStreamTicketService tickets = Service();

        string front = tickets.Mint("front-door");
        string garage = tickets.Mint("garage");

        Assert.NotEqual(front, garage);
        Assert.Equal("front-door", tickets.TrySpend(front));
        Assert.Equal("garage", tickets.TrySpend(garage));
    }

    /// <summary>
    /// <c>action: "end"</c> drops the ticket. There is nothing to tear down on go2rtc's side, but
    /// the credential should not outlive the session it was minted for.
    /// </summary>
    [Fact]
    public void Ending_a_session_revokes_its_ticket()
    {
        CameraStreamTicketService tickets = Service();
        string ticket = tickets.Mint("front-door");

        tickets.Revoke(ticket);

        Assert.Null(tickets.TrySpend(ticket));
    }

    [Fact]
    public void Tickets_are_url_safe_and_unguessable()
    {
        CameraStreamTicketService tickets = Service();

        string[] minted = [.. Enumerable.Range(0, 32).Select(_ => tickets.Mint("front-door"))];

        Assert.Equal(minted.Length, minted.Distinct(StringComparer.Ordinal).Count());

        foreach (string ticket in minted)
        {
            Assert.DoesNotContain('+', ticket);
            Assert.DoesNotContain('/', ticket);
            Assert.DoesNotContain('=', ticket);
            Assert.True(ticket.Length >= 42, ticket);
        }
    }

    /// <summary>
    /// The sweep only drops what has expired, so a display that is slow to connect does not lose
    /// its ticket to housekeeping.
    /// </summary>
    [Fact]
    public void The_sweep_leaves_live_tickets_alone()
    {
        CameraStreamTicketService tickets = Service();
        string ticket = tickets.Mint("front-door");

        tickets.SweepExpired();

        Assert.Equal("front-door", tickets.TrySpend(ticket));
    }

    /// <summary>
    /// A playback ticket has no budget: a Cast receiver pulls a playlist and every segment behind
    /// it for as long as somebody watches, and any number would only decide how many minutes in
    /// the picture stops.
    /// </summary>
    [Fact]
    public void A_playback_ticket_does_not_run_out()
    {
        CameraStreamTicketService tickets = Service();

        string ticket = tickets.MintForPlayback("front-door");

        for (int i = 0; i < 500; i++)
        {
            Assert.Equal("front-door", tickets.TrySpend(ticket));
        }
    }

    /// <summary>
    /// <b>And reading one must not take it out of the table, even briefly.</b> The budgeted path
    /// removes the entry and puts it back with one fewer use, which is atomic against counting but
    /// leaves a window in which the ticket does not exist. A player fetching the playlist and a
    /// segment at the same instant would have one land in that window and be refused — a stream
    /// that stutters with nothing to show for it in any log.
    /// </summary>
    [Fact]
    public async Task Concurrent_playback_reads_all_succeed()
    {
        CameraStreamTicketService tickets = Service();

        string ticket = tickets.MintForPlayback("front-door");

        string?[] results = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => Task.Run(() => tickets.TrySpend(ticket))));

        Assert.All(results, r => Assert.Equal("front-door", r));
    }

    /// <summary>It still expires — an unbudgeted ticket is not an unbounded one.</summary>
    [Fact]
    public void A_playback_ticket_expires()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        CameraStreamTicketService tickets = Service(time: clock);

        string ticket = tickets.MintForPlayback("front-door");

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.Null(tickets.TrySpend(ticket));
    }

    /// <summary>And it still names one camera: a ticket is not a key to the camera list.</summary>
    [Fact]
    public void A_playback_ticket_names_only_its_own_camera()
    {
        CameraStreamTicketService tickets = Service();

        Assert.Equal("front-door", tickets.TrySpend(tickets.MintForPlayback("front-door")));
        Assert.NotEqual("garage", tickets.TrySpend(tickets.MintForPlayback("front-door")));
    }
}

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> over a value that never changes, so a service that
/// reads configuration at call time can be tested without a host.
/// </summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when told to, so a window measured in
/// minutes can be tested in microseconds.
///
/// <para>Hand-written rather than pulled in from <c>Microsoft.Extensions.TimeProvider.Testing</c>:
/// the only member anything here calls is <see cref="GetUtcNow"/>, and a package reference to get
/// one overridden method would be the larger change.</para>
/// </summary>
internal sealed class AdvanceableClock : TimeProvider
{
    private DateTimeOffset _now;

    public AdvanceableClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
