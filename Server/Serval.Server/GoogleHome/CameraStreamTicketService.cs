using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Camera-scoped tickets for the public routes Google reaches with no session of its own: the
/// WebRTC signaling endpoint, and the HLS playlist and segments a Cast receiver fetches.
///
/// <para><b>The two kinds have deliberately different shapes.</b> Signaling is one exchange by
/// Google's cloud, so its ticket is short and use-budgeted. Playback is a Cast device fetching a
/// playlist every few seconds and every segment behind it, for as long as somebody is watching, so
/// a budget there would only mean the picture stopping mid-view. See <see cref="MintForPlayback"/>
/// for what is given up and what is kept.</para>
///
/// <para><b>Why not a JWT on the existing signing key.</b> Three reasons, and the first is
/// concrete. <c>Program.cs</c> tells its two bearer schemes apart with
/// <c>hasStreamScope != requireStreamScope</c> — a boolean — so a token carrying some third scope
/// reads as <em>not</em> a stream token, is refused by the <c>StreamToken</c> scheme, and is
/// <em>accepted by the default one</em>: a signaling credential would quietly become a full Serval
/// session. Turning that boolean into a scope-name comparison means editing the security core for
/// one route. Second, a JWT cannot express "good for camera <c>front-door</c>, three times"
/// without server state anyway. Third, and decisively, a JWT cannot be revoked — and DISCONNECT
/// has to actually take access away.</para>
///
/// <para><b>Why a budget of three rather than single use.</b> <c>StreamTicketService</c>, which
/// this otherwise copies, consumes its ticket on the way in — right for a browser opening a socket
/// it controls. Google retries a failed signaling POST, so a one-shot ticket would turn a
/// momentary go2rtc hiccup into a permanently broken stream request. A small budget still caps a
/// leaked ticket at three views of one camera inside its window, which is the smallest bound that
/// is actually useful.</para>
///
/// <para>In-memory, for the reason <c>StreamTicketService</c> gives: nothing here supports more
/// than one Server instance. A restart loses live tickets, and the cost of that is one "show me
/// the front door" that fails and works on the retry.</para>
/// </summary>
public sealed class CameraStreamTicketService
{
    /// <summary>
    /// Three. Enough for Google's own retry plus a renegotiation, few enough that a ticket found
    /// in a proxy log is worth little.
    /// </summary>
    private const int UseBudget = 3;

    /// <summary>
    /// The budget of a playback ticket, which is to say none — see <see cref="MintForPlayback"/>.
    /// A sentinel rather than a flag on the entry so that the one place it matters,
    /// <see cref="TrySpend"/>, reads it without a second field to keep in step.
    /// </summary>
    private const int Unlimited = int.MaxValue;

    private readonly ConcurrentDictionary<string, Entry> _tickets = new();
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly TimeProvider _time;

