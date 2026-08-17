using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The recording session's ffmpeg command line.
///
/// This assembly had no tests at all while it lived as a private method, which is the wrong shape
/// for it: nobody reads an ffmpeg argument list, so a mistake here shows up as a recording that is
/// subtly wrong weeks later — a decode nobody wanted, segments a player will not seek in, or an
/// archive quietly deleted by the muxer.
/// </summary>
public class RecordArgumentsTests
{
    private static RecordSpec Spec(
        VideoPlan video,
        AudioPlan? audio = null,
        bool snapshot = false,
        DetectFramePlan? detect = null) =>
        new(
            Url: "rtsp://cam/main",
            Video: video,
            Audio: audio ?? new AudioPlan(false, false, null),
            SegmentSeconds: 4.0,
            SessionStamp: "20260731-120000",
            InitFileName: "init-20260731-120000.mp4",
            WithSnapshot: snapshot,
            SnapshotFps: 1.0,
            SnapshotMaxPixels: 250_000,
            Detect: detect);

    private static DetectFramePlan Detect() =>
        new("/dev/shm/serval/detect/cam-1", Fps: 5.0, Width: 1280, Height: 720);

    private static VideoPlan Copy(string codec = "h264", string? tag = null) =>
        new(VideoMode.Copy, codec, null, tag);

    private static VideoPlan Transcode(string codec = "h264", string? device = null) =>
        new(VideoMode.Transcode, codec, EncoderSelector.Select(codec, device, null, "2M"), null);

    private static List<string> Build(RecordSpec spec) => [.. RecordArguments.Build(spec)];

    // --- video --------------------------------------------------------------------------------

    [Fact]
    public void A_copy_carries_no_forced_keyframes()
    {
        // The regression this guards is expensive and invisible: -force_key_frames on a copy makes
        // ffmpeg decode and re-encode, which is exactly the cost the copy exists to avoid.
        List<string> args = Build(Spec(Copy()));

        Assert.Contains("copy", args);
        Assert.DoesNotContain("-force_key_frames", args);
    }

    [Fact]
    public void A_transcode_forces_keyframes_at_segment_boundaries()
    {
        List<string> args = Build(Spec(Transcode()));

        int index = args.IndexOf("-force_key_frames");
        Assert.True(index >= 0);
        Assert.Equal("expr:gte(t,n_forced*4)", args[index + 1]);
    }

    [Fact]
    public void A_copy_with_a_codec_tag_emits_it()
    {
        List<string> args = Build(Spec(Copy("hevc", "hvc1")));

        int index = args.IndexOf("-tag:v");
        Assert.True(index >= 0);
        Assert.Equal("hvc1", args[index + 1]);
    }

