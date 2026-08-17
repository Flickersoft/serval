namespace Serval.Server.Backup;

/// <summary>
/// What a restore actually did.
///
/// <para>A restore is best-effort, not all-or-nothing, so this is not a formality — it is the half
/// of the feature that makes the other half honest. One camera can be refused because this host's
/// ffmpeg cannot encode what it asks for, one setting because it is environment-only here, one
/// account because the file would have demoted the person running the restore. Everything else
/// still lands, and the operator is told exactly what did not, in words they can act on.</para>
///
/// <para>See <see cref="ConfigRestoreService"/> for why best-effort is the right shape.</para>
/// </summary>
/// <param name="FileCreatedAt">When the file was taken, echoed back so a mistaken restore of an
/// old backup is visible in the result rather than only in the dialog that preceded it.</param>
/// <param name="Sections">
/// One row per section, in the order they were applied. Section names are labels, not an enum, so a
/// later Server can back up something new without the App needing to know what it is.
/// </param>
/// <param name="Notes">
/// Things that happened and are worth saying, but are not failures: accounts signed out, settings
/// stored that need a restart before they mean anything.
/// </param>
public sealed record ConfigRestoreResult(
    DateTimeOffset RestoredAt,
    DateTimeOffset FileCreatedAt,
    string? FileCreatedBy,
    IReadOnlyList<RestoreSection> Sections,
    IReadOnlyList<RestoreSkip> Skipped,
    IReadOnlyList<string> Notes);

/// <summary>
/// What one section of the file came to.
/// </summary>
/// <param name="Cleared">
/// Entries removed rather than written — which happens only to the stale tail of a list setting the
/// file has shortened. Counted separately from <paramref name="Updated"/> so "5 settings written"
/// and "5 written, 2 stale list entries removed" are distinguishable, and so the one place a
/// merge-only restore does delete something is visible rather than implied.
/// </param>
public sealed record RestoreSection(
    string Name,
    int Created,
    int Updated,
    int Skipped,
    int Cleared);

/// <summary>
/// One thing the file asked for that was not done, and why.
///
/// <para><paramref name="Reason"/> is shown to the operator verbatim. Every producer of one is
/// expected to write a sentence a person can act on — which mostly means passing through the
/// message a validator already wrote, since those are written that way for the settings and camera
/// forms already.</para>
/// </summary>
public sealed record RestoreSkip(string Section, string Item, string Reason);
