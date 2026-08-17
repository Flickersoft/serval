using Serval.Server.Snapshots;

namespace Serval.Server.Tests;

/// <summary>
/// The broadcaster is the dashboard's fan-out. What matters: subscribers get published frames,
/// the latest-per-camera cache is what a freshly-connected client paints from, and a dropped
/// subscription stops receiving — a leak here would grow an undrained channel per lost viewer.
/// </summary>
public class SnapshotBroadcasterTests
{
    private static Snapshot Frame(string camera, byte b = 0xAB) =>
        new(camera, [0xFF, 0xD8, b, 0xFF, 0xD9], DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_subscriber_receives_published_frames()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription sub = broadcaster.Subscribe();

        broadcaster.Publish(Frame("cam1"));

        Snapshot received = await sub.Reader.ReadAsync(TestCts().Token);
        Assert.Equal("cam1", received.CameraId);
    }

    [Fact]
    public void Latest_returns_the_most_recent_frame_per_camera()
    {
        var broadcaster = new SnapshotBroadcaster();

        broadcaster.Publish(Frame("cam1", 0x01));
        broadcaster.Publish(Frame("cam1", 0x02));
        broadcaster.Publish(Frame("cam2", 0x03));

        Assert.Equal(0x02, broadcaster.Latest("cam1")!.Jpeg[2]);
        Assert.Equal(0x03, broadcaster.Latest("cam2")!.Jpeg[2]);
        Assert.Null(broadcaster.Latest("cam-unknown"));
    }

    [Fact]
    public void AllLatest_covers_every_camera_seen()
    {
        var broadcaster = new SnapshotBroadcaster();
        broadcaster.Publish(Frame("cam1"));
        broadcaster.Publish(Frame("cam2"));

        Assert.Equal(
            new[] { "cam1", "cam2" },
            broadcaster.AllLatest().Select(s => s.CameraId).OrderBy(x => x));
    }

    [Fact]
    public void Forget_drops_a_cameras_cached_frame()
    {
        var broadcaster = new SnapshotBroadcaster();
        broadcaster.Publish(Frame("cam1"));

        broadcaster.Forget("cam1");

        Assert.Null(broadcaster.Latest("cam1"));
    }

    [Fact]
    public void A_disposed_subscription_stops_receiving()
    {
        var broadcaster = new SnapshotBroadcaster();
        SnapshotBroadcaster.Subscription sub = broadcaster.Subscribe();
        sub.Dispose();

        broadcaster.Publish(Frame("cam1")); // must not throw, and nothing is queued for a gone sub

        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Two_subscribers_each_get_the_frame()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription a = broadcaster.Subscribe();
        using SnapshotBroadcaster.Subscription b = broadcaster.Subscribe();

        broadcaster.Publish(Frame("cam1"));

        Assert.Equal("cam1", (await a.Reader.ReadAsync(TestCts().Token)).CameraId);
        Assert.Equal("cam1", (await b.Reader.ReadAsync(TestCts().Token)).CameraId);
    }

    [Fact]
    public async Task A_keyed_subscriber_receives_only_its_own_camera()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription sub = broadcaster.Subscribe("cam1");

        broadcaster.Publish(Frame("cam2"));
        broadcaster.Publish(Frame("cam1", 0x07));
        broadcaster.Publish(Frame("cam3"));

        Snapshot received = await sub.Reader.ReadAsync(TestCts().Token);
        Assert.Equal("cam1", received.CameraId);
        Assert.Equal(0x07, received.Jpeg[2]);

        // The point of keying: the other cameras never consumed a slot in this buffer.
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public async Task An_unkeyed_subscriber_still_sees_every_camera()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription all = broadcaster.Subscribe();
        using SnapshotBroadcaster.Subscription keyed = broadcaster.Subscribe("cam1");

        broadcaster.Publish(Frame("cam1"));
        broadcaster.Publish(Frame("cam2"));

        Assert.Equal("cam1", (await all.Reader.ReadAsync(TestCts().Token)).CameraId);
        Assert.Equal("cam2", (await all.Reader.ReadAsync(TestCts().Token)).CameraId);
        Assert.Equal("cam1", (await keyed.Reader.ReadAsync(TestCts().Token)).CameraId);
    }

    [Fact]
    public void Two_keyed_subscribers_on_one_camera_both_get_the_frame()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription a = broadcaster.Subscribe("cam1");
        using SnapshotBroadcaster.Subscription b = broadcaster.Subscribe("cam1");

        broadcaster.Publish(Frame("cam1"));

        Assert.True(a.Reader.TryRead(out _));
        Assert.True(b.Reader.TryRead(out _));
    }

    [Fact]
    public void A_disposed_keyed_subscription_stops_receiving()
    {
        var broadcaster = new SnapshotBroadcaster();
        SnapshotBroadcaster.Subscription gone = broadcaster.Subscribe("cam1");
        using SnapshotBroadcaster.Subscription stays = broadcaster.Subscribe("cam1");
        gone.Dispose();

        broadcaster.Publish(Frame("cam1"));

        Assert.False(gone.Reader.TryRead(out _));
        Assert.True(stays.Reader.TryRead(out _)); // the surviving subscriber is untouched
    }

    [Fact]
    public void A_keyed_subscriber_keeps_its_whole_buffer_for_its_own_camera()
    {
        var broadcaster = new SnapshotBroadcaster();
        using SnapshotBroadcaster.Subscription sub = broadcaster.Subscribe("cam1");

        // 32 of this camera's frames interleaved with plenty of others. On an every-camera
        // subscription the noise would have evicted the earliest cam1 frames; keyed, none of it
        // consumes a slot, so all 32 survive.
        for (int i = 0; i < 32; i++)
        {
            broadcaster.Publish(Frame("cam1", (byte)i));
            broadcaster.Publish(Frame("cam2"));
            broadcaster.Publish(Frame("cam3"));
        }

        for (int i = 0; i < 32; i++)
        {
            Assert.True(sub.Reader.TryRead(out Snapshot? received));
            Assert.Equal((byte)i, received!.Jpeg[2]);
        }

        Assert.False(sub.Reader.TryRead(out _));
    }

    private static CancellationTokenSource TestCts() => new(TimeSpan.FromSeconds(5));
}