    [Fact]
    public void Hardware_setup_lands_before_the_input_and_filters_after_it()
    {
        // -vaapi_device is an input option: after -i it is silently ignored and the encode falls
        // back to software, which looks like "hardware encoding is slow" rather than "off".
        List<string> args = Build(Spec(Transcode("h264", "/dev/dri/renderD128")));

        Assert.True(args.IndexOf("-vaapi_device") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-vf") > args.IndexOf("-i"));
    }

    // --- audio --------------------------------------------------------------------------------

    [Fact]
    public void Audio_that_can_be_copied_is_copied()
    {
        List<string> args = Build(Spec(Copy(), new AudioPlan(true, true, "aac")));

        int index = args.IndexOf("-c:a");
        Assert.Equal("copy", args[index + 1]);
    }

    [Fact]
    public void Audio_that_cannot_be_copied_becomes_mono_aac()
    {
        List<string> args = Build(Spec(Copy(), new AudioPlan(true, false, "pcm_mulaw")));

        Assert.Equal(["-c:a", "aac", "-b:a", "64k", "-ac", "1"],
            args.Skip(args.IndexOf("-c:a")).Take(6));
    }

    [Fact]
    public void No_audio_map_at_all_when_audio_is_not_included()
    {
        List<string> args = Build(Spec(Copy()));

        Assert.DoesNotContain("-map", args.Skip(args.IndexOf("-c:v")).Where(a => a == "0:a?"));
        Assert.DoesNotContain("0:a?", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void The_audio_map_tolerates_a_track_that_disappears_mid_session()
    {
        // '?' rather than a bare 0:a — a camera that drops its audio should keep recording video
        // rather than failing the whole session.
        List<string> args = Build(Spec(Copy(), new AudioPlan(true, true, "aac")));

        Assert.Contains("0:a?", args);
    }

    // --- output -------------------------------------------------------------------------------

    [Fact]
    public void Segments_are_never_deleted_by_the_muxer()
    {
        // hls_list_size 0 and the absence of delete_segments are what let recordings outlive the
        // playlist. If ffmpeg started pruning, the RetentionWorker's retention policy would be
        // silently overridden by a much shorter one.
        List<string> args = Build(Spec(Copy()));

        int index = args.IndexOf("-hls_list_size");
        Assert.Equal("0", args[index + 1]);
        Assert.DoesNotContain(args, a => a.Contains("delete_segments", StringComparison.Ordinal));
    }

    /// <summary>The snapshot output's filename pattern, wherever it appears in the arguments.</summary>
    private static string? SnapshotOutput(List<string> args) =>
        args.Find(a => a.Contains("snap-", StringComparison.Ordinal));

    [Fact]
    public void The_snapshot_output_appears_only_when_this_session_owns_it()
    {
        // Two processes writing the same frames would race; neither writing them loses the
        // dashboard, motion detection and the AI.
        Assert.Null(SnapshotOutput(Build(Spec(Copy()))));
        Assert.NotNull(SnapshotOutput(Build(Spec(Copy(), snapshot: true))));
    }

    [Fact]
    public void The_snapshot_is_a_second_output_after_the_playlist()
    {
        // Before live.m3u8 it would become an option of the HLS output rather than its own output.
        List<string> args = Build(Spec(Copy(), snapshot: true));

        Assert.True(args.IndexOf("live.m3u8") < args.IndexOf(SnapshotOutput(args)!));
    }

    [Fact]
    public void The_snapshot_output_names_frames_by_their_position_in_the_stream()
    {
        // The whole reason it is not one overwritten file. Without -frame_pts the only timestamp
        // available is when the Server got round to reading it, which on a real camera ran ten
        // seconds behind the footage the frame came from.
        List<string> args = Build(Spec(Copy(), snapshot: true));

        Assert.Contains("-frame_pts", args);
        Assert.Equal("1", args[args.IndexOf("-frame_pts") + 1]);
        Assert.DoesNotContain("-update", args);
        Assert.Contains("%d", SnapshotOutput(args)!);
    }

    // --- raw detect frames --------------------------------------------------------------------

    /// <summary>The detect output's filename pattern, wherever it appears in the arguments.</summary>
    private static string? DetectOutput(List<string> args) =>
        args.Find(a => a.Contains("frame-", StringComparison.Ordinal));

    [Fact]
    public void The_detect_output_appears_only_when_this_session_owns_the_frames()
    {
        // It rides on whoever produces the snapshots, for the same reason they do: two processes
        // writing one camera's frames would race, and neither writing them loses detection.
        Assert.Null(DetectOutput(Build(Spec(Copy(), detect: Detect()))));
        Assert.Null(DetectOutput(Build(Spec(Copy(), snapshot: true))));
        Assert.NotNull(DetectOutput(Build(Spec(Copy(), snapshot: true, detect: Detect()))));
    }

    [Fact]
    public void Detect_frames_are_unencoded_planar_video()
    {
        // yuv420p because its luma plane is exactly the grayscale buffer motion detection wants, at
        // two thirds the bandwidth of RGB; rawvideo because re-encoding a picture only to decode it
        // again costs both time and the detail a small distant object can least afford.
        List<string> args = Build(Spec(Copy(), snapshot: true, detect: Detect()));

        Assert.Equal("yuv420p", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Contains("rawvideo", args);
        Assert.Equal("image2", args[args.LastIndexOf("-f") + 1]);
    }

    [Fact]
    public void Detect_frames_are_written_at_an_exact_size()
    {
        // The reader tells a finished frame from a partial one by its exact length, so an
        // aspect-derived height that ffmpeg rounded differently would make every frame look
        // incomplete forever. area sampling averages what it discards, which is what stops a
        // downscale aliasing a distant subject out of existence.
        List<string> args = Build(Spec(Copy(), snapshot: true, detect: Detect()));

        Assert.Contains(args, a => a.Contains("scale=1280:720:flags=area", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("fps=5", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_frames_are_staged_outside_the_camera_directory()
    {
        // Absolute because the session runs in the camera's media directory, and these belong on
        // tmpfs rather than the disk holding the recordings.
        string output = Assert.IsType<string>(
            DetectOutput(Build(Spec(Copy(), snapshot: true, detect: Detect()))));

        Assert.StartsWith("/dev/shm/serval/detect/cam-1", output, StringComparison.Ordinal);
        Assert.Contains("%d", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_frames_name_each_frame_by_its_position_in_the_stream()
    {
        // Same reason the snapshots do: the index is what dates a detection, and the alternative is
        // the wall clock at the moment we got round to the file.
        List<string> args = Build(Spec(Copy(), snapshot: true, detect: Detect()));

        Assert.Equal(2, args.Count(a => a == "-frame_pts"));
    }

    [Fact]
    public void The_recording_still_comes_first()
    {
        // Both extra outputs sit after live.m3u8; before it they would become options of the HLS
        // output rather than outputs of their own.
        List<string> args = Build(Spec(Copy(), snapshot: true, detect: Detect()));

        Assert.True(args.IndexOf("live.m3u8") < args.IndexOf(SnapshotOutput(args)!));
        Assert.True(args.IndexOf(SnapshotOutput(args)!) < args.IndexOf(DetectOutput(args)!));
    }

    // --- decode threads -------------------------------------------------------------------------

    [Fact]
    public void A_copy_that_makes_stills_caps_decode_threads_before_the_input()
    {
        // After -i it would bind to the JPEG encoder and leave the decoder on the host's core
        // count, which is the whole thing being capped: a deep frame-threading pipeline holds
        // frames back, putting the wall and every detection seconds behind the camera.
        List<string> args = Build(Spec(Copy(), snapshot: true, detect: Detect()));

        Assert.Equal("2", args[args.IndexOf("-threads") + 1]);
        Assert.True(args.IndexOf("-threads") < args.IndexOf("-i"));
    }

    [Fact]
    public void A_pure_copy_caps_nothing()
    {
        // Nothing is decoded at all without a still to make, so the cap would describe work that
        // does not happen.
        Assert.DoesNotContain("-threads", Build(Spec(Copy())));
    }

    [Fact]
    public void A_transcode_is_left_at_the_hosts_own_parallelism()
    {
        // Here the decode feeds an encoder rather than a still, so its threads are throughput and
        // capping them would slow the recording down to buy latency nobody is waiting on.
        Assert.DoesNotContain("-threads", Build(Spec(Transcode(), snapshot: true, detect: Detect())));
    }
}