    /// <summary>
    /// <paramref name="time"/> is the BCL's <see cref="TimeProvider"/>, registered as
    /// <see cref="TimeProvider.System"/> in <c>Program.cs</c>. It is here so expiry can be tested
    /// without a test that sleeps — the alternative was a test named for expiry that only asserted
    /// the case before it, which is worse than no test.
    /// </summary>
    public CameraStreamTicketService(IOptionsMonitor<ServerOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    private TimeSpan Ttl => TimeSpan.FromSeconds(
        Math.Clamp(_options.CurrentValue.GoogleHome.SignalingTicketSeconds, 10, 900));

    private TimeSpan PlaybackTtl => TimeSpan.FromMinutes(
        Math.Clamp(_options.CurrentValue.GoogleHome.HlsTicketMinutes, 1, 720));

    /// <summary>Mints a ticket for one camera. The raw value is the capability; nothing else names the camera.</summary>
    public string Mint(string cameraId)
    {
        string ticket = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = new Entry(cameraId, _time.GetUtcNow() + Ttl, UseBudget);
        return ticket;
    }

    /// <summary>
    /// A ticket for the HLS routes: same camera scoping, no use budget, and a lifetime measured in
    /// minutes rather than seconds.
    ///
    /// <para><b>Why no budget.</b> A Cast receiver re-fetches the playlist every few seconds and
    /// pulls every segment named in it. There is no number that distinguishes a normal view from
    /// an abusive one, so a budget would only decide how many minutes in the picture stops.</para>
    ///
    /// <para><b>What is still true.</b> It names one camera and nothing else, it cannot read the
    /// archive — the routes it opens serve the live edge only — and it expires. The lifetime
    /// default matches <c>stream_token</c>'s, which is the closest thing already in the codebase:
    /// a URL-borne credential that can watch video and do nothing else. That comparison is the
    /// point, because this one is handed to Google and travels to a device on the operator's
    /// network, so it should not be a broader grant than the App already hands its own player.</para>
    /// </summary>
    public string MintForPlayback(string cameraId)
    {
        string ticket = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = new Entry(cameraId, _time.GetUtcNow() + PlaybackTtl, Unlimited);
        return ticket;
    }

    /// <summary>
    /// Spends one use and returns the camera the ticket is for, or null if it is unknown, expired,
    /// or exhausted.
    ///
    /// <para>Removal is atomic and the entry is put back with one fewer use, so two concurrent
    /// calls cannot both spend the same one — the discipline
    /// <c>StreamTicketService.TryConsume</c> uses, extended from a budget of one to a budget of
    /// several. An expired entry is dropped rather than replaced, which is also what keeps the
    /// sweep cheap.</para>
    /// </summary>
    public string? TrySpend(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket))
        {
            return null;
        }

        // An unbudgeted ticket is read, not spent — and it must not go through the remove-and-put-
        // back below, which is only atomic against *counting*. A player fetching the playlist and a
        // segment at the same moment would have one of them arrive in the window where the entry is
        // out of the dictionary and be refused, which is a stream that stutters for no visible
        // reason. There is nothing to decrement here, so there is no reason to take it out at all.
        if (_tickets.TryGetValue(ticket, out Entry existing) && existing.UsesLeft == Unlimited)
        {
            return existing.ExpiresAt > _time.GetUtcNow() ? existing.CameraId : null;
        }

        if (!_tickets.TryRemove(ticket, out Entry entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= _time.GetUtcNow())
        {
            return null;
        }

        if (entry.UsesLeft > 1)
        {
            _tickets[ticket] = entry with { UsesLeft = entry.UsesLeft - 1 };
        }

        return entry.CameraId;
    }

    /// <summary>
    /// Drops a ticket outright — what <c>action: "end"</c> does. There is nothing to tear down
    /// on go2rtc's side, but the credential should not outlive the session it was for.
    /// </summary>
    public void Revoke(string? ticket)
    {
        if (!string.IsNullOrEmpty(ticket))
        {
            _tickets.TryRemove(ticket, out _);
        }
    }

    /// <summary>
    /// Drops every ticket for one camera — what switching it off in the Home app does.
    ///
    /// <para>Without this a session already playing would carry on after the switch was thrown,
    /// because the credential it is using is still valid. That reads as the switch not working,
    /// which is worse than a stream that stops.</para>
    /// </summary>
    public void RevokeForCamera(string cameraId)
    {
        foreach ((string key, Entry entry) in _tickets)
        {
            if (string.Equals(entry.CameraId, cameraId, StringComparison.Ordinal))
            {
                _tickets.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Expired-but-unspent tickets, so a display that never connects does not leak memory.</summary>
    public void SweepExpired()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach ((string key, Entry entry) in _tickets)
        {
            if (entry.ExpiresAt <= now)
            {
                _tickets.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Live ticket count, for the admin status card. Diagnostic only.</summary>
    public int Count => _tickets.Count;

    private readonly record struct Entry(string CameraId, DateTimeOffset ExpiresAt, int UsesLeft);
}

/// <summary>
/// Periodic cleanup for <see cref="CameraStreamTicketService"/>. Every ticket is already capped at a
/// couple of minutes, so this only needs to run occasionally — the same arrangement
/// <c>StreamTicketSweepWorker</c> has.
/// </summary>
public sealed class CameraStreamTicketSweepWorker(CameraStreamTicketService tickets) : BackgroundService
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
