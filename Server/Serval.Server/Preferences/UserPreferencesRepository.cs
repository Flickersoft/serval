using MongoDB.Driver;
using Serval.Server.Configuration;
using Serval.Server.Storage;

namespace Serval.Server.Preferences;

/// <summary>
/// Reading and writing one account's own preferences, keyed by the user id the token carries.
///
/// There is no "list everyone's" and no read by an id the caller supplies: every method takes the
/// id the endpoint pulled off the authenticated principal, so a Viewer cannot reach another
/// person's wall by asking for it. That is enforced by there being no route shaped to allow it
/// rather than by a check somebody has to remember.
/// </summary>
public sealed class UserPreferencesRepository
{
    /// <summary>
    /// The most tiles one wall may store. Not a product limit — it is a bound on what an
    /// authenticated client can put in the database, since the list arrives from outside and
    /// nothing else caps its length.
    /// </summary>
    public const int MaxTiles = 256;

    /// <summary>
    /// The most notification rules one account may store — one per camera, with room to spare for
    /// rules left behind by cameras that have since been removed. A bound on what an authenticated
    /// client can write, for the same reason <see cref="MaxTiles"/> is one.
    /// </summary>
    public const int MaxNotificationRules = 256;

    /// <summary>The most labels one rule may name, which is more classes than any model reports.</summary>
    public const int MaxLabelsPerRule = 512;

    private readonly IMongoCollection<UserPreferences> _preferences;

    public UserPreferencesRepository(MongoContext context) =>
        _preferences = context.UserPreferences;

