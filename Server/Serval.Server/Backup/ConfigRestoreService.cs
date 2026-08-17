using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Preferences;

namespace Serval.Server.Backup;

/// <summary>
/// Merges a <see cref="ConfigBackupFile"/> into this Server.
///
/// <para><b>Merge, never replace.</b> Everything the file names is created or overwritten;
/// everything it does not name is left exactly as it is. Nothing is deleted — a camera added since
/// the backup survives the restore, and so does an account. The one exception proves the rule: the
/// stale tail of a list setting the file has shortened is removed, because the unit of meaning
/// there is the list and not the index (see <see cref="PlanSettingsWrites"/>).</para>
///
/// <para><b>Best-effort, not transactional.</b> Mongo runs here as a standalone without a replica
/// set, so a multi-document transaction is unavailable, and "all or nothing" would mean hand-rolled
/// compensation — a second write path whose own failure mode is strictly worse, having discarded the
/// file's changes and possibly not restored the originals. It would also break the main use case on
/// a detail: the likeliest refusal is one camera asking for a codec the new host's ffmpeg cannot
/// encode, and abandoning six cameras, twelve settings and three accounts over it is wrong. No
/// cross-document invariant survives a partial apply except the last-Admin one, handled as a
/// precondition on the accounts. A restore is idempotent, so running the same file again after
/// fixing what was refused converges.</para>
///
/// <para><b>Section order is settings → cameras → users → preferences, and it matters twice.</b>
/// Cameras are validated against <c>Serval:Ingest</c>, so settings must already be in force or a
/// camera is judged against a configuration the file replaced. Preferences are keyed by account, so
/// the accounts the file creates must exist before their walls are written.</para>
/// </summary>
public sealed class ConfigRestoreService
{
    private readonly SettingsConfigurationProvider _settings;
    private readonly CameraRepository _cameras;
    private readonly UserRepository _users;
    private readonly RefreshTokenRepository _refreshTokens;
    private readonly UserPreferencesRepository _preferences;
    private readonly ILogger<ConfigRestoreService> _logger;

    public ConfigRestoreService(
        SettingsConfigurationProvider settings,
        CameraRepository cameras,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        UserPreferencesRepository preferences,
        ILogger<ConfigRestoreService> logger)
    {
        _settings = settings;
        _cameras = cameras;
        _users = users;
        _refreshTokens = refreshTokens;
        _preferences = preferences;
        _logger = logger;
    }

    public async Task<ConfigRestoreResult> RestoreAsync(
        ConfigBackupFile file, string? actorUserId, CancellationToken cancellationToken = default)
    {
        List<RestoreSkip> skips = [];
        List<string> notes = [];
        List<RestoreSection> sections =
        [
            await RestoreSettingsAsync(file, actorUserId, skips, notes, cancellationToken),
            await RestoreCamerasAsync(file, skips, cancellationToken),
            await RestoreUsersAsync(file, actorUserId, skips, notes, cancellationToken),
            await RestorePreferencesAsync(file, skips, cancellationToken),
        ];

        _logger.LogInformation(
            "Configuration restored by {User} from a backup taken {TakenAt}: {Summary}. {Skipped} skipped.",
            actorUserId,
            file.CreatedAt,
            string.Join(", ", sections.Select(s => $"{s.Name} +{s.Created}/~{s.Updated}")),
            skips.Count);

        return new ConfigRestoreResult(
            RestoredAt: DateTimeOffset.UtcNow,
            FileCreatedAt: file.CreatedAt,
            FileCreatedBy: file.CreatedBy,
            Sections: sections,
            Skipped: skips,
            Notes: notes);
    }

    // ---- settings ------------------------------------------------------------------------------

