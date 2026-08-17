namespace Serval.Ai;

/// <summary>
/// A region of one camera's view to ignore, as a polygon in normalised coordinates.
///
/// Masking is not a threshold problem. A driveway camera that also sees the public road detects
/// every passing car perfectly correctly, and no confidence floor tells those apart from the one
/// pulling in — they are the same object, equally well seen. The only thing that separates them is
/// where they are, so that is what this expresses.
///
/// A polygon rather than a rectangle because the things worth excluding are shaped like roads and
/// property lines, which run diagonally across a view far more often than they run square to it.
/// </summary>
public sealed class DetectionMask
{
    /// <summary>For logs and the UI. Never matched on.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Vertices as a flat <c>x1, y1, x2, y2, …</c> list, each 0..1 as a fraction of the frame.
    ///
    /// Flat rather than a list of points so it survives configuration binding intact: this has to
    /// be expressible in appsettings.json, in an environment variable, and in a Mongo document,
    /// and a flat array of numbers is the one shape all three agree on.
    /// </summary>
    public double[] Points { get; set; } = [];

    /// <summary>
    /// Which detection classes this shape silences, or null for every one of them.
    ///
    /// Null and empty both mean <em>everything</em> — the only sensible reading of "ignore this
    /// shape" with nothing narrowing it.
    ///
    /// The case this exists for is a driveway meeting a public pavement. Cars and lorries on it are
    /// traffic; the person walking along it is what the camera is there for. A mask naming
    /// <c>car</c> and <c>truck</c> removes the first without the second, which no confidence floor
    /// and no second polygon can do.
    /// </summary>
    public string[]? Classes { get; set; }

    /// <summary>Fewer than three vertices cannot enclose anything, so it is ignored rather than
    /// treated as masking everything or nothing.
    ///
    /// Deliberately says nothing about <see cref="Classes"/>: a class filter has no bearing on
    /// whether a polygon encloses a region, and a mask naming a class the model never emits is a
    /// mask that matches nothing — which is legal, and the same thing an unknown name in a
    /// camera's class list already means.</summary>
    public bool IsUsable => Points.Length >= 6 && Points.Length % 2 == 0;

    /// <summary>
    /// Whether this mask can be applied before the model has run.
    ///
    /// A mask naming classes cannot: the label it needs does not exist until there is a detection to
    /// label. That is the whole difference in cost between the two forms — an unconditional mask
    /// stops <see cref="RegionPlanner"/> pointing the detector at the shape at all, and a
    /// class-scoped one can only throw the answer away afterwards.
    /// </summary>
    public bool AppliesToEverything => Classes is not { Length: > 0 };

    /// <summary>
    /// Whether this mask has anything to say about a detection of <paramref name="label"/>.
    ///
    /// Ordinal, like every other class comparison: these are matched against the model's own class
    /// strings, which are fixed ASCII from its label file.
    /// </summary>
    public bool Applies(string label) =>
        Classes is not { Length: > 0 } classes
        || Array.IndexOf(classes, label) >= 0;

    /// <summary>
    /// Whether a point falls inside, by ray casting.
    ///
    /// The point tested against this is a detection's <em>bottom centre</em>, not its middle —
    /// where the object meets the ground. A person walking behind a masked hedge has a box whose
    /// centre is well above it while their feet are firmly inside, and using the centre would let
    /// exactly the traffic a mask exists to remove back through.
    /// </summary>
    public bool Contains(double x, double y)
    {
        if (!IsUsable)
        {
            return false;
        }

        bool inside = false;
        int count = Points.Length / 2;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double xi = Points[i * 2], yi = Points[(i * 2) + 1];
            double xj = Points[j * 2], yj = Points[(j * 2) + 1];

            if (yi > y != yj > y && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Whether a detection stands inside, tested at the bottom centre of its box — where the object
    /// meets the ground. See <see cref="Contains(double, double)"/> for why that point and not the
    /// middle.
    ///
    /// <para>The one place that point is computed, because two callers need it and they must agree:
    /// <see cref="ObjectEventPolicy"/> drops a track for standing here, and
    /// <see cref="RegionPlanner"/> declines to spend a crop looking for it. A planner that skipped a
    /// box the policy would have kept is a subject silently never detected.</para>
    /// </summary>
    public bool Contains(BoundingBox box) =>
        Contains(box.X + (box.Width / 2), box.Y + box.Height);
}
