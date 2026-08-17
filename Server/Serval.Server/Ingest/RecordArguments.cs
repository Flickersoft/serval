using System.Globalization;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <param name="WithSnapshot">
/// Whether this process also emits the camera's JPEG and raw detect frames. True only when the
/// recorded stream is also the detect stream — see <see cref="FfmpegSnapshotSession"/> for the other
/// case.
/// </param>
/// <param name="Detect">
/// The raw detect-frame output, or null to emit none — which is the case when the source would not
/// say how big it is, since frames of unknown size cannot be read back.
/// </param>
/// <param name="DecodeThreads">
/// Decoder threads, applied only when this process decodes solely to make stills — see
/// <see cref="ServerOptions.DecodeThreads"/>. A transcoding recorder ignores it.
/// </param>
internal sealed record RecordSpec(
    string Url,
    VideoPlan Video,
    AudioPlan Audio,
    double SegmentSeconds,
    string SessionStamp,
    string InitFileName,
    bool WithSnapshot,
    double SnapshotFps,
    long SnapshotMaxPixels,
    DetectFramePlan? Detect = null,
    int DecodeThreads = 2);

/// <summary>
/// Where a camera's raw detect frames go, how often, and at exactly what size.
///
/// <paramref name="Directory"/> is absolute because the session runs in the camera's media directory
/// and these frames deliberately do not live there — they belong on tmpfs, away from the disk holding
/// the recordings.
/// </summary>
internal sealed record DetectFramePlan(string Directory, double Fps, int Width, int Height);

/// <summary>
/// Assembles the recording session's ffmpeg command line. Pure and static for the same reason
/// <see cref="SourceArguments"/> is: this is the code whose output nobody reads until something is
/// silently wrong with a recording weeks later, so it needs to be testable without a camera, a
/// database or a disk.
/// </summary>
internal static class RecordArguments
{
    public static IReadOnlyList<string> Build(RecordSpec spec)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "warning" };
        string seg = spec.SegmentSeconds.ToString(CultureInfo.InvariantCulture);

        // Hardware setup (e.g. VAAPI device) goes before -i.
        if (spec.Video.Encoder is { } encoder)
        {
            args.AddRange(encoder.InputArgs);
        }

        // A copy that also emits stills is the one case where this process decodes for no reason
        // but the stills, so the frame-threading delay is pure latency and gets capped. A transcode
        // decodes to feed its encoder, where the parallelism is the point and is left alone; a pure
        // copy never decodes at all, so the cap would describe work that does not happen.
        if (spec.WithSnapshot && spec.Video.Mode == VideoMode.Copy)
        {
            args.AddRange(["-threads", spec.DecodeThreads.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(SourceArguments.InputArgs(spec.Url));

        // Video for the recording output.
        args.AddRange(["-map", "0:v"]);
        if (spec.Video.Mode == VideoMode.Copy)
        {
            args.AddRange(["-c:v", "copy"]);

            if (spec.Video.CodecTag is { } tag)
            {
                args.AddRange(["-tag:v", tag]);
            }

            // Note what is deliberately absent: -force_key_frames. A copy cannot insert a keyframe
            // that is not in the source, so the HLS muxer instead starts each segment at the first
            // keyframe at or after hls_time. Every segment still begins on a keyframe — which is
            // what independent_segments asserts — but its duration is >= SegmentSeconds, quantised
            // to the camera's GOP. Set the camera's I-frame interval to SegmentSeconds, or a
            // divisor of it, to keep segments the length they were asked to be.
        }
        else
        {
            args.AddRange(spec.Video.Encoder!.FilterArgs);
            args.AddRange(spec.Video.Encoder.VideoArgs);
            // Force keyframes at segment boundaries so every segment is independently decodable.
            args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{seg})"]);
        }

        if (spec.Audio.Include)
        {
            // '?' keeps a camera that drops its audio mid-session recording video rather than
            // failing outright.
            args.AddRange(["-map", "0:a?"]);

            // Most IP cameras emit G.711 (pcm_mulaw/pcm_alaw), which has no fMP4 sample entry at
            // all — copying it produces a file nothing can open. Unlike video, there is exactly one
            // legal target and it costs ~64 kbps, so this is a container constraint rather than a
            // codec choice made on the operator's behalf.
            args.AddRange(
                spec.Audio.Copy
                    ? ["-c:a", "copy"]
                    : new[] { "-c:a", "aac", "-b:a", "64k", "-ac", "1" });
        }

        // HLS fMP4 output. Unlike the DASH muxer, this puts every mapped stream into one variant,
        // so each segment file holds video and audio together.
        //
        // hls_list_size 0 keeps every segment in the playlist and, crucially, stops ffmpeg from
        // deleting any — the RetentionWorker prunes by age instead, so recordings survive.
        // delete_segments is deliberately absent from hls_flags for the same reason.
        args.AddRange([
            "-f", "hls",
            "-hls_segment_type", "fmp4",
            "-hls_time", seg,
            "-hls_list_size", "0",
            "-hls_flags", "independent_segments",
            "-hls_fmp4_init_filename", spec.InitFileName,
            "-hls_segment_filename", $"seg-{spec.SessionStamp}-%05d.m4s",
            "live.m3u8",
        ]);

        // Snapshot output: one JPEG per frame at the snapshot rate — but only when this stream is
        // also the detect stream. It is the reason a copied recording decodes at all, so a camera
        // with its own detect stream is deliberately left as a pure copy here.
        if (spec.WithSnapshot)
        {
            args.AddRange(SnapshotWatcher.OutputArgs("0:v", spec.SnapshotFps, spec.SnapshotMaxPixels));

            // Raw frames for object detection, at their own rate and their own size. Nearly free
            // beside the snapshot above: producing that one already forces a full decode of every
            // frame — the fps filter picks one and discards the rest — so these cost a scale and a
            // write rather than a decode.
            if (spec.Detect is { } detect)
            {
                args.AddRange(DetectFrameReader.OutputArgs(
                    "0:v", detect.Fps, detect.Width, detect.Height, detect.Directory));
            }
        }

        return args;
    }
}
