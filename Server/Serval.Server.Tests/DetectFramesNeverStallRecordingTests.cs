using System.Diagnostics;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The one property the whole detect-frame transport was chosen for: nothing detection does can stop
/// the recording.
///
/// <para>The detect output rides on the same ffmpeg that writes the camera's HLS segments, because
/// sharing the process is what makes the decode free and keeps detections on the same media clock as
/// the footage they will be drawn over. The price of sharing is that ffmpeg's output loop is shared
/// too. Had the frames gone down a pipe — the obvious choice — a reader that stalled for one frame
/// time would have blocked ffmpeg's write, since a Linux pipe holds 64 KiB against a frame of well
/// over a megabyte, and the recording would have stopped with it. A burst of database writes delaying
/// the read loop would have been enough.</para>
///
/// <para>So this runs a real ffmpeg with both outputs and never reads the frames at all. Segments
/// must keep being written regardless. This is a slow test by the standards of this assembly and it
/// earns that: the failure it guards is silent, intermittent, and costs footage.</para>
/// </summary>
public class DetectFramesNeverStallRecordingTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public async Task An_unread_detect_output_does_not_stop_segments_being_written()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        string root = Path.Combine(Path.GetTempPath(), $"serval-stall-{Guid.NewGuid():N}");
        string detectDir = Path.Combine(root, "detect");
        Directory.CreateDirectory(detectDir);

        try
        {
            var args = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",

                // A synthetic source so this needs no camera and no network. The duration is an
                // *input* option, before -i, and must stay there: after -i it bounds only the output
                // it precedes, leaving the detect output running against an endless source until it
                // fills the disk.
                "-t", "2",
                "-f", "lavfi", "-i", $"testsrc=size={Width}x{Height}:rate=10",

                // The recording output, in the shape the real one takes.
                "-map", "0:v", "-c:v", "libx264", "-preset", "ultrafast", "-g", "5",
                "-f", "hls", "-hls_segment_type", "fmp4", "-hls_time", "0.5", "-hls_list_size", "0",
                "-hls_flags", "independent_segments",
                "-hls_fmp4_init_filename", "init.mp4",
                "-hls_segment_filename", "seg-%05d.m4s",
                "live.m3u8",
            };

            // ...and the detect output, byte for byte the one the sessions emit.
            args.AddRange(DetectFrameReader.OutputArgs("0:v", fps: 5, Width, Height, detectDir));

            using var process = Start(args, root);

            // Deliberately no reader. Every frame ffmpeg writes here stays on disk for the whole run.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);

            string[] segments = Directory.GetFiles(root, "seg-*.m4s");
            string[] frames = Directory.GetFiles(detectDir, "frame-*.yuv");

            // Both outputs ran to completion. Under a pipe, ffmpeg would have blocked on the first
            // unread frame — one frame here is ~115 KB against a 64 KiB pipe — and neither the
            // remaining frames nor any further segment would exist.
            Assert.True(segments.Length >= 2, $"only {segments.Length} segment(s) were written");
            Assert.True(frames.Length >= 5, $"only {frames.Length} detect frame(s) were written");

            // Two seconds at five frames a second, and no more: an output that outran its input
            // would mean the duration bound had slipped onto the wrong side of -i.
            Assert.True(frames.Length <= 20, $"{frames.Length} frames is more than the source held");

            // Sequential from zero, which is what FrameClock's anchoring assumes — frame n is
            // sessionStart + n/fps, and an index that is really a presentation timestamp in some
            // filter time base would date every detection somewhere else entirely.
            Assert.Equal(
                Enumerable.Range(0, frames.Length).Select(i => (long)i),
                frames
                    .Select(path => FrameFiles.IndexOf(Path.GetFileName(path), "frame-", ".yuv")!.Value)
                    .Order());

            // And the frames really are the size the reader will insist on, which is what lets it
            // tell a finished one from a partial one.
            Assert.All(
                frames,
                path => Assert.Equal(
                    DetectFrameReader.FrameBytes(Width, Height), new FileInfo(path).Length));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process Start(IEnumerable<string> args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        // Drained so ffmpeg cannot block on its own stderr, which would be this test reproducing the
        // exact hazard it is meant to rule out.
        _ = process.StandardError.ReadToEndAsync();

        return process;
    }

    private static class Ffmpeg
    {
        public static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit(5000);
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
