using MongoDB.Bson;
using Serval.Server.Auth;
using Serval.Server.Clips;
using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// What a clip route accepts and who may change a clip afterwards.
///
/// Every one of these is a decision the App also has to make — the trim UI enforces the same cap
/// and hides the same buttons — so the two can disagree. These pin the side that is authoritative.
/// </summary>
public class ClipRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static RecordingSegment Segment(string init, int index) => new()
    {
        Id = ObjectId.GenerateNewId(),
        CameraId = "front-door",
        FileName = $"seg-{index:D5}.m4s",
        InitFileName = init,
        StartedAt = Noon.AddSeconds(index * 4),
        DurationSeconds = 4,
    };

    private static SavedClip Clip(string savedBy = "jeremiah") => new()
    {
        Id = ObjectId.GenerateNewId(),
        CameraId = "front-door",
        CameraName = "Front door",
        Name = "Parcel behind the planter",
        SavedBy = savedBy,
        From = Noon,
        To = Noon.AddSeconds(55),
        SavedAt = Noon,
    };

    [Fact]
    public void A_sensible_range_is_accepted()
    {
        Assert.Null(ClipRules.RejectSave("front-door", "Parcel", Noon, Noon.AddSeconds(55), maxMinutes: 30));
    }

    [Fact]
    public void A_range_over_the_cap_is_refused_and_says_how_long_it_was()
    {
        string? refusal = ClipRules.RejectSave(
            "front-door", "Long one", Noon, Noon.AddMinutes(31), maxMinutes: 30);

        Assert.NotNull(refusal);
        Assert.Contains("31", refusal);
        Assert.Contains("30 minutes", refusal);
    }

    [Fact]
    public void The_cap_itself_is_allowed()
    {
        // Exactly the maximum is a legal clip. An off-by-one here is a range the trim UI offers and
        // the server then refuses, which reads as a bug in the trimmer.
        Assert.Null(ClipRules.RejectSave("front-door", "Half hour", Noon, Noon.AddMinutes(30), maxMinutes: 30));
    }

    [Fact]
    public void A_backwards_or_empty_range_is_refused()
    {
        Assert.NotNull(ClipRules.RejectSave("front-door", "Backwards", Noon.AddMinutes(1), Noon, 30));
        Assert.NotNull(ClipRules.RejectSave("front-door", "Instant", Noon, Noon, 30));
    }

    [Fact]
    public void A_clip_needs_a_name_that_is_not_only_spaces()
    {
        Assert.NotNull(ClipRules.RejectSave("front-door", null, Noon, Noon.AddSeconds(55), 30));
        Assert.NotNull(ClipRules.RejectSave("front-door", "   ", Noon, Noon.AddSeconds(55), 30));
    }

    [Fact]
    public void A_name_is_measured_after_trimming()
    {
        string name = new string(' ', 50) + new string('x', ClipRules.MaxNameLength) + new string(' ', 50);

        Assert.Null(ClipRules.RejectSave("front-door", name, Noon, Noon.AddSeconds(55), 30));
        Assert.NotNull(ClipRules.RejectSave(
            "front-door", new string('x', ClipRules.MaxNameLength + 1), Noon, Noon.AddSeconds(55), 30));
    }

    [Fact]
    public void A_camera_id_that_could_escape_its_directory_is_refused()
    {
        // The same guard the media routes use. A clip's files are named for the clip rather than
        // the camera, so this cannot traverse today — but the id reaches the recording index and
        // the camera directory, and a rule that only holds by accident is one refactor from not.
        Assert.NotNull(ClipRules.RejectSave("../etc", "Escape", Noon, Noon.AddSeconds(55), 30));
        Assert.NotNull(ClipRules.RejectSave("", "Empty", Noon, Noon.AddSeconds(55), 30));
    }

    [Fact]
    public void A_range_with_no_footage_is_refused()
    {
        Assert.NotNull(ClipRules.RejectSegments([]));
    }

    [Fact]
    public void A_range_inside_one_recording_session_is_accepted()
    {
        Assert.Null(ClipRules.RejectSegments([Segment("init-a.mp4", 0), Segment("init-a.mp4", 1)]));
    }

    [Fact]
    public void A_range_crossing_a_recording_restart_is_refused_rather_than_truncated()
    {
        // The streaming export truncates at the boundary and says so in a header. A saved clip
        // cannot: nobody is watching the response, and a clip silently half the length asked for
        // would be discovered weeks later by the person who needed the other half.
        string? refusal = ClipRules.RejectSegments(
            [Segment("init-a.mp4", 0), Segment("init-a.mp4", 1), Segment("init-b.mp4", 2)]);

        Assert.NotNull(refusal);
        Assert.Contains("restarted", refusal);
    }

    [Fact]
    public void The_person_who_saved_a_clip_may_edit_it()
    {
        Assert.True(ClipRules.MayEdit("jeremiah", Role.Viewer, Clip(savedBy: "jeremiah")));
    }

    [Fact]
    public void Somebody_else_may_not()
    {
        Assert.False(ClipRules.MayEdit("guest", Role.Viewer, Clip(savedBy: "jeremiah")));
    }

    [Fact]
    public void An_admin_may_edit_anybody_s_clip()
    {
        // Otherwise a clip saved by an account that has since been deleted could never be removed.
        Assert.True(ClipRules.MayEdit("someone-else", Role.Admin, Clip(savedBy: "jeremiah")));
    }

    [Fact]
    public void An_unauthenticated_principal_may_not_edit_anything()
    {
        // GetUserId returns null when the claim is missing, and null must not match the "unknown"
        // a clip saved without one is stamped with.
        Assert.False(ClipRules.MayEdit(null, Role.Viewer, Clip(savedBy: "unknown")));
    }

    [Fact]
    public void Usernames_differing_only_in_case_are_different_people()
    {
        // Accounts are lowercased at creation (see User), so a mixed-case match here would only
        // ever come from a token that did not go through that path.
        Assert.False(ClipRules.MayEdit("Jeremiah", Role.Viewer, Clip(savedBy: "jeremiah")));
    }

    [Fact]
    public void A_downloaded_clip_is_named_after_the_clip()
    {
        SavedClip clip = Clip();
        Assert.Equal("Parcel behind the planter.mp4", ClipRules.FileNameFor(clip));
    }

    [Fact]
    public void A_name_a_filesystem_would_refuse_is_made_safe()
    {
        SavedClip clip = Clip();
        clip.Name = "Front/door: 4pm";

        string fileName = ClipRules.FileNameFor(clip);

        Assert.DoesNotContain('/', fileName);
        Assert.EndsWith(".mp4", fileName);
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_falls_back_to_camera_and_time()
    {
        SavedClip clip = Clip();
        clip.Name = "///";

        Assert.Equal("front-door-20260809-120000.mp4", ClipRules.FileNameFor(clip));
    }
}