    private async Task<RestoreSection> RestoreSettingsAsync(
        ConfigBackupFile file,
        string? actorUserId,
        List<RestoreSkip> skips,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> stored = _settings.OverriddenKeys;
        Dictionary<string, string?> writes = PlanSettingsWrites(file.Settings, stored, skips, notes);

        if (writes.Count == 0)
        {
            return new RestoreSection("settings", 0, 0, CountSkips(skips, "settings"), 0);
        }

        // One save for the whole change set, not one per key. The store is a read-modify-write of a
        // single document with no optimistic concurrency, justified by there being one writer; a
        // restore is a much bigger write than a settings form and splitting it would multiply the
        // window rather than shrink it. It is also one OnReload() rather than dozens.
        await _settings.SaveAsync(writes, actorUserId, cancellationToken);

        int cleared = writes.Count(w => w.Value is null);
        int written = writes.Count - cleared;
        int created = writes.Count(w => w.Value is not null && !stored.Contains(w.Key));

        return new RestoreSection(
            "settings", created, written - created, CountSkips(skips, "settings"), cleared);
    }

    /// <summary>
    /// Turns the file's stored overlay into the write set that merges it, rejecting keys this
    /// Server will not accept.
    ///
    /// <para><b>A list is merged as a list, not as its indices.</b>
    /// <c>Serval:Ai:Sound:AlertLabels:0</c> is not a setting;
    /// <c>Serval:Ai:Sound:AlertLabels</c> is. If this Server holds five entries and the file holds
    /// two, writing only the file's two leaves indices 2–4 behind and the binder reads a five-entry
    /// list that is neither the file's nor this Server's — a splice of the two, which is not a merge
    /// of anything. So every list the file names is cleared to its stored extent first and then
    /// written. A list the file does <em>not</em> name is untouched, exactly like a scalar it does
    /// not name; merge holds.</para>
    ///
    /// <para>Pure and internal so the whole of the above is testable without a database — the same
    /// reason <c>SettingsEndpoints.Translate</c> is.</para>
    /// </summary>
    /// <param name="stored">The keys the overlay currently holds, needed to know a list's extent.</param>
    internal static Dictionary<string, string?> PlanSettingsWrites(
        IReadOnlyDictionary<string, string> file,
        IReadOnlySet<string> stored,
        List<RestoreSkip> skips,
        List<string> notes)
    {
        Dictionary<string, string?> writes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> clearedLists = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in file)
        {
            if (SettingsCatalog.ValidateStored(key, value) is { } error)
            {
                skips.Add(new RestoreSkip("settings", key, error));
                continue;
            }

            if (ListParent(key) is { } parent)
            {
                // First time this list is seen, drop every index it currently occupies. Ordering is
                // safe: the clears go in before any of this list's values, and a value written
                // afterwards overwrites the null left at the same key.
                if (clearedLists.Add(parent))
                {
                    foreach (string index in stored.Where(k => IsIndexOf(parent, k)))
                    {
                        writes[index] = null;
                    }
                }

                writes[key] = value;
                Note(SettingsCatalog.Find(parent), notes);
                continue;
            }

            writes[key] = value;
            Note(SettingsCatalog.Find(key), notes);
        }

