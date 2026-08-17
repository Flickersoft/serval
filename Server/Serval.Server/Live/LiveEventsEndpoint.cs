using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serval.Server.Auth;
using Serval.Server.Events;

namespace Serval.Server.Live;

/// <summary>
/// The App's live AI feed: a WebSocket that streams each new utterance/diarization as it's
/// ingested, as JSON text — <c>{ "camera_id", "type", "document" }</c>. An optional
/// <c>?camera=</c> query filters to one camera; omitted, it carries every camera. History is a
/// REST query; this socket is only the real-time tap.
/// </summary>
public static class LiveEventsEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapLiveEventsEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/api/events", (HttpContext context, EventBroadcaster events, StreamTicketService tickets) =>
            TicketedWebSocket.AcceptAsync(context, tickets, async (socket, aborted) =>
            {
                string? cameraFilter = context.Request.Query["camera"];
                using EventBroadcaster.Subscription subscription = events.Subscribe();
                await PumpAsync(socket, subscription, cameraFilter, aborted);
            }))
            .AsTicketedWebSocket();
    }

    /// <summary>
    /// Drains both of the subscription's lanes onto the socket, state before positions.
    ///
    /// <para>The priority is the point: a detection episode's close is one message and nothing
    /// repeats it, while the positions it is queued among are superseded twice a second. Reading
    /// them in arrival order would put the close behind whatever had piled up in front of it, and
    /// under the load that causes the pile-up it is the close that would be dropped waiting. See
    /// <see cref="EventBroadcaster"/> for the failure this ordering ends.</para>
    ///
    /// <para>The pending waits are held across iterations rather than started fresh each time
    /// round. <c>Task.WhenAny</c> abandons the loser, and a loop that mints a new waiter per event
    /// on a busy house would leave a trail of them on every channel it is not reading.</para>
    /// </summary>
    private static async Task PumpAsync(
        WebSocket socket,
        EventBroadcaster.Subscription subscription,
        string? cameraFilter,
        CancellationToken aborted)
    {
        Task<bool>? durableWait = null;
        Task<bool>? droppableWait = null;

        while (!aborted.IsCancellationRequested)
        {
            if (subscription.Durable.TryRead(out LiveEvent? liveEvent)
                || subscription.Droppable.TryRead(out liveEvent))
            {
                if (cameraFilter is not null && liveEvent.CameraId != cameraFilter)
                {
                    continue;
                }

                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    camera_id = liveEvent.CameraId,
                    type = liveEvent.Type,
                    document = liveEvent.Document,
                }, JsonOptions);

                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, aborted);
                continue;
            }

            durableWait ??= subscription.Durable.WaitToReadAsync(aborted).AsTask();
            droppableWait ??= subscription.Droppable.WaitToReadAsync(aborted).AsTask();

            Task<bool> completed = await Task.WhenAny(durableWait, droppableWait);

            // False means the lane was completed, which only happens when the subscription is
            // disposed — both lanes go together, so either one saying so ends the pump.
            if (!await completed)
            {
                return;
            }

            if (ReferenceEquals(completed, durableWait))
            {
                durableWait = null;
            }
            else
            {
                droppableWait = null;
            }
        }
    }
}
