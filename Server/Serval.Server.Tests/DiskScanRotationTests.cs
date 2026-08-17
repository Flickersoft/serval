using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// Which media directory the slow sweep walks next.
///
/// The rule under test is that a directory with no figure at all is not the same thing as one whose
/// figure is old. Getting that wrong is invisible in every way that matters — the page renders, the
/// numbers it does show are correct, and the volume total is correct — and the only symptom is rows
/// that are not there: six cameras recording and one of them listed.
/// </summary>
public class DiskScanRotationTests
{
    private static DiskScanTarget Target(string key) => new(key, key, $"/media/{key}");

    private static readonly IReadOnlyList<DiskScanTarget> SixCamerasAndThreeOthers =
    [
        Target("frontgate"), Target("backyard"), Target("trailcam"),
        Target("garden"), Target("livingroom"), Target("patio"),
        Target("conversations"), Target("clips"), Target("alerts"),
    ];

    private static Func<string, bool> Walked(params string[] keys) =>
        key => keys.Contains(key, StringComparer.Ordinal);

    private static string[] AllOf(IReadOnlyList<DiskScanTarget> targets) =>
        [.. targets.Select(t => t.Key)];

    /// <summary>
    /// The reported fault, as a single call.
    ///
    /// A fresh install has no cameras when the startup sweep runs, so it measures the three
    /// directories that belong to none. Six cameras registered a minute later were then measured one
    /// per interval — a quarter of an hour each, in a rotation that put the other three first if the
    /// cursor happened to be there.
    /// </summary>
    [Fact]
    public void Cameras_registered_after_the_startup_sweep_are_all_measured_at_once()
    {
        DiskScanTick tick = DiskScanRotation.Next(
            SixCamerasAndThreeOthers,
            Walked("conversations", "clips", "alerts"),
            cursor: 0);

        Assert.Equal(
            ["frontgate", "backyard", "trailcam", "garden", "livingroom", "patio"],
            tick.Walk.Select(t => t.Key));
        Assert.True(tick.CatchingUp);
    }

    /// <summary>
    /// Catching up is not the rotation's turn. Consuming one would skip whichever directory was
    /// next, and that one would then be the stale row nobody could explain.
    /// </summary>
    [Fact]
    public void Catching_up_leaves_the_rotation_where_it_stood()
    {
        DiskScanTick tick = DiskScanRotation.Next(
            SixCamerasAndThreeOthers, Walked("frontgate"), cursor: 4);

        Assert.Equal(4, tick.Cursor);
    }

    [Fact]
    public void With_every_figure_taken_it_walks_one_directory_and_moves_on()
    {
        DiskScanTick tick = DiskScanRotation.Next(
            SixCamerasAndThreeOthers,
            Walked(AllOf(SixCamerasAndThreeOthers)),
            cursor: 2);

        Assert.Equal("trailcam", Assert.Single(tick.Walk).Key);
        Assert.Equal(3, tick.Cursor);
        Assert.False(tick.CatchingUp);
    }

    /// <summary>
    /// The rotation wraps rather than resetting, so a camera deleted mid-cycle shifts it by one
    /// instead of sending it back to the start and re-walking directories it has just done.
    /// </summary>
    [Fact]
    public void The_rotation_wraps_at_the_end_of_the_list()
    {
        DiskScanTick tick = DiskScanRotation.Next(
            SixCamerasAndThreeOthers,
            Walked(AllOf(SixCamerasAndThreeOthers)),
            cursor: 8);

        Assert.Equal("alerts", Assert.Single(tick.Walk).Key);
        Assert.Equal(0, tick.Cursor);
    }

    /// <summary>A server with no cameras and no media root yet still ticks; it just has nothing to
    /// walk, and must not divide by a target count of zero to find that out.</summary>
    [Fact]
    public void Nothing_to_measure_is_not_an_error()
    {
        DiskScanTick tick = DiskScanRotation.Next([], Walked(), cursor: 3);

        Assert.Empty(tick.Walk);
        Assert.Equal(3, tick.Cursor);
    }
}
