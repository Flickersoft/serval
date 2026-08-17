using System.Text.Json;
using System.Text.Json.Serialization;
using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Preferences;

namespace Serval.Server.Backup;

/// <summary>
/// A whole configuration, in the shape it is written to a file and read back from one.
///
/// <para><b>Configuration only.</b> Recorded footage, detections, utterances, sounds and telemetry
/// are not here and never will be: they are enormous, they are what the configuration produces, and
/// the volume they live on is the operator's to back up however they back up a disk. This file is
/// for the part no disk image recovers — what someone typed in.</para>
///
/// <para><b>Cameras are the registry's own type, serialized directly</b> rather than copied into a
/// parallel record, so a camera object here is byte-identical to a <c>POST /api/cameras</c> body:
/// already documented, already hand-editable, and unable to drift from what the registry accepts.
/// Users and preferences do get their own records, because their live types carry things a file must
/// not — lockout bookkeeping on one, a mutable BSON id on the other.</para>
///
/// <para>Property order is the record's declaration order, and that is load-bearing:
/// <see cref="Kind"/> and <see cref="Warning"/> are first so that anyone who
/// opens the file sees what it is and what it holds before they see any of it.</para>
/// </summary>
/// <param name="Kind">Always <see cref="FileKind"/>. What makes "this is not a Serval backup" a
/// different message from "this is a Serval backup this Server cannot read".</param>
/// <param name="Warning">Always <see cref="SecretWarning"/>. Written for a human, ignored on read.</param>
/// <param name="Settings">
/// The stored overlay exactly as <c>ServerSettingsDocument.Values</c> holds it: colon-form
/// configuration paths to string values, with list entries as indexed children
/// (<c>Serval:Ai:Sound:AlertLabels:0</c>). Only what a user has actually overridden — a setting
/// left at its deployment or built-in value is absent, because that is what "not overridden"
/// <em>is</em>. Restoring onto a differently-deployed Server therefore restores the choices, not
/// the resulting values, which is the right answer when the two machines have different disks.
/// </param>
public sealed record ConfigBackupFile(
    string Kind,
    string Warning,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string? CreatedOn,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<Camera> Cameras,
    IReadOnlyList<BackupUser> Users,
    IReadOnlyList<BackupPreferences> Preferences)
{
    /// <summary>What every backup file says it is.</summary>
    public const string FileKind = "serval.config-backup";

    /// <summary>
    /// The warning carried inside the file itself, because the file outlives the dialog that was
    /// shown when it was downloaded. Somebody finding this on a drive in two years should be able
    /// to tell what they have found without knowing anything about Serval.
    ///
    /// <para>Written without apostrophes on purpose. System.Text.Json escapes <c>'</c> to
    /// <c>'</c> by default, and the only way to stop it is
    /// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> — which relaxes the escaping of
    /// <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> too, on a document that carries attacker-influenced
    /// strings like camera names. Rewording one sentence is a much smaller price than that, and the
    /// sentence has to be legible to be worth carrying at all.</para>
    /// </summary>
    public const string SecretWarning =
        "THIS FILE CONTAINS SECRETS IN PLAIN TEXT. It holds the ONVIF password of every camera, "
        + "any user:password inside a stream URL, and the password hash of every account. Anyone "
        + "who reads it can sign in to your cameras, and can attack your account passwords offline "
        + "where no lockout applies. Store it where you would store those passwords: not e-mail, "
        + "not a shared drive, not a repository. It contains no recorded footage, no detections "
        + "and no telemetry.";

    /// <summary>
    /// How the file is written and read.
    ///
    /// <para>Camel case to match every other payload the Server produces, so a camera here reads
    /// the same as a camera from <c>GET /api/cameras</c>. Indented because the warning above is
    /// meant to be read by a person who opened the file in whatever they had to hand, and a single
    /// line of minified JSON hides it.</para>
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>serval-config-20260808-140311.json</c>. Sortable, second-resolution, and local time —
    /// the person choosing between two of these is choosing by when they took them.
    /// </summary>
    public static string FileNameFor(DateTimeOffset at) =>
        $"serval-config-{at.ToLocalTime():yyyyMMdd-HHmmss}.json";
}

/// <summary>
/// One account, as a backup carries it.
///
/// <para>Its own record rather than <see cref="User"/> because two of that type's fields must not
/// travel. <c>FailedLoginAttempts</c> and <c>LockedUntil</c> are the state of an attack against one
/// machine at one moment, not configuration: restoring a <c>LockedUntil</c> would lock an account
/// out for half an hour over a typo that happened somewhere else last month, and restoring a
/// failure count of four would silently arm a lockout on the next one.</para>
/// </summary>
/// <param name="PasswordHash">
/// PBKDF2 output from <c>PasswordHasher&lt;User&gt;</c>, verbatim. Carried so a restore actually
/// restores the ability to sign in — an account whose password has to be reset by hand afterwards
/// is a list of usernames, not a backup. This is the field the warning at the top of the file is
/// mostly about.
/// </param>
public sealed record BackupUser(
    string Username,
    string DisplayName,
    string PasswordHash,
    Role Role,
    DateTimeOffset CreatedAt);

/// <summary>
/// One account's preferences. Uses <see cref="WallTilePayload"/> and
/// <see cref="CameraNotificationRulePayload"/> — the same shapes <c>/api/preferences</c> speaks —
/// rather than the stored types, so the file and the API describe a wall and a set of notification
/// rules identically.
///
/// <para>Push <em>subscriptions</em> are deliberately not here. They belong to a browser rather
/// than to a person: they expire on their own, they are re-registered on every launch, and one
/// restored onto another machine would name a device that deployment has never spoken to. The
/// rules are configuration; the endpoints are not.</para>
/// </summary>
public sealed record BackupPreferences(
    string UserId,
    IReadOnlyList<WallTilePayload> WallLayout,
    bool NotificationsEnabled,
    IReadOnlyList<CameraNotificationRulePayload> Notifications);
