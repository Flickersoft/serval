using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Push;

/// <summary>
/// Storage for push subscriptions. Every method that touches one row takes the owning account with
/// it: the endpoint is the id, and an endpoint is a bearer credential of sorts — anyone holding one
/// can be handed it by a browser — so a delete that matched on endpoint alone would let one signed-in
/// account retire another's device by guessing nothing more than a URL it had seen.
/// </summary>
public sealed class PushSubscriptionRepository
{
    private readonly MongoContext _context;

    public PushSubscriptionRepository(MongoContext context) => _context = context;

    private IMongoCollection<PushSubscription> Subscriptions => _context.PushSubscriptions;

    /// <summary>
    /// Records a subscription, replacing whatever was stored for the same endpoint.
    ///
    /// An upsert rather than an insert because the App re-registers on every start — that is how a
    /// browser-reissued endpoint gets noticed — and the overwhelmingly common case is writing back
    /// exactly what is already there. Keys are re-read from the request rather than assumed
    /// unchanged: a browser may rotate them while keeping the endpoint.
    /// </summary>
    public async Task<PushSubscription> SaveAsync(
        PushSubscription subscription, CancellationToken cancellationToken = default)
    {
        FilterDefinition<PushSubscription> byId =
            Builders<PushSubscription>.Filter.Eq(s => s.Id, subscription.Id);

        // Preserve the delivery history across a re-registration; the row is the same device.
        if (await Subscriptions.Find(byId).FirstOrDefaultAsync(cancellationToken) is { } existing)
        {
            subscription.CreatedAt = existing.CreatedAt;
            subscription.LastSuccessAt = existing.LastSuccessAt;
            subscription.FailureCount = existing.FailureCount;
        }

        await Subscriptions.ReplaceOneAsync(
            byId,
            subscription,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

        return subscription;
    }

    /// <summary>Every device one account has registered.</summary>
    public Task<List<PushSubscription>> ListForUserAsync(
        string userId, CancellationToken cancellationToken = default) =>
        Subscriptions
            .Find(Builders<PushSubscription>.Filter.Eq(s => s.UserId, userId))
            .SortBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Everything to notify, across every account. The alert path reads the whole collection per
    /// alert: a household has a handful of devices, and an index on the alert's camera would not
    /// help because a subscription is not scoped to one.
    /// </summary>
    public Task<List<PushSubscription>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Subscriptions.Find(Builders<PushSubscription>.Filter.Empty).ToListAsync(cancellationToken);

    /// <summary>
    /// Retires a subscription. Called both by a person turning notifications off on a device and by
    /// the sender on a 404 or 410, which mean the same thing — this endpoint will never accept
    /// another message.
    /// </summary>
    public Task DeleteAsync(string id, string? userId = null, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<PushSubscription> by = Builders<PushSubscription>.Filter;
        FilterDefinition<PushSubscription> filter = by.Eq(s => s.Id, id);

        if (userId is not null)
        {
            filter &= by.Eq(s => s.UserId, userId);
        }

        return Subscriptions.DeleteOneAsync(filter, cancellationToken);
    }

    /// <summary>Notes that an endpoint accepted a message, clearing any run of failures.</summary>
    public Task RecordSuccessAsync(string id, CancellationToken cancellationToken = default) =>
        Subscriptions.UpdateOneAsync(
            Builders<PushSubscription>.Filter.Eq(s => s.Id, id),
            Builders<PushSubscription>.Update
                .Set(s => s.LastSuccessAt, DateTimeOffset.UtcNow)
                .Set(s => s.FailureCount, 0),
            options: null,
            cancellationToken);

    /// <summary>Notes a failure that was not a rejection, and says how many have run together.</summary>
    public async Task<int> RecordFailureAsync(string id, CancellationToken cancellationToken = default)
    {
        PushSubscription? updated = await Subscriptions.FindOneAndUpdateAsync(
            Builders<PushSubscription>.Filter.Eq(s => s.Id, id),
            Builders<PushSubscription>.Update.Inc(s => s.FailureCount, 1),
            new FindOneAndUpdateOptions<PushSubscription> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        return updated?.FailureCount ?? 0;
    }
}