    /// <summary>
    /// This account's preferences, or an empty set for someone who has never saved any.
    ///
    /// Absent is not an error and is not distinguished from empty: "I have not arranged my wall"
    /// and "my wall is arranged as nothing" mean the same thing to every caller, and the App packs
    /// its default for both.
    /// </summary>
    public async Task<UserPreferences> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        await _preferences.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken)
        ?? UserPreferences.Empty(userId);

    /// <summary>
    /// Stores this account's wall layout, creating the document on first save.
    ///
    /// An upsert on the id rather than a read-modify-write: two tabs saving at once should leave
    /// one of the two layouts, not a document assembled from halves of both.
    /// </summary>
    public async Task<UserPreferences> SaveWallLayoutAsync(
        string userId, IReadOnlyList<WallTile> layout, CancellationToken cancellationToken = default)
    {
        Validate(layout);

        UpdateDefinition<UserPreferences> update = Builders<UserPreferences>.Update
            .Set(p => p.WallLayout, [.. layout])
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .SetOnInsert(p => p.UserId, userId);

        return await _preferences.FindOneAndUpdateAsync<UserPreferences>(
            p => p.UserId == userId,
            update,
            new FindOneAndUpdateOptions<UserPreferences>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken);
    }

    /// <summary>
    /// Stores whether this account wants notifications at all.
    ///
    /// Its own write rather than part of the rules below, so the master switch and the per-camera
    /// detail can be changed independently — which is what lets "mute everything this evening" not
    /// disturb a carefully arranged set of rules it would then have to restore.
    /// </summary>
    public async Task<UserPreferences> SaveNotificationsEnabledAsync(
        string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        UpdateDefinition<UserPreferences> update = Builders<UserPreferences>.Update
            .Set(p => p.NotificationsEnabled, enabled)
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .SetOnInsert(p => p.UserId, userId);

        return await _preferences.FindOneAndUpdateAsync<UserPreferences>(
            p => p.UserId == userId,
            update,
            new FindOneAndUpdateOptions<UserPreferences>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken);
    }

    /// <summary>
    /// Stores this account's per-camera notification rules, replacing the set whole.
    ///
    /// Whole rather than one camera at a time because the screen edits the set as one thing and it
    /// is small. The merge that matters is between <em>preferences</em>, which the endpoint does by
    /// writing only the properties a request actually mentioned.
    /// </summary>
    public async Task<UserPreferences> SaveNotificationRulesAsync(
        string userId,
        IReadOnlyList<CameraNotificationRule> rules,
        CancellationToken cancellationToken = default)
    {
        ValidateRules(rules);

        UpdateDefinition<UserPreferences> update = Builders<UserPreferences>.Update
            .Set(p => p.Notifications, [.. rules])
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .SetOnInsert(p => p.UserId, userId);

        return await _preferences.FindOneAndUpdateAsync<UserPreferences>(
            p => p.UserId == userId,
            update,
            new FindOneAndUpdateOptions<UserPreferences>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken);
    }

    /// <summary>
    /// Every account's preferences.
    ///
    /// <para>The one read here not scoped to the caller, and the exception the class note calls out
    /// as not existing — so it is worth being clear about why it is safe. It backs the Admin-only
    /// configuration backup, which is a whole-database export by definition; there is no route that
    /// reaches it with a user id, so it does not reopen "read someone else's wall by asking".</para>
    /// </summary>
    public async Task<List<UserPreferences>> ListAsync(CancellationToken cancellationToken = default) =>
        await _preferences.Find(FilterDefinition<UserPreferences>.Empty).ToListAsync(cancellationToken);

    /// <summary>
    /// Writes one account's preferences whole, creating the document if it is absent. Returns true
    /// when it did not exist.
    ///
    /// <para>Written whole rather than through <see cref="SaveWallLayoutAsync"/>, because a restore
    /// is replacing the document rather than changing one preference — the lost-update the
    /// endpoint's merge semantics exist to prevent is not a hazard when the whole thing arrives at
    /// once from a file.</para>
    /// </summary>
    public async Task<bool> RestoreAsync(
        UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        Validate(preferences.WallLayout);
        ValidateRules(preferences.Notifications);

        UpdateDefinition<UserPreferences> update = Builders<UserPreferences>.Update
            .Set(p => p.WallLayout, preferences.WallLayout)
            .Set(p => p.NotificationsEnabled, preferences.NotificationsEnabled)
            .Set(p => p.Notifications, preferences.Notifications)
            .Set(p => p.UpdatedAt, DateTimeOffset.UtcNow)
            .SetOnInsert(p => p.UserId, preferences.UserId);

        UpdateResult result = await _preferences.UpdateOneAsync(
            p => p.UserId == preferences.UserId,
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        return result.UpsertedId is not null;
    }

    /// <summary>
    /// Forgets an account's preferences. Called when the account itself is deleted, so a new user
    /// created with a recycled username does not inherit the last one's wall.
    /// </summary>
    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default) =>
        await _preferences.DeleteOneAsync(p => p.UserId == userId, cancellationToken);

    /// <summary>
    /// What the server insists on: a tile is on the grid, spans at least one cell, and no camera
    /// appears twice.
    ///
    /// Not overlap, and not packing. Those rules live in the App's <c>WallGrid</c> and belong
    /// there — a second implementation here would be a second thing to keep in step, and the App
    /// reconciles the stored layout against the cameras that actually exist on every read, so a
    /// layout that is merely ugly is already survivable. What is checked is what would make the
    /// document itself nonsense.
    /// </summary>
    internal static void Validate(IReadOnlyList<WallTile> layout)
    {
        if (layout.Count > MaxTiles)
        {
            throw new PreferencesValidationException(
                $"A wall layout cannot hold more than {MaxTiles} tiles.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (WallTile tile in layout)
        {
            if (string.IsNullOrWhiteSpace(tile.CameraId))
            {
                throw new PreferencesValidationException("A wall tile must name a camera.");
            }

            if (!seen.Add(tile.CameraId))
            {
                throw new PreferencesValidationException(
                    $"Camera '{tile.CameraId}' appears more than once in the layout.");
            }

            if (tile.ColumnSpan < 1 || tile.RowSpan < 1)
            {
                throw new PreferencesValidationException(
                    $"Camera '{tile.CameraId}' has a tile smaller than one cell.");
            }

            if (tile.Column < 0 || tile.Row < 0)
            {
                throw new PreferencesValidationException(
                    $"Camera '{tile.CameraId}' has a tile off the top or left of the grid.");
            }

            if (tile.Column + tile.ColumnSpan > WallGridBounds.Columns)
            {
                throw new PreferencesValidationException(
                    $"Camera '{tile.CameraId}' has a tile running past the right of the grid.");
            }

            // Rows are deliberately unbounded on the App's grid — the wall grows downward — so
            // there is no upper row to check against. MaxTiles is what bounds the document.
        }
    }

    /// <summary>
    /// What the server insists on for notification rules: one rule per camera, and a bound on the
    /// document.
    ///
    /// <para>The class lists are deliberately <b>not</b> checked against what the camera actually
    /// alerts on. That set is a moving target — an admin can widen or narrow it at any time — and a
    /// rule naming a class the camera does not raise today is harmless (it matches nothing) and
    /// becomes meaningful the day it does. Validating against it would trade that for an error
    /// message about a set the person cannot see from the screen they are on. See
    /// <see cref="CameraNotificationRule"/> for the full argument.</para>
    /// </summary>
    /// <remarks>
    /// Named apart from <see cref="Validate(IReadOnlyList{WallTile})"/> rather than overloading it.
    /// Both take a list, and <c>Validate([])</c> — which is how a caller says "nothing to check" —
    /// cannot pick between two overloads that differ only in element type.
    /// </remarks>
    internal static void ValidateRules(IReadOnlyList<CameraNotificationRule> rules)
    {
        if (rules.Count > MaxNotificationRules)
        {
            throw new PreferencesValidationException(
                $"There cannot be more than {MaxNotificationRules} notification rules.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (CameraNotificationRule rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.CameraId))
            {
                throw new PreferencesValidationException("A notification rule must name a camera.");
            }

            if (!seen.Add(rule.CameraId))
            {
                throw new PreferencesValidationException(
                    $"Camera '{rule.CameraId}' has more than one notification rule.");
            }

            if (rule.ObjectClasses?.Length > MaxLabelsPerRule
                || rule.SoundLabels?.Length > MaxLabelsPerRule)
            {
                throw new PreferencesValidationException(
                    $"Camera '{rule.CameraId}' names more than {MaxLabelsPerRule} labels.");
            }

            // Bounded against the same constant the catalogue draws its slider from, so the settings
            // page and this endpoint cannot disagree about what a legal cooldown is.
            if (rule.CooldownSeconds is < 0 or > PushOptions.MaxCooldownSeconds)
            {
                throw new PreferencesValidationException(
                    $"Camera '{rule.CameraId}' asks for a cooldown outside 0 to "
                    + $"{PushOptions.MaxCooldownSeconds} seconds.");
            }
        }
    }
}

/// <summary>The one grid fact the server needs. The rest of the grid's rules are the App's.</summary>
public static class WallGridBounds
{
    /// <summary>Mirrors <c>WallGrid.columns</c>. Twenty-four, because it divides by 2, 3, 4, 6, 8 and 12.</summary>
    public const int Columns = 24;
}

/// <summary>A rejected preferences write.</summary>
public sealed class PreferencesValidationException(string message) : ValidationException(message);
