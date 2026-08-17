using Serval.Server.Ai;

namespace Serval.Server.Tests;

/// <summary>
/// The level feed is computed only while somebody is watching, so <see cref="AudioLevelBroadcaster
/// .HasSubscribers"/> is not an optimisation — it is the switch that decides whether a camera is
/// measured at all. A subscription that outlives its viewer leaves a camera paying an RMS pass and
/// a ten-per-second publish for nobody, forever.
/// </summary>
public class AudioLevelBroadcasterTests
{
    private static AudioLevel Level(string cameraId = "front-door") =>
        new(cameraId, DateTimeOffset.UnixEpoch, 0.002f, 0.004f, 0.0015f, 0.01f, true, false);

    [Fact]
    public void A_camera_with_no_subscribers_is_not_watched() =>
        Assert.False(new AudioLevelBroadcaster().HasSubscribers("front-door"));

    [Fact]
    public void Subscribing_makes_the_camera_watched()
    {
        var broadcaster = new AudioLevelBroadcaster();

        using AudioLevelBroadcaster.Subscription subscription = broadcaster.Subscribe("front-door");

        Assert.True(broadcaster.HasSubscribers("front-door"));
    }

    [Fact]
    public void Disposing_the_subscription_stops_the_camera_being_watched()
    {
        var broadcaster = new AudioLevelBroadcaster();

        AudioLevelBroadcaster.Subscription subscription = broadcaster.Subscribe("front-door");
        subscription.Dispose();

        Assert.False(broadcaster.HasSubscribers("front-door"));
    }

    /// <summary>
    /// The emptied-bucket case. Unsubscribe deliberately leaves the per-camera dictionary in place
    /// — removing it races with a concurrent subscribe — so a <c>HasSubscribers</c> that tested
    /// only for the bucket's presence would report every camera that ever had a viewer as still
    /// being watched, permanently.
    /// </summary>
    [Fact]
    public void The_last_subscriber_leaving_stops_the_camera_being_watched()
    {
        var broadcaster = new AudioLevelBroadcaster();

        AudioLevelBroadcaster.Subscription first = broadcaster.Subscribe("front-door");
        AudioLevelBroadcaster.Subscription second = broadcaster.Subscribe("front-door");

        first.Dispose();
        Assert.True(broadcaster.HasSubscribers("front-door"));

        second.Dispose();
        Assert.False(broadcaster.HasSubscribers("front-door"));
    }

    [Fact]
    public void A_publish_reaches_only_the_camera_subscribed_to()
    {
        var broadcaster = new AudioLevelBroadcaster();

        using AudioLevelBroadcaster.Subscription subscription = broadcaster.Subscribe("front-door");

        broadcaster.Publish(Level("driveway"));
        Assert.False(subscription.Reader.TryRead(out _));

        broadcaster.Publish(Level("front-door"));
        Assert.True(subscription.Reader.TryRead(out AudioLevel? received));
        Assert.Equal("front-door", received!.CameraId);
    }

    [Fact]
    public void Publishing_to_a_camera_nobody_watches_is_harmless()
    {
        var broadcaster = new AudioLevelBroadcaster();

        broadcaster.Publish(Level()); // does not throw
    }

    /// <summary>
    /// A meter has no history worth buffering, so a reader far enough behind to fill the channel
    /// should be shown the newest reading rather than a queue of stale ones.
    /// </summary>
    [Fact]
    public void A_slow_reader_loses_old_levels_rather_than_the_newest()
    {
        var broadcaster = new AudioLevelBroadcaster();
        using AudioLevelBroadcaster.Subscription subscription = broadcaster.Subscribe("front-door");

        // Capacity is 4; publish more than that without reading.
        for (int i = 0; i < 10; i++)
        {
            broadcaster.Publish(Level() with { Peak = i });
        }

        var received = new List<float>();
        while (subscription.Reader.TryRead(out AudioLevel? level))
        {
            received.Add(level.Peak);
        }

        Assert.Equal(4, received.Count);
        Assert.Equal(9, received[^1]);       // the newest survived
        Assert.DoesNotContain(0, received);  // the oldest did not
    }

    /// <summary>
    /// Completing the writer is what ends a reader parked in <c>ReadAllAsync</c>, so a session
    /// torn down from the server side does not depend on its cancellation token having fired.
    /// </summary>
    [Fact]
    public async Task Unsubscribing_completes_the_channel_writer()
    {
        var broadcaster = new AudioLevelBroadcaster();
        AudioLevelBroadcaster.Subscription subscription = broadcaster.Subscribe("front-door");

        subscription.Dispose();

        var drained = new List<AudioLevel>();
        await foreach (AudioLevel level in subscription.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            drained.Add(level);
        }

        Assert.Empty(drained); // completed rather than hanging
    }
}
