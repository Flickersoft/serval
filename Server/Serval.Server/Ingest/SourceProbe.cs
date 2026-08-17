using System.Diagnostics;
using System.Globalization;

namespace Serval.Server.Ingest;

/// <summary>What a source says it is sending on its video stream. Any field may be null when the
/// source did not answer.</summary>
internal sealed record VideoProbe(string? Codec, int? Width, int? Height);

/// <summary>
/// Asks a source what it is sending, for the questions that shape a session's command line.
///
/// Shared by the recording session and the standalone snapshot session because both now need the
/// source's dimensions: the raw detect output is written at an exact pixel size so the reader can
/// tell a complete frame from one still being written, and that size can only be computed from the
/// source's own aspect ratio.
/// </summary>
internal static class SourceProbe
{
    /// <summary>How long a wedged source is given before its probe is abandoned.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Codec and dimensions in one call, since a second ffprobe means a second chance to stall on a
    /// wedged source.
    /// </summary>
    public static async Task<VideoProbe> VideoAsync(
        string url, string ffprobePath, string cameraId, ILogger logger, CancellationToken cancellationToken)
    {
        string[] fields = await RunAsync(
            url, ffprobePath, "v:0", "stream=codec_name,width,height", cameraId, logger,
            cancellationToken);

        return new VideoProbe(
            fields.Length > 0 && fields[0].Length > 0 ? fields[0] : null,
            fields.Length > 1 ? Parse(fields[1]) : null,
            fields.Length > 2 ? Parse(fields[2]) : null);

        static int? Parse(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                && parsed > 0
                ? parsed
                : null;
    }

    /// <summary>
    /// The source's audio codec, or null when there is no audio track — a normal answer, not a
    /// fault.
    /// </summary>
    public static async Task<string?> AudioCodecAsync(
        string url, string ffprobePath, string cameraId, ILogger logger, CancellationToken cancellationToken)
    {
        string[] fields = await RunAsync(
            url, ffprobePath, "a:0", "stream=codec_name", cameraId, logger, cancellationToken);

        return fields.Length > 0 && fields[0].Length > 0 ? fields[0] : null;
    }

    private static async Task<string[]> RunAsync(
        string url,
        string ffprobePath,
        string streamSpecifier,
        string entries,
        string cameraId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // ffprobe takes the same input options as ffmpeg, so the same scheme dispatch applies —
        // handing -rtsp_transport to a non-RTSP source fails the probe, which would silently
        // downgrade a copyable stream into a transcode.
        foreach (string arg in new[]
                 {
                     "-v", "error", "-select_streams", streamSpecifier,
                     "-show_entries", entries,
                     "-of", "default=nw=1:nokey=1",
                 }.Concat(SourceArguments.ProbeArgs(url)))
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var probe = Process.Start(startInfo);
            if (probe is null)
            {
                return [];
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            string output = await probe.StandardOutput.ReadToEndAsync(timeout.Token);
            await probe.WaitForExitAsync(timeout.Token);

            // One value per line, in the order asked for. Blank lines are kept so a missing middle
            // field cannot shift the ones after it into the wrong slots.
            return output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "ffprobe ({Stream}) failed for camera {CameraId}.", streamSpecifier, cameraId);
            return [];
        }
    }
}