        return writes;
    }

    /// <summary>
    /// The list descriptor a stored key is an entry of, or null when the key is a scalar. Only
    /// called after <see cref="SettingsCatalog.ValidateStored"/> has accepted the key, so a
    /// non-null answer here is a key that really is an indexed child of a real list.
    /// </summary>
    private static string? ListParent(string key)
    {
        int separator = key.LastIndexOf(':');
        return separator > 0
            && SettingsCatalog.Find(key[..separator]) is { Kind: SettingKind.TextList } parent
            && int.TryParse(key[(separator + 1)..], out _)
            ? parent.Key
            : null;
    }

    private static bool IsIndexOf(string parent, string key) =>
        key.Length > parent.Length + 1
        && key.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && key[parent.Length] == ':'
        && int.TryParse(key[(parent.Length + 1)..], out _);

    /// <summary>
    /// A restart-gated setting is stored and reported but is not what the running server is using,
    /// so a restore that changed one has to say so — otherwise the operator reads the value back,
    /// sees what they expected, and concludes the restore worked when half of it has not started.
    /// </summary>
    private static void Note(SettingDescriptor? descriptor, List<string> notes)
    {
        if (descriptor is { RestartRequired: true })
        {
            string note = $"{descriptor.Label} is stored but needs a Server restart before it takes effect.";
            if (!notes.Contains(note, StringComparer.Ordinal))
            {
                notes.Add(note);
            }
        }
    }

    // ---- cameras -------------------------------------------------------------------------------

    private async Task<RestoreSection> RestoreCamerasAsync(
        ConfigBackupFile file, List<RestoreSkip> skips, CancellationToken cancellationToken)
    {
        int created = 0;
        int updated = 0;

        foreach (Camera camera in file.Cameras)
        {
            try
            {
                // Through the repository, never the collection: CreateAsync and UpdateAsync run the
                // validation that keeps a malformed camera out of the ingest pipeline, and a backup
                // is exactly the untrusted input that validation exists for — it can be hand-edited,
                // and it can have been written by a Server configured differently from this one.
                if (await _cameras.GetAsync(camera.Id, cancellationToken) is null)
                {
                    await _cameras.CreateAsync(camera, cancellationToken);
                    created++;
                }
                else if (await _cameras.UpdateAsync(camera, cancellationToken))
                {
                    updated++;
                }
            }
            catch (CameraValidationException ex)
            {
                // The registry's messages are already written for the person who caused them —
                // "this host's ffmpeg does not have the libsvtav1 encoder" names the problem and the
                // fix. Passing one through beats anything this layer could say about it.
                skips.Add(new RestoreSkip("cameras", camera.Id, ex.Message));
            }
        }

        return new RestoreSection("cameras", created, updated, CountSkips(skips, "cameras"), 0);
    }

    // ---- users ---------------------------------------------------------------------------------

    private async Task<RestoreSection> RestoreUsersAsync(
        ConfigBackupFile file,
        string? actorUserId,
        List<RestoreSkip> skips,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        List<User> existing = await _users.ListAsync(cancellationToken);
        IReadOnlyList<UserPlan> plans = PlanUsers(file.Users, existing, actorUserId, skips);

        int created = 0;
        int updated = 0;
        int signedOut = 0;

        foreach (UserPlan plan in plans)
        {
            if (plan.IsNew)
            {
                await _users.InsertRestoredAsync(
                    new User
                    {
                        Id = plan.Username,
                        DisplayName = plan.DisplayName,
                        PasswordHash = plan.PasswordHash!,
                        Role = plan.Role!.Value,
                        CreatedAt = plan.CreatedAt,
                    },
                    cancellationToken);
                created++;
                continue;
            }

            await _users.UpdateRestoredAsync(
                plan.Username, plan.DisplayName, plan.PasswordHash, plan.Role, cancellationToken);
            updated++;

            if (plan.PasswordHash is not null)
            {
                // The same reasoning UpdateUserRequest.SignOutAllSessions is written on: a session
                // established under the credential this is replacing was not authorised by whoever
                // owns the file, and its refresh token is good for weeks. The counter-case that made
                // SetPasswordAsync leave this to its caller — somebody changing their own password
                // on their own laptop — cannot arise here, because PlanUsers exempts the caller.
                await _refreshTokens.RevokeAllForUserAsync(plan.Username, cancellationToken);
                signedOut++;
            }
        }

        if (signedOut > 0)
        {
            notes.Add(signedOut == 1
                ? "1 account had its password changed and was signed out of every device."
                : $"{signedOut} accounts had their password changed and were signed out of every device.");
        }

        return new RestoreSection("users", created, updated, CountSkips(skips, "users"), 0);
    }

    /// <summary>
    /// Decides what happens to each account in the file, and refuses the two ways a restore could
    /// lock somebody out of the system it is meant to be recovering.
    ///
    /// <para><b>The caller's own role and password hash are never written.</b> A restore is one
    /// unattended button press, and one that demotes or re-credentials the person pressing it
    /// removes their ability to finish or reverse it. There is no recovery path from that:
    /// <c>AdminBootstrap</c> deliberately refuses to run once any account exists, so not even a
    /// redeploy with a bootstrap password gets it back. Their display name restores normally, and
    /// the withheld fields are reported rather than silently dropped.</para>
    ///
    /// <para><b>The projected set of Admins is never empty.</b> Computed before anything is written,
    /// as the existing Admins the file does not name plus the file's own Admins. If that is empty,
    /// <em>no</em> account changes at all — section-granular rather than per-row, because the
    /// invariant is over the set: refusing individual demotions would apply some arbitrary prefix of
    /// them and leave a state present in neither the file nor this Server. Settings, cameras and
    /// preferences still restore. In practice the guard above already makes this unreachable — the
    /// route is Admin-only and nothing is ever deleted, so the caller is an Admin who stays one —
    /// and it is kept as the written statement of the invariant rather than as live defence.</para>
    ///
    /// <para>Pure and internal, so both guards are testable without a database.</para>
    /// </summary>
    internal static IReadOnlyList<UserPlan> PlanUsers(
        IReadOnlyList<BackupUser> file,
        IReadOnlyList<User> existing,
        string? actorUserId,
        List<RestoreSkip> skips)
    {
        string? actor = actorUserId is null ? null : UserRepository.Normalize(actorUserId);
        Dictionary<string, User> byId = existing.ToDictionary(
            u => u.Id, StringComparer.OrdinalIgnoreCase);

        List<UserPlan> plans = [];
        List<RestoreSkip> pending = [];

        foreach (BackupUser entry in file)
        {
            string username = UserRepository.Normalize(entry.Username);

            if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsWhiteSpace))
            {
                pending.Add(new RestoreSkip("users", entry.Username,
                    "A username is required and cannot contain whitespace."));
                continue;
            }

            bool isNew = !byId.ContainsKey(username);
            bool isActor = actor is not null && username.Equals(actor, StringComparison.OrdinalIgnoreCase);

            // A hash that is not base64 makes every login attempt for this username throw out of
            // PasswordHasher, permanently, with no way back through the UI. Refusing the account is
            // recoverable; writing it is not. See UserRepository.IsPlausiblePasswordHash.
            bool hashUsable = UserRepository.IsPlausiblePasswordHash(entry.PasswordHash);
            if (isNew && !hashUsable)
            {
                pending.Add(new RestoreSkip("users", username,
                    "The password in the file is not one this Server can read, so the account was "
                    + "not created. Add it from Users & access instead."));
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? username : entry.DisplayName;

            if (isActor)
            {
                pending.Add(new RestoreSkip("users", username,
                    "Left your own account's role and password alone — a restore must not sign out "
                    + "the person running it. Change them from Users & access if you meant to."));
                plans.Add(new UserPlan(username, displayName, null, null, entry.CreatedAt, IsNew: false));
                continue;
            }

            if (!hashUsable)
            {
                pending.Add(new RestoreSkip("users", username,
                    "The password in the file is not one this Server can read, so this account kept "
                    + "the password it already had."));
            }

            // An unchanged hash is not a password change, so it is not written and does not sign
            // anybody out. Restoring the same file twice would otherwise revoke every session on the
            // second run as readily as the first, which is exactly the kind of surprise that makes
            // people stop trusting the button — and idempotence is the property this feature leans
            // on when it tells somebody to fix a skip and restore again.
            bool changed = hashUsable
                && !(byId.TryGetValue(username, out User? before)
                     && string.Equals(before.PasswordHash, entry.PasswordHash, StringComparison.Ordinal));

            plans.Add(new UserPlan(
                username,
                displayName,
                changed ? entry.PasswordHash : null,
                entry.Role,
                entry.CreatedAt,
                isNew));
        }

        // The set of Admins this plan would leave behind. Existing Admins the plan does not touch,
        // plus everyone it makes or keeps an Admin.
        HashSet<string> planned = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> named = plans.Select(p => p.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        planned.UnionWith(existing.Where(u => u.Role == Role.Admin && !named.Contains(u.Id)).Select(u => u.Id));
        planned.UnionWith(plans.Where(p => p.Role == Role.Admin || (p.Role is null && IsAdmin(byId, p.Username)))
            .Select(p => p.Username));

        if (planned.Count == 0)
        {
            skips.Add(new RestoreSkip("users", "every account",
                "Restoring the accounts in this file would leave this Server with no Admin, so none "
                + "of them were changed. Everything else in the file was restored."));
            return [];
        }

        skips.AddRange(pending);
        return plans;
    }

    private static bool IsAdmin(IReadOnlyDictionary<string, User> byId, string username) =>
        byId.TryGetValue(username, out User? user) && user.Role == Role.Admin;

    // ---- preferences ---------------------------------------------------------------------------

    private async Task<RestoreSection> RestorePreferencesAsync(
        ConfigBackupFile file, List<RestoreSkip> skips, CancellationToken cancellationToken)
    {
        int created = 0;
        int updated = 0;

        // Read after the accounts section, so an account the file just created counts as existing.
        HashSet<string> accounts = (await _users.ListAsync(cancellationToken))
            .Select(u => u.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (BackupPreferences entry in file.Preferences)
        {
            string userId = UserRepository.Normalize(entry.UserId);

            // Preferences are deleted along with their account precisely so a recycled username does
            // not inherit the last person's wall. Writing them for an account that does not exist
            // would recreate that, one restore ahead of time.
            if (!accounts.Contains(userId))
            {
                skips.Add(new RestoreSkip("preferences", entry.UserId,
                    $"No account '{userId}' exists on this Server, and the file does not create one."));
                continue;
            }

            try
            {
                bool inserted = await _preferences.RestoreAsync(
                    new UserPreferences
                    {
                        UserId = userId,
                        WallLayout = [.. entry.WallLayout.Select(t => new WallTile
                        {
                            CameraId = t.CameraId,
                            Column = t.Column,
                            Row = t.Row,
                            ColumnSpan = t.ColumnSpan,
                            RowSpan = t.RowSpan,
                        })],

                        NotificationsEnabled = entry.NotificationsEnabled,
                        Notifications = [.. entry.Notifications.Select(
                            r => new CameraNotificationRule
                            {
                                CameraId = r.CameraId,
                                Enabled = r.Enabled,
                                ObjectClasses = r.ObjectClasses,
                                SoundLabels = r.SoundLabels,
                                CooldownSeconds = r.CooldownSeconds,
                            })],
                    },
                    cancellationToken);

                if (inserted)
                {
                    created++;
                }
                else
                {
                    updated++;
                }
            }
            catch (PreferencesValidationException ex)
            {
                skips.Add(new RestoreSkip("preferences", entry.UserId, ex.Message));
            }
        }

        return new RestoreSection("preferences", created, updated, CountSkips(skips, "preferences"), 0);
    }

    private static int CountSkips(List<RestoreSkip> skips, string section) =>
        skips.Count(s => s.Section == section);
}

/// <summary>
/// What a restore intends to do to one account. Null <see cref="PasswordHash"/> or
/// <see cref="Role"/> means "leave that field as it is", which is how the caller's own account is
/// exempted from the two fields that could lock them out.
/// </summary>
internal sealed record UserPlan(
    string Username,
    string DisplayName,
    string? PasswordHash,
    Role? Role,
    DateTimeOffset CreatedAt,
    bool IsNew);
