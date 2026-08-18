namespace Serval.Ai;

/// <summary>
/// Folds together detections that two overlapping regions made of the same object.
///
/// <para>Regions in one frame may overlap — a sweep run whole has tiles that share most of their width,
/// and a motion crop routinely sits inside the tile that would have found the same thing. Each is a
/// separate inference, so a subject in the shared area comes back once per region.</para>
///
/// <para><see cref="ObjectTracker"/> cannot absorb that: association is one detection to one track, so
/// the second copy matches nothing and starts a track of its own. Both then arrive on every frame, both
/// confirm, and one object becomes two episodes and two alerts. Suppressing here rather than inside the
/// association rules keeps it a property of how the frame was examined, which is what it is.</para>
///
/// <para>Per label, for the reason the detector's own suppression is per class: a dog in front of its
/// owner is two boxes over nearly the same pixels, and merging those loses the object that matters.</para>
/// </summary>
public static class OverlappingRegions
{
    /// <summary>
    /// Overlap at or above which two boxes of one label are the same object seen twice.
    ///
    /// The same figure the detector's own suppression uses within a single inference
    /// (<c>YoloDflPostprocessor.OverlapLimit</c>): a pair it would have merged inside one region must
    /// not survive by having been found in two.
    /// </summary>
    public const float OverlapLimit = 0.4f;

    /// <summary>
    /// Everything found across a frame's regions, with duplicate sightings of one object reduced to the
    /// highest-scoring copy.
    ///
    /// <para>Returned strongest first rather than in the order found, which is what makes the greedy
    /// pass correct: a weaker duplicate is only ever dropped in favour of one already kept, so the box
    /// that survives is the best look any region got at the object.</para>
    /// </summary>
    /// <param name="found">Detections from every region examined this frame, in frame coordinates.</param>
    public static IReadOnlyList<DetectedObject> Fold(IReadOnlyList<DetectedObject> found)
    {
        if (found.Count < 2)
        {
            return found;
        }

        List<DetectedObject> kept = [];

        foreach (DetectedObject candidate in found.OrderByDescending(static d => d.Score))
        {
            bool duplicate = false;

            foreach (DetectedObject held in kept)
            {
                if (string.Equals(held.Label, candidate.Label, StringComparison.Ordinal)
                    && ObjectTracker.Overlap(held.Box, candidate.Box) >= OverlapLimit)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }
}
