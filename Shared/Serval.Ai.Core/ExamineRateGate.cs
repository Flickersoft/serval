namespace Serval.Ai;

/// <summary>
/// A ceiling on how often one camera is examined at all, independent of how fast frames arrive.
///
/// <para>A different question from <see cref="InferenceScheduler"/>, which divides a host's
/// inference between cameras. This bounds the work before a frame is downsampled and planned at
/// all: a spare room is worth a look every ten seconds where a drive is worth every frame.</para>
///
/// <para><b>The comparison needs a tolerance.</b> Frames are dated from an integer index over a
/// rate, so a period that is not exactly representable in binary leaves consecutive frames a tick
/// short of it. Admitting only on a full period therefore drops about a third of the frames when
/// the ceiling equals the rate they arrive at — the setting that reads like "examine everything".
/// It depends on the rate, which is what makes it easy to miss: exact at 1 and 2 fps, but 143
/// frames of 200 at 5 fps and 146 at 10.</para>
/// </summary>
public sealed class ExamineRateGate(double maxFps)
{
    /// <summary>
    /// How much of a period a frame may fall short by and still count as on time. The error being
    /// absorbed is a few ticks on a period measured in hundreds of milliseconds; anything missing
    /// by more than a percent of it genuinely arrived early.
    /// </summary>
    private const double Tolerance = 0.01;

    private readonly TimeSpan _minimumGap = maxFps > 0
        ? TimeSpan.FromSeconds((1.0 / maxFps) * (1.0 - Tolerance))
        : TimeSpan.Zero;

    private DateTimeOffset? _lastAdmitted;

    /// <summary>Whether there is a ceiling at all. Zero or less means examine every frame.</summary>
    public bool Limits => maxFps > 0;

    /// <summary>Frames turned away for arriving inside the period.</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// Whether this frame should be examined, recording it as admitted if so. Dated from the
    /// frame's own position in the stream rather than from when it was read, so a reader that falls
    /// behind and catches up does not admit a burst.
    /// </summary>
    public bool Admit(DateTimeOffset frameAt)
    {
        if (!Limits)
        {
            _lastAdmitted = frameAt;
            return true;
        }

        // A frame older than the last admitted one is a new session's clock starting over, not a
        // frame arriving early. Refusing it would stall the gate until the new session caught up to
        // an instant from the previous one — on a long-running camera, a very long time.
        if (_lastAdmitted is { } last && frameAt >= last && frameAt - last < _minimumGap)
        {
            Skipped++;
            return false;
        }

        _lastAdmitted = frameAt;
        return true;
    }
}
