using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Serval.Server.Ai;
using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Live;

/// <summary>
/// The camera settings screen's input level meter: a WebSocket carrying one camera's measured
/// audio level about ten times a second, alongside the thresholds it is being compared against.
///
/// Exists because the thresholds it feeds are otherwise tuned blind. The fault that motivated all
/// of this — a gate set above a quiet room's speech, discarding ten of eleven utterances — was
/// invisible from every surface the server offered: the camera was enabled, the model was loaded,
/// sound events were arriving, and only the utterances were missing. This is the instrument that
/// makes the level a person can see.
/// </summary>
public static class AudioLevelsEndpoint
{
    /// <summary>
    /// How long a send may take before the peer is presumed gone.
    ///
    /// This is the case <see cref="HttpContext.RequestAborted"/> cannot catch. A client that
    /// crashes outright drops the connection and the next send throws; a client that
    /// <em>freezes</em> leaves TCP open and simply stops reading, so the send buffer fills, the
    /// send blocks forever, and nothing ever cancels. Against a ten-per-second feed of small JSON
    /// messages, a send that cannot complete in five seconds means the peer is not reading.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hard ceiling on one session, whatever else happens.
    ///
    /// The server measures a camera's level only while somebody is subscribed, so a subscription
    /// that outlives its viewer is not just waste — it defeats the reason the feed is free when
    /// unwatched. This is the backstop for any way of leaking one not anticipated above: a meter
    /// is a tuning aid attached to an open settings panel, not a monitoring feed, so a bounded
    /// session costs a real viewer nothing. The App reconnects, and the reconnect is itself proof
    /// that somebody is still there.
    /// </summary>
    private static readonly TimeSpan SessionCap = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void MapAudioLevelsEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/api/cameras/{id}/audio-levels", (
            string id,
            HttpContext context,
            CameraRepository cameras,
            AudioLevelBroadcaster levels,
            IOptionsMonitor<ServerOptions> options,
            ILoggerFactory loggerFactory,
            StreamTicketService tickets) =>
            TicketedWebSocket.AcceptAsync(
                context,
                tickets,
                (socket, aborted) => RunSessionAsync(
                    socket, levels, id,
                    loggerFactory.CreateLogger(typeof(AudioLevelsEndpoint).FullName!), aborted),
                gate: async () =>
                {
                    // Rejected before the upgrade, with the reason. A socket that accepts and
                    // then never sends is indistinguishable from a camera whose microphone is
                    // dead, and the App can only say which one it is if it is told.
                    if (!options.CurrentValue.ServerAi.Enabled)
                    {
                        return Results.BadRequest(
                            "Serval:ServerAi:Enabled is false, so no camera's audio is being analysed.");
                    }

                    Camera? camera = await cameras.GetAsync(id, context.RequestAborted);
                    if (camera is null)
                    {
                        return Results.NotFound(new { error = $"No camera '{id}'." });
                    }

                    if (!camera.AiAudio)
                    {
                        return Results.BadRequest(
                            $"Camera '{id}' has AiAudio false, so its audio is never opened and "
                            + "there is no level to report.");
                    }

                    return null;
                }))
            .AsTicketedWebSocket();
    }

    /// <summary>
    /// One viewer's session, from subscribe to release.
    ///
    /// The subscription is taken and disposed here rather than by the caller because subscribing
    /// is what makes the detector start measuring this camera, so "the session ended" and "the
    /// camera stopped being measured" have to be the same event. Everything else in this method
    /// exists to guarantee it returns.
    ///
    /// <para>Internal so the teardown paths can be driven against a fake socket. They are the part
    /// worth testing: a session that fails to end leaves a camera measured for a viewer that is no
    /// longer there.</para>
    /// </summary>
    internal static async Task RunSessionAsync(
        WebSocket socket,
        AudioLevelBroadcaster levels,
        string cameraId,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? sendTimeout = null,
        TimeSpan? sessionCap = null)
    {
        using AudioLevelBroadcaster.Subscription subscription = levels.Subscribe(cameraId);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.CancelAfter(sessionCap ?? SessionCap);

        try
        {
            // Two jobs that are only meaningful together: publish, and watch for the peer going
            // away. Whichever finishes first ends the other, so this returns — and it is
            // returning, not any particular exception, that releases the subscription above.
            await TaskGroup.RunUntilFirstCompletesAsync(
                [
                    token => PublishAsync(
                        socket, subscription, logger, cameraId, sendTimeout ?? SendTimeout, token),
                    token => DrainAsync(socket, token),
                ],
                session.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { /* client vanished; nothing to do */ }
    }

    private static async Task PublishAsync(
        WebSocket socket,
        AudioLevelBroadcaster.Subscription subscription,
        ILogger logger,
        string cameraId,
        TimeSpan sendTimeout,
        CancellationToken cancellationToken)
    {
        await foreach (AudioLevel level in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(level, JsonOptions);

            // The timeout is per send, not per session: a peer that is merely slow recovers, one
            // that has stopped reading does not.
            using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            send.CancelAfter(sendTimeout);

            try
            {
                await socket.SendAsync(
                    payload, WebSocketMessageType.Text, endOfMessage: true, send.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Logged rather than silent: a client that repeatedly wedges is a bug worth
                // seeing, and this is the only place it is visible.
                logger.LogInformation(
                    "Dropping the audio level feed for camera {CameraId}: the client stopped "
                    + "reading and a send did not complete within {Seconds}s.",
                    cameraId, sendTimeout.TotalSeconds);
                return;
            }
        }
    }

    /// <summary>
    /// Reads and discards whatever the client sends, returning when it closes.
    ///
    /// Nothing here wants the client's messages — this feed is one-way. It exists because a socket
    /// that is never read from never observes a close frame, so a cleanly-closing viewer would
    /// otherwise be noticed only when a later send happened to fail. It also gives the transport a
    /// second, independent path on which to surface a peer that has gone away.
    /// </summary>
    private static async Task DrainAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var scratch = new byte[256];

        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(scratch, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }
}
