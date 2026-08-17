using System.Text.Json;
using Serval.Server.Preferences;

namespace Serval.Server.Tests;

/// <summary>
/// What the server insists on before a wall layout goes in the database.
///
/// The line drawn here matters as much as the checks: the grid's packing and overlap rules live in
/// the App's <c>WallGrid</c> and are deliberately <em>not</em> re-implemented, because a second
/// copy is a second thing to keep in step and the App reconciles a stored layout against the
/// cameras that exist on every read. What is checked is only what would make the stored document
/// itself nonsense — a tile off the grid, a tile with no area, a camera claiming two places, or a
/// list long enough to be a storage problem.
/// </summary>
public class UserPreferencesValidationTests
{
    private static WallTile Tile(
        string cameraId, int column = 0, int row = 0, int columnSpan = 6, int rowSpan = 2) =>
        new()
        {
            CameraId = cameraId,
            Column = column,
            Row = row,
            ColumnSpan = columnSpan,
            RowSpan = rowSpan,
        };

    [Fact]
    public void An_ordinary_layout_is_accepted()
    {
        UserPreferencesRepository.Validate(
            [Tile("front-door"), Tile("driveway", column: 6), Tile("back-yard", column: 12)]);
    }

    [Fact]
    public void An_empty_layout_is_accepted()
    {
        // Not a rejection: an empty array is how "reset my wall" is expressed, and it has to be
        // distinguishable from omitting the property entirely.
        UserPreferencesRepository.Validate([]);
    }

    [Fact]
    public void A_tile_running_past_the_right_edge_is_refused()
    {
        PreferencesValidationException error = Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("front-door", column: 20, columnSpan: 6)]));

        Assert.Contains("front-door", error.Message);
    }

    [Fact]
    public void A_tile_exactly_filling_the_grid_is_accepted()
    {
        // The boundary the check above must not overshoot: 0 + 24 == 24 is the full-width tile the
        // design actually offers, not an overflow.
        UserPreferencesRepository.Validate([Tile("front-door", column: 0, columnSpan: 24)]);
    }

    [Fact]
    public void A_negative_origin_is_refused()
    {
        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("front-door", column: -1)]));

        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("front-door", row: -1)]));
    }

    [Fact]
    public void A_tile_with_no_area_is_refused()
    {
        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("front-door", columnSpan: 0)]));

        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("front-door", rowSpan: 0)]));
    }

    [Fact]
    public void A_camera_appearing_twice_is_refused()
    {
        // One camera cannot be in two places, and a layout saying so would draw the same feed
        // twice while some other camera has no tile at all.
        PreferencesValidationException error = Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate(
                [Tile("front-door"), Tile("front-door", column: 6)]));

        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void A_tile_naming_no_camera_is_refused()
    {
        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("")]));

        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate([Tile("   ")]));
    }

    [Fact]
    public void A_layout_longer_than_the_cap_is_refused()
    {
        // The list arrives from an authenticated client and nothing else bounds its length.
        List<WallTile> tooMany =
            [.. Enumerable.Range(0, UserPreferencesRepository.MaxTiles + 1)
                .Select(i => Tile($"camera-{i}", row: i))];

        Assert.Throws<PreferencesValidationException>(
            () => UserPreferencesRepository.Validate(tooMany));
    }

    [Fact]
    public void A_layout_exactly_at_the_cap_is_accepted()
    {
        List<WallTile> atCap =
            [.. Enumerable.Range(0, UserPreferencesRepository.MaxTiles)
                .Select(i => Tile($"camera-{i}", row: i))];

        UserPreferencesRepository.Validate(atCap);
    }

    [Fact]
    public void Overlapping_tiles_are_deliberately_not_refused()
    {
        // Pinned because it is a decision, not an oversight. Overlap is the App's rule, checked
        // there on every drag; re-checking it here would put the grid's geometry in two places.
        // A layout that overlaps renders oddly and is fixed by dragging — it is not corruption.
        UserPreferencesRepository.Validate(
            [Tile("front-door", column: 0), Tile("driveway", column: 3)]);
    }

    [Fact]
    public void An_unbounded_row_is_accepted()
    {
        // Rows grow downward without limit on the App's grid, so there is no upper row to check.
        UserPreferencesRepository.Validate([Tile("front-door", row: 10_000)]);
    }

    // ------------------------------------------------------------ merge contract

    /// <summary>
    /// The property that lets a preference be added while an older tab is still open: a body that
    /// does not mention one is deserialised as null, which the endpoint reads as "leave it alone".
    /// Absent and empty must not collapse into each other — empty is how each is cleared.
    /// </summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_body_mentioning_no_preference_leaves_everything_alone()
    {
        // What a client sends when it is changing something this build does not have. The endpoint
        // answers it with the stored document rather than treating it as an error.
        PreferencesRequest? request = JsonSerializer.Deserialize<PreferencesRequest>(
            """{"somethingThisBuildHasNeverHeardOf":true}""", Web);

        Assert.NotNull(request);
        Assert.Null(request.WallLayout);
    }

    [Fact]
    public void An_empty_wall_layout_is_not_the_same_as_omitting_it()
    {
        // The array clears the saved arrangement so the wall falls back to the default packing;
        // omitting it keeps whatever is stored. Collapsing the two would make a client that never
        // mentions the wall erase it.
        PreferencesRequest? request = JsonSerializer.Deserialize<PreferencesRequest>(
            """{"wallLayout":[]}""", Web);

        Assert.NotNull(request);
        Assert.NotNull(request.WallLayout);
        Assert.Empty(request.WallLayout);
    }

    /// <summary>
    /// The same absent-versus-explicit distinction one level down, on a rule. Null inherits the
    /// deployment's cooldown and zero notifies every time; a client that predates the field sends
    /// neither and must land on inherit rather than on "always".
    /// </summary>
    [Fact]
    public void An_omitted_cooldown_is_not_the_same_as_zero()
    {
        PreferencesRequest? omitted = JsonSerializer.Deserialize<PreferencesRequest>(
            """{"notifications":[{"cameraId":"front-door","enabled":true}]}""", Web);

        PreferencesRequest? zero = JsonSerializer.Deserialize<PreferencesRequest>(
            """{"notifications":[{"cameraId":"front-door","enabled":true,"cooldownSeconds":0}]}""",
            Web);

        Assert.Null(omitted?.Notifications?[0].CooldownSeconds);
        Assert.Equal(0, zero?.Notifications?[0].CooldownSeconds);
    }
}
