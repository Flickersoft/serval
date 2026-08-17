using Serval.Server.Events;

namespace Serval.Server.Tests;

/// <summary>
/// The lane split, which exists because one dropped message used to be permanently wrong.
///
/// <para>An open detection episode publishes a position once per detection frame; the close that
/// ends it publishes once and nothing repeats it. Sharing one drop-oldest queue meant the
/// positions filled it and the eviction — taken from the head — reached the close first, because
/// the close was published before every position piled up behind it. The App went on drawing a
/// box over an object that had gone, for the rest of its session, and its reconnect heal never
/// fired because the socket had not broken.</para>
/// </summary>
public class EventBroadcasterTests
{
    private const int DroppableCapacity = 128;

    private static LiveEvent Event(string type = "detection", string id = "") =>
        new("driveway", type, new { id });

    private static string IdOf(LiveEvent liveEvent) =>
        (string)liveEvent.Document.GetType().GetProperty("id")!.GetValue(liveEvent.Document)!;

    [Fact]
    public void A_flood_of_positions_cannot_evict_a_close()
    {
        var broadcaster = new EventBroadcaster();
        using EventBroadcaster.Subscription subscription = broadcaster.Subscribe();

        // The close goes first, so in a single queue it would be the oldest item and therefore the
        // first thing evicted — which is exactly how this failed.
        broadcaster.Publish(Event(id: "close"));

        for (int i = 0; i < DroppableCapacity * 4; i++)
        {
            broadcaster.Publish(Event(id: $"position-{i}"), droppable: true);
        }

        Assert.True(subscription.Durable.TryRead(out LiveEvent? survived));
        Assert.Equal("close", IdOf(survived!));
    }

    [Fact]
    public void Positions_are_still_dropped_rather_than_backing_up()
    {
        // The other half of the bargain: a slow viewer must lose positions rather than apply
        // backpressure to ingest. Only the newest bufferful is kept.
        var broadcaster = new EventBroadcaster();
        using EventBroadcaster.Subscription subscription = broadcaster.Subscribe();

        for (int i = 0; i < DroppableCapacity * 3; i++)
        {
            broadcaster.Publish(Event(id: $"position-{i}"), droppable: true);
        }

        Assert.Equal(DroppableCapacity, subscription.Droppable.Count);

        Assert.True(subscription.Droppable.TryRead(out LiveEvent? oldestKept));
        Assert.Equal($"position-{(DroppableCapacity * 3) - DroppableCapacity}", IdOf(oldestKept!));
    }

    [Fact]
    public void Delivery_is_the_default_so_a_new_kind_of_event_is_not_quietly_droppable()
    {
        // Pinned because it is the safe half of a decision that is silent when it goes wrong: an
        // event added later without a thought about which lane it belongs in gets guaranteed
        // delivery, and pays a queue slot for it.
        var broadcaster = new EventBroadcaster();
        using EventBroadcaster.Subscription subscription = broadcaster.Subscribe();

        broadcaster.Publish(Event("scene"));

        Assert.Equal(1, subscription.Durable.Count);
        Assert.Equal(0, subscription.Droppable.Count);
    }

    [Fact]
    public void Every_subscriber_gets_its_own_copy()
    {
        var broadcaster = new EventBroadcaster();
        using EventBroadcaster.Subscription first = broadcaster.Subscribe();
        using EventBroadcaster.Subscription second = broadcaster.Subscribe();

        broadcaster.Publish(Event(id: "close"));

        Assert.Equal(1, first.Durable.Count);
        Assert.Equal(1, second.Durable.Count);
    }

    [Fact]
    public void Publishing_to_a_disposed_subscription_does_not_throw()
    {
        var broadcaster = new EventBroadcaster();
        EventBroadcaster.Subscription subscription = broadcaster.Subscribe();
        subscription.Dispose();

        broadcaster.Publish(Event(id: "close"));
        broadcaster.Publish(Event(id: "position"), droppable: true);
    }

    [Fact]
    public async Task Disposing_ends_both_lanes_so_a_reader_waiting_on_either_wakes()
    {
        // How the socket pump learns to stop: it parks on whichever lane has nothing, and both
        // have to complete or it would hang on the one that never did.
        var broadcaster = new EventBroadcaster();
        EventBroadcaster.Subscription subscription = broadcaster.Subscribe();

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<bool> durable = subscription.Durable.WaitToReadAsync(ct).AsTask();
        Task<bool> droppable = subscription.Droppable.WaitToReadAsync(ct).AsTask();

        subscription.Dispose();

        Assert.False(await durable);
        Assert.False(await droppable);
    }
}
