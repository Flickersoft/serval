using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using Serval.Server.Auth;
using Serval.Server.Live;
using Serval.Server.Snapshots;

namespace Serval.Server.Dashboard;

/// <summary>
/// The multi-camera live wall: one WebSocket per viewer carrying every camera's ~1 fps
/// snapshot. Frames are binary — <c>[uint32 cameraId length][cameraId UTF-8][JPEG bytes]</c> —
/// to avoid the ~33% base64 tax of sending images as JSON. The client keys tiles by cameraId
/// and swaps a tile to the camera's HLS stream when the viewer opens it full-screen.
/// </summary>
public static class DashboardEndpoint
{
    public static void MapDashboardEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/api/dashboard", (HttpContext context, SnapshotBroadcaster broadcaster, StreamTicketService tickets) =>
            TicketedWebSocket.AcceptAsync(context, tickets, async (socket, aborted) =>
            {
                // Subscribe before painting the current frames, so nothing published in between is lost.
                using SnapshotBroadcaster.Subscription subscription = broadcaster.Subscribe();

                foreach (Snapshot snapshot in broadcaster.AllLatest())
                {
                    await SendAsync(socket, snapshot, aborted);
                }

                await foreach (Snapshot snapshot in subscription.Reader.ReadAllAsync(aborted))
                {
                    await SendAsync(socket, snapshot, aborted);
                }
            }))
            .AsTicketedWebSocket();
    }

    private static async Task SendAsync(WebSocket socket, Snapshot snapshot, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        // Sent as two fragments of one message rather than one concatenated buffer. A 1080p JPEG
        // is normally past the 85 KB large-object threshold, so building a combined array would
        // put an LOH allocation and a full image copy on every frame, for every viewer, purely to
        // prepend four bytes. The receiver reassembles the fragments, so the wire format above is
        // unchanged. Safe to fragment because each socket is written by this one loop.
        int idLength = Encoding.UTF8.GetByteCount(snapshot.CameraId);
        var header = new byte[4 + idLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)idLength);
        Encoding.UTF8.GetBytes(snapshot.CameraId, header.AsSpan(4));

        await socket.SendAsync(header, WebSocketMessageType.Binary, endOfMessage: false, cancellationToken);
        await socket.SendAsync(
            snapshot.Jpeg, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }
}
