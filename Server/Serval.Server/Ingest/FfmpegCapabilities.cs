using System.Diagnostics;

namespace Serval.Server.Ingest;

/// <summary>ffmpeg could not be run, or answered with something unrecognisable.</summary>
public sealed class FfmpegUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// What this host's ffmpeg can actually encode, read once at startup from
/// <c>ffmpeg -hide_banner -encoders</c>.
///
/// The absence of this was the single worst failure mode in the ingest path: hardware varies per
/// deployer, nothing checked it, and so asking for a codec the host could not encode produced an
/// ffmpeg that failed on every attempt and was retried forever, with the reason visible only to
/// someone reading logs. Knowing the encoder list up front turns that into a 400 on the request
/// that caused it, which is the only place the mistake is still attached to a person.
///
/// Read once and cached: the answer cannot change without the process restarting, and shelling out
/// per camera per reconnect would be absurd.
/// </summary>
public sealed class FfmpegCapabilities
{
    private readonly HashSet<string> _videoEncoders;

    /// <summary>
    /// Build from a known encoder list. This is how tests get a host with, say, libx264 but no
    /// av1_vaapi without needing that hardware — or any ffmpeg at all.
    /// </summary>
    public FfmpegCapabilities(IEnumerable<string> videoEncoders) =>
        _videoEncoders = new HashSet<string>(videoEncoders, StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> VideoEncoders => _videoEncoders;

    public bool CanEncodeVideo(string? encoder) =>
        !string.IsNullOrWhiteSpace(encoder) && _videoEncoders.Contains(encoder.Trim());

    /// <summary>
    /// Runs ffmpeg and parses its encoder table. Throws
    /// <see cref="FfmpegUnavailableException"/> rather than returning an empty set — a server that
    /// cannot run ffmpeg cannot record, and silently continuing would reject every transcode
    /// request with "this host has no encoders", which points at the wrong problem.
    /// </summary>
    public static FfmpegCapabilities Probe(string ffmpegPath, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");

        string output;
        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new FfmpegUnavailableException($"'{ffmpegPath} -encoders' started no process.");

            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new FfmpegUnavailableException(
                    $"'{ffmpegPath} -encoders' did not finish within {timeout.TotalSeconds:0}s.");
            }
        }
        catch (FfmpegUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FfmpegUnavailableException(
                $"ffmpeg could not be run at '{ffmpegPath}' ({ex.Message}). Serval records by "
                + "shelling out to ffmpeg and cannot start without it. Install it, or set "
                + "Serval:Ingest:FfmpegPath to its full path.", ex);
        }

        IReadOnlyList<string> encoders = ParseVideoEncoders(output);
        if (encoders.Count == 0)
        {
            // Not "this build has no video encoders" — no such build exists. It means the table
            // format moved and the parser silently matched nothing, which would otherwise present
            // as every transcode request being rejected for the wrong reason.
            throw new FfmpegUnavailableException(
                $"'{ffmpegPath} -encoders' returned no recognisable video encoders. The output "
                + "format may have changed; Serval cannot validate transcode requests against it.");
        }

        return new FfmpegCapabilities(encoders);
    }

    /// <summary>
    /// The parse half, separated so the output format is unit-tested without running anything.
    ///
    /// The table is a preamble, a line of dashes, then one row per encoder:
    /// <code>
    ///  V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC
    /// </code>
    /// The first field is exactly six flag characters whose first character is the media type, and
    /// the second is the encoder name. Matching on the flag field's shape rather than on a fixed
    /// column keeps this working if the description column shifts; requiring the leading dashes
    /// line keeps the preamble's prose out of the result.
    /// </summary>
    internal static IReadOnlyList<string> ParseVideoEncoders(string encodersOutput)
    {
        var names = new List<string>();
        bool inTable = false;

        foreach (string line in encodersOutput.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!inTable)
            {
                inTable = trimmed.Length > 0 && trimmed.All(c => c == '-');
                continue;
            }

            string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0].Length == 6 && fields[0][0] == 'V')
            {
                names.Add(fields[1]);
            }
        }

        return names;
    }
}
