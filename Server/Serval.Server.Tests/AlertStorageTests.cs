using Microsoft.Extensions.Options;
using Serval.Server.Alerts;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// Where an alert's preview clip and poster go.
///
/// The path carries the same two constraints a saved clip's does — the retention sweep deletes
/// inside <c>Media.Root/{cameraId}</c> and nowhere else, and the disk scan does not recurse — plus
/// one a clip does not have: these *are* pruned, on their own schedule, so the placement buys them a
/// lifetime of their own rather than exemption from having one.
/// </summary>
public class AlertStorageTests
{
    private static AlertStorage Storage(string root = "/srv/media", string alerts = "alerts") =>
        new(Options.Create(
            new ServerOptions { Media = new MediaOptions { Root = root, AlertsRoot = alerts } }));

    [Fact]
    public void Previews_live_beside_the_camera_directories_rather_than_inside_one()
    {
        // Inside a camera's directory they would be deleted with the footage they exist to outlive.
        Assert.Equal(Path.Combine("/srv/media", "alerts"), Storage().Root);
    }

    [Fact]
    public void An_absolute_alerts_root_points_previews_at_another_volume()
    {
        Assert.Equal("/mnt/ssd/alerts", Storage(alerts: "/mnt/ssd/alerts").Root);
    }

    [Fact]
    public void A_preview_and_its_poster_sit_flat_beside_each_other()
    {
        // Flat rather than a directory per alert, because DiskUsageScanner refuses to recurse: one
        // level down measures as zero bytes.
        AlertStorage storage = Storage();
        string id = "0129af24-e6ef-433f-8314-f68ded646ca6";

        Assert.Equal(Path.Combine("/srv/media", "alerts", $"{id}.mp4"), storage.VideoFor(id));
        Assert.Equal(Path.Combine("/srv/media", "alerts", $"{id}.jpg"), storage.PosterFor(id));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("id with spaces")]
    public void An_id_cannot_walk_out_of_the_alerts_directory(string id)
    {
        // These ids reach this class from a URL route as well as from the database, and a path built
        // from a route parameter is exactly the shape a traversal takes.
        string video = Storage().VideoFor(id);

        Assert.Equal(Path.Combine("/srv/media", "alerts"), Path.GetDirectoryName(video));
        Assert.DoesNotContain("..", video, StringComparison.Ordinal);
    }
}
