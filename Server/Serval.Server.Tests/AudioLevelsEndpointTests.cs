using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Ai;
using Serval.Server.Live;

namespace Serval.Server.Tests;

/// <summary>
/// How a level-feed session ends, which matters more than how it runs.
///
/// The server measures a camera's audio level only while somebody is subscribed, so a session that
/// fails to end leaves a camera being measured for a viewer that is no longer there — the feature's
/// central claim, that it is free when unwatched, is exactly what a leak breaks.
///
/// Three ways a viewer goes away, and they are not equivalent. A crashed process drops the
/// connection and the next send throws, which needs nothing clever. A cleanly-closing client sends
/// a close frame, which is only ever noticed because something reads the socket. And a
/// <em>frozen</em> client leaves TCP open and simply stops reading — no exception, no abort, no
/// close frame, and <c>RequestAborted</c> never fires. That last one is why the send timeout
/// exists, and it is the case that would otherwise measure forever.
///
/// Every test here asserts the subscription was released, not merely that the loop exited. Those
/// are different claims and only the first one is the point.
/// </summary>
public class AudioLevelsEndpointTests
{
    private const string CameraId = "front-door";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LongEnoughNotToFire = TimeSpan.FromMinutes(5);

    private static AudioLevel Level() =>
        new(CameraId, DateTimeOffset.UnixEpoch, 0.002f, 0.004f, 0.0015f, 0.01f, true, false);

    /// <summary>
    /// A socket whose two halves are controlled independently, so each teardown path can be driven
    /// on its own. Only the members the session actually calls are implemented.
    /// </summary>
    private sealed class FakeSocket : WebSocket
    {
        private readonly TaskCompletionSource<WebSocketReceiveResult> _receive = new();

        /// <summary>When set, every send parks forever — the frozen client.</summary>
        public bool SendsHang { get; init; }

        /// <summary>When set, the first send throws as a dropped connection does.</summary>
        public bool SendsThrow { get; init; }

        public int SendCount { get; private set; }

        public void SendCloseFrame() => _receive.TrySetResult(
            new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCount++;

            if (SendsThrow)
            {
                throw new WebSocketException("the peer went away");
            }

            if (SendsHang)
            {
                // Never completes on its own. Only the per-send timeout can end this, which is the
                // whole point of the test that uses it.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // A real client on this feed sends nothing until it closes, so the drain loop parks
            // here for the life of the session.
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => _receive.TrySetCanceled(cancellationToken));

            return await _receive.Task;
        }

        public override WebSocketState State => WebSocketState.Open;

        public override void Abort() { }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose() { }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;
    }

    private static Task RunAsync(
        FakeSocket socket,
        AudioLevelBroadcaster levels,
        CancellationToken cancellationToken,
        TimeSpan? sendTimeout = null,
        TimeSpan? sessionCap = null) =>
        AudioLevelsEndpoint.RunSessionAsync(
            socket,
            levels,
            CameraId,
            NullLogger.Instance,
            cancellationToken,
            sendTimeout ?? LongEnoughNotToFire,
            sessionCap ?? LongEnoughNotToFire);

    [Fact]
    public async Task A_running_session_makes_the_camera_watched()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket();
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token);

        // The subscription is taken synchronously, before the first await that could yield.
        Assert.True(levels.HasSubscribers(CameraId));

        await cts.CancelAsync();
        await session;
    }

    [Fact]
    public async Task A_client_close_frame_ends_the_session_and_releases_the_subscription()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket();
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token);
        Assert.True(levels.HasSubscribers(CameraId));

        socket.SendCloseFrame();

        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(levels.HasSubscribers(CameraId));
    }

    /// <summary>
    /// The frozen client. Nothing about this connection is broken — TCP is open, no exception is
    /// raised, and the request is never aborted — so the send timeout is the only thing that can
    /// end it. Without it, this camera is measured for the lifetime of the process.
    /// </summary>
    [Fact]
    public async Task A_send_that_never_completes_ends_the_session_and_releases_the_subscription()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket { SendsHang = true };
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token, sendTimeout: ShortTimeout);

        // Give the reader a level to wedge on.
        await Task.Delay(20, TestContext.Current.CancellationToken);
        levels.Publish(Level());

        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, socket.SendCount);
        Assert.False(levels.HasSubscribers(CameraId));
    }

    [Fact]
    public async Task A_dropped_connection_ends_the_session_and_releases_the_subscription()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket { SendsThrow = true };
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        levels.Publish(Level());

        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(levels.HasSubscribers(CameraId));
    }

    /// <summary>
    /// The backstop. Whatever else happens, a session is bounded — so any leak path not anticipated
    /// costs at most one cap's worth of measuring rather than the lifetime of the process.
    /// </summary>
    [Fact]
    public async Task A_session_ends_at_the_cap_and_releases_the_subscription()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket();
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token, sessionCap: ShortTimeout);

        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(levels.HasSubscribers(CameraId));
    }

    [Fact]
    public async Task An_aborted_request_ends_the_session_and_releases_the_subscription()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket();
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token);
        Assert.True(levels.HasSubscribers(CameraId));

        await cts.CancelAsync();

        await session.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(levels.HasSubscribers(CameraId));
    }

    [Fact]
    public async Task Levels_published_while_connected_are_sent()
    {
        var levels = new AudioLevelBroadcaster();
        var socket = new FakeSocket();
        using var cts = new CancellationTokenSource();

        Task session = RunAsync(socket, levels, cts.Token);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        levels.Publish(Level());
        levels.Publish(Level());
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await session;

        Assert.Equal(2, socket.SendCount);
    }
}
