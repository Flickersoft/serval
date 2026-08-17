using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// Which volume the media root sits on. Small enough to look obvious and wrong in two ways that
/// are not — first-match instead of longest-match, and string-prefix instead of component-prefix —
/// which is the whole reason it is a function with tests rather than a line inside the collector.
/// </summary>
public class MountPointsTests
{
    [Fact]
    public void The_longest_containing_mount_wins_not_the_first()
    {
        // "/" contains everything, so a first-match implementation always answers "/" and the
        // media volume's own capacity is never reported.
        Assert.Equal("/media", MountPoints.Best("/media/front-door", ["/", "/media"]));
        Assert.Equal("/media", MountPoints.Best("/media/front-door", ["/media", "/"]));
    }

    [Fact]
    public void A_path_on_no_other_volume_falls_to_the_root()
    {
        Assert.Equal("/", MountPoints.Best("/srv/serval/media", ["/", "/media"]));
    }

    /// <summary>
    /// The bug this function exists to prevent. "/mediafoo" starts with the string "/media" and is
    /// emphatically not on that volume, so a StartsWith would report someone else's free space as
    /// Serval's.
    /// </summary>
    [Fact]
    public void Matching_is_on_path_components_not_on_the_string()
    {
        Assert.Equal("/", MountPoints.Best("/mediafoo", ["/", "/media"]));
        Assert.Equal("/", MountPoints.Best("/media-backup/x", ["/", "/media"]));
    }

    [Fact]
    public void A_mount_point_matches_itself()
    {
        Assert.Equal("/media", MountPoints.Best("/media", ["/", "/media"]));
        Assert.Equal("/media", MountPoints.Best("/media/", ["/", "/media/"]));
    }

    [Fact]
    public void Deeper_nesting_still_picks_the_deepest_mount()
    {
        Assert.Equal(
            "/mnt/pool/serval",
            MountPoints.Best("/mnt/pool/serval/media/front-door", ["/", "/mnt", "/mnt/pool/serval"]));
    }

    [Fact]
    public void Nothing_to_match_against_is_null()
    {
        Assert.Null(MountPoints.Best("/media", []));
        Assert.Null(MountPoints.Best("", ["/"]));
        Assert.Null(MountPoints.Best("   ", ["/"]));
    }

    [Fact]
    public void Blank_mount_names_are_skipped_rather_than_matching_everything()
    {
        Assert.Equal("/", MountPoints.Best("/media", ["", "   ", "/"]));
    }
}
