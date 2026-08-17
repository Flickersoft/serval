using System.Globalization;

namespace Serval.Server.Ingest;

/// <summary>
/// A ceiling on how much picture a frame carries, expressed as pixel area rather than as a width.
///
/// Area is the honest unit for every consumer downstream. A dashboard tile, a vision model and a
/// JPEG on the wire all cost what the pixel count costs, and none of them cares which axis the
/// pixels lie along — a dynamic-resolution VLM in particular bills tokens by area. Capping a single
/// axis makes the aspect ratio a hidden multiplier on all three: at the same setting a 32:9 frame
/// gets half the picture a 16:9 frame does, and a portrait frame three times as much, so the dial
/// cannot be raised for the wide cameras without inflating the tall ones far more.
///
/// Pure and process-free, so the argument construction is unit-testable — though the arithmetic
/// itself lives in an ffmpeg expression, which is what <c>SnapshotScaleTests</c> runs ffmpeg to
/// check.
/// </summary>
internal static class PixelBudget
{
    /// <summary>
    /// Megapixels, as the settings page states them, in pixels as a filter expression needs them.
    ///
    /// Zero and below mean no budget, which is how a setting turns the cap off entirely.
    /// </summary>
    public static long Pixels(double megapixels) =>
        megapixels > 0 ? (long)Math.Round(megapixels * 1_000_000) : 0;

    /// <summary>
    /// An ffmpeg <c>scale</c> filter that shrinks a frame to at most <paramref name="pixels"/> of
    /// area, or null when there is no budget to enforce.
    ///
    /// <para><c>sqrt(P/(iw*ih))</c> is the linear factor that lands the frame exactly on the budget,
    /// applied to both axes so the aspect ratio survives. <c>min(iw,…)</c> is what makes this a
    /// ceiling rather than a target: a source already inside the budget comes through untouched
    /// rather than upscaled, which would cost bandwidth for detail that is not there.</para>
    ///
    /// <para>The width is truncated to an even number and the height left to <c>-2</c>, because
    /// yuvj420p halves chroma on both axes and an odd dimension has no legal representation. The
    /// <c>max(2,…)</c> is the floor that survives a budget small enough to round an axis to nothing.
    /// </para>
    ///
    /// <para>An expression rather than arithmetic here, deliberately. The callers that produce JPEGs
    /// do not all know their source's dimensions — <c>FfmpegSnapshotSession</c> keeps working when a
    /// camera refuses to answer a probe, precisely because the JPEG path never needed them — so
    /// computing this in C# would either cost that property or need a second fallback path.</para>
    /// </summary>
    public static string? ScaleFilter(long pixels) => pixels > 0
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"scale=w='max(2,trunc(min(iw,iw*sqrt({pixels}/(iw*ih)))/2)*2)':h=-2")
        : null;

    /// <summary>The filter for a budget stated in megapixels, or null when there is none.</summary>
    public static string? ScaleFilter(double megapixels) => ScaleFilter(Pixels(megapixels));
}
