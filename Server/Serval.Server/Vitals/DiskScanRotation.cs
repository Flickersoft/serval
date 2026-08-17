namespace Serval.Server.Vitals;

/// <summary>What one disk tick does.</summary>
/// <param name="Walk">The directories to measure, in the order to measure them.</param>
/// <param name="Cursor">Where the rotation stands afterwards.</param>
/// <param name="CatchingUp">
/// Whether this tick is reaching directories nothing has walked yet, rather than refreshing one
/// already walked. Only the caller's log line reads it; the work is the same either way.
/// </param>
internal readonly record struct DiskScanTick(
    IReadOnlyList<DiskScanTarget> Walk, int Cursor, bool CatchingUp);

/// <summary>
/// Which media directories a disk tick walks, and where that leaves the rotation.
///
/// <para>Pure, and split from <see cref="SystemStatsWorker"/> for the reason the detection policy is
/// split from the detector: the walk itself needs a filesystem and a camera registry, while the
/// decision about <em>what</em> to walk is arithmetic over a list of names — and it is the part that
/// was wrong. Everything here is exercisable with a handful of strings.</para>
/// </summary>
internal static class DiskScanRotation
{
    /// <summary>
    /// The targets to measure this tick, and the cursor to carry into the next one.
    ///
    /// <para><b>Completeness before freshness.</b> Anything not yet walked is walked, all of it,
    /// this tick: a directory with no figure is absent from the breakdown, so the per-directory rows
    /// stop adding up to the volume total — the exact fault the startup sweep exists to avoid, and
    /// one that arrives again with every camera registered after it. A figure one interval out of
    /// date has no such problem, so once everything has been walked the rotation goes back to a
    /// single directory per tick.</para>
    ///
    /// <para><b>Walked, not measured.</b> The question is whether this directory has had its turn,
    /// not whether the turn produced a number — a directory that cannot be read produces none, and
    /// asking about the result would put it back at the head of every tick forever while every other
    /// figure went stale behind it.</para>
    ///
    /// <para>The cursor only advances on a rotation tick. A catch-up tick is not the rotation's turn
    /// and must not consume it, or the directory that was next would be skipped.</para>
    /// </summary>
    /// <param name="targets">Every directory that should have a figure, in registry order.</param>
    /// <param name="walked">Whether this key has been measured at least once, successfully or not.</param>
    /// <param name="cursor">Where the rotation stood after the last tick.</param>
    public static DiskScanTick Next(
        IReadOnlyList<DiskScanTarget> targets, Func<string, bool> walked, int cursor)
    {
        if (targets.Count == 0)
        {
            return new DiskScanTick([], cursor, CatchingUp: false);
        }

        List<DiskScanTarget> missing = [.. targets.Where(t => !walked(t.Key))];
        if (missing.Count > 0)
        {
            return new DiskScanTick(missing, cursor, CatchingUp: true);
        }

        // Modulo rather than a reset, so a camera added or removed between sweeps shifts the
        // rotation by one instead of restarting it.
        return new DiskScanTick(
            [targets[cursor % targets.Count]], (cursor + 1) % targets.Count, CatchingUp: false);
    }
}
