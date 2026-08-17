using MongoDB.Bson.Serialization.Attributes;
using Serval.Server.Alerts;

namespace Serval.Server.Preferences;

/// <summary>
/// What one person has arranged for themselves — the state that belongs to a user rather than to
/// the deployment, and that should follow them to another browser.
///
/// <para>Deliberately not part of <c>/api/settings</c>, which is the other kind of setting
/// entirely: that one is server-wide configuration, catalogue-validated, Admin-only to write, and
/// its write path ends in a configuration reload. None of that is true here. A Viewer must be able
/// to arrange their own wall, arranging it must not reload anything, and a wall layout is
/// structured state rather than a value with a default and a source.</para>
///
/// <para>Deliberately its own collection rather than fields on <see cref="Auth.User"/>. That
/// document holds the password hash and the lockout bookkeeping, and is written by the login path;
/// this one is written by whoever is looking at it, whenever they drag a tile. Keeping them apart
/// means a preference write can never touch an auth field, and the two can be read, cached and
/// deleted on their own terms.</para>
///
/// <para>This is the place to add the next one. A new preference is a property here, a line in
/// <see cref="PreferencesEndpoints"/>'s merge, and nothing else — which is why the write path
/// leaves unmentioned properties alone rather than replacing the document.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class UserPreferences
{
    /// <summary>The owning account's id — a lowercased username, exactly as <see cref="Auth.User.Id"/> holds it.</summary>
    [BsonId]
    public required string UserId { get; set; }

    /// <summary>
    /// Where this person has put their camera tiles. Empty means "never arranged one", which the
    /// App answers by packing the design's default — not by drawing an empty wall.
    /// </summary>
    public List<WallTile> WallLayout { get; set; } = [];

    /// <summary>
    /// Whether this person wants alerts pushed to them at all. The switch a person reaches for when
    /// they are going to be somewhere they cannot be interrupted, and the reason it is not simply
    /// "unsubscribe every device": turning it back on should not need the browser's permission
    /// prompt again on each one.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Which cameras notify this person, and about what. A camera with no rule here notifies about
    /// everything it raises an alert for, so an account that has never opened the screen behaves the
    /// way the deployment was configured to behave.
    /// </summary>
    public List<CameraNotificationRule> Notifications { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>What a user who has never saved anything reads as.</summary>
    public static UserPreferences Empty(string userId) => new() { UserId = userId };

    /// <summary>
    /// Whether an alert that has already been raised should be pushed to this person.
    ///
    /// <para>A camera with no rule notifies — the default is to be told, and an account that has
    /// never opened the notifications screen gets what the deployment was set up to send. Somebody
    /// who wants silence has to say so, which is the right way round for a security system.</para>
    /// </summary>
    public bool WantsNotifiedOf(string cameraId, AlertKind kind, string label)
    {
        if (!NotificationsEnabled)
        {
            return false;
        }

        return RuleFor(cameraId)?.Allows(kind, label) ?? true;
    }

    /// <summary>
    /// How long this person wants left between two notifications about the same thing on this
    /// camera, in seconds. Zero sends every one.
    /// </summary>
    /// <param name="deploymentDefault"><c>Serval:Push:CooldownSeconds</c>, which a rule saying
    /// nothing inherits.</param>
    public int CooldownSecondsFor(string cameraId, int deploymentDefault) =>
        RuleFor(cameraId)?.CooldownSeconds ?? deploymentDefault;

    /// <summary>
    /// This person's rule for one camera, or null where they have never set one.
    ///
    /// Shared by the two questions above rather than each finding its own, so they can never
    /// disagree about which rule they are reading. Linear over a list the repository caps at 256.
    /// </summary>
    public CameraNotificationRule? RuleFor(string cameraId) => Notifications.Find(
        r => string.Equals(r.CameraId, cameraId, StringComparison.Ordinal));
}

/// <summary>
/// One tile's place on the wall's 24-column grid.
///
/// The grid's rules live in the App (<c>WallGrid</c>) and are not restated here. The server checks
/// only what it must to keep the document sane — that a tile is on the grid and that no camera
/// appears twice — because re-implementing the packing and overlap rules would make a second source
/// of truth for them, and the App reconciles a layout against the cameras that exist on every read
/// anyway.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class WallTile
{
    public required string CameraId { get; set; }

    public int Column { get; set; }
    public int Row { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
}

/// <summary>
/// What one camera is allowed to interrupt one person about — the third and last link in the chain
/// that decides whether a notification happens.
///
/// <para>The first two links decide whether an alert exists at all:
/// <c>Serval:Ai:Detection:AlertClasses</c> is the deployment's answer, a camera's
/// <c>CameraDetectionTuning.AlertClasses</c> overrides it, and <c>CameraAiOptions.For</c> resolves
/// the two. That happens once, in the detection loop, and produces a row in the shared queue. This
/// link is applied afterwards and per person, which is why it can only ever <b>narrow</b>: a class
/// the camera does not alert on produces no row, so no amount of asking for it here conjures one.
/// The App draws the camera's effective alert classes as the menu, so what is on offer is what can
/// actually arrive.</para>
///
/// <para>Selecting something outside that set is stored rather than rejected. The camera's list can
/// change, and a choice that starts working the day an admin adds a class is better than a
/// validation error against a set that was only true this morning.</para>
///
/// <para><b>Null is not an empty list.</b> Null inherits — every class this camera alerts on —
/// matching the idiom the per-camera tuning objects already use for links one and two. An empty
/// list means the person chose nothing and wants nothing, which is a different statement and the
/// one that makes "mute this kind of alert without muting the camera" expressible.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CameraNotificationRule
{
    public required string CameraId { get; set; }

    /// <summary>Whether this camera may notify this person at all. The coarse switch, above the lists.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which detected objects notify, spelled as the detector spells them. Null inherits everything
    /// the camera alerts on.
    /// </summary>
    public string[]? ObjectClasses { get; set; }

    /// <summary>
    /// Which sounds notify, spelled as the audio model spells them. Null inherits everything the
    /// camera alerts on. Separate from <see cref="ObjectClasses"/> because the two come from
    /// different models with different vocabularies, and one list holding both would make
    /// "everything except sounds" impossible to say.
    /// </summary>
    public string[]? SoundLabels { get; set; }

    /// <summary>
    /// How long to leave this person alone after this camera has interrupted them about one thing,
    /// before it may interrupt them about that same thing again. Null inherits
    /// <c>Serval:Push:CooldownSeconds</c>; zero sends every alert this camera raises.
    ///
    /// <para>The one field here that narrows by <em>rate</em> rather than by kind, and the only one
    /// whose effect is invisible in the queue: a held notification still leaves its row, its clip
    /// and its unread dot. Somebody who wants the record but not the interruption is asking for
    /// this rather than for an empty <see cref="ObjectClasses"/>.</para>
    ///
    /// <para>Zero is a choice and null is the absence of one, the same distinction the lists above
    /// draw. Somebody who has decided this camera should always reach them wants that decision to
    /// survive an admin changing the deployment's default, and only a stored zero says so.</para>
    /// </summary>
    public int? CooldownSeconds { get; set; }

    /// <summary>
    /// Whether an alert of this kind and label should reach the person this rule belongs to.
    ///
    /// The alert's existence already proves the label passed the first two links, so this asks the
    /// only remaining question rather than re-resolving the chain.
    /// </summary>
    public bool Allows(AlertKind kind, string label)
    {
        if (!Enabled)
        {
            return false;
        }

        string[]? allowed = kind == AlertKind.Sound ? SoundLabels : ObjectClasses;

        return allowed is null || allowed.Contains(label, StringComparer.OrdinalIgnoreCase);
    }
}
