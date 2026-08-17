using System.Diagnostics;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// What the snapshot filter actually does to a frame, checked by running it.
///
/// <para>This is the only place that can check it. The fitting arithmetic is an ffmpeg expression
/// rather than C#, deliberately — the JPEG path is built without knowing the source's dimensions, so
/// a camera that refuses a probe still produces snapshots — and the cost of that choice is that no
/// unit test can evaluate it. Asserting the filter string only restates it. So each case here feeds
/// a synthetic source of a known shape through the real
/// <see cref="SnapshotWatcher.OutputArgs(string, double, long)"/> and reads the size that comes out
/// the other end.</para>
///
/// <para>The cases are chosen around the property the budget exists for: at one setting, every
/// shape of camera gets the same amount of picture. A width limit could not do that — it gave a
/// 32:9 camera half of what a 16:9 camera got and a portrait camera three times as much.</para>
///
/// <para>Skipped where ffmpeg is not on PATH.</para>
/// </summary>
public class SnapshotScaleTests
{
    /// <summary>0.25 MP — the shipped default, and the budget every expectation below is fitted to.</summary>
    private const double Budget = 0.25;

    [Theory]
    // Every shape lands on ~0.25 MP, whatever it started as.
    [InlineData(3840, 2160, 666, 374)]      // 16:9, 4K
    [InlineData(2560, 1440, 666, 374)]      // 16:9, 2K — same result, as it should be
    [InlineData(3840, 1080, 942, 264)]      // 32:9: 942 wide, where a 640 width limit gave 640x180
    [InlineData(1080, 1920, 374, 664)]      // 9:16: 0.25 MP, where a 640 width limit gave 640x1138
    [InlineData(2048, 2048, 500, 500)]      // 1:1
    [InlineData(704, 480, 604, 412)]        // 4:3
    // A source already inside the budget is left exactly alone rather than upscaled.
    [InlineData(640, 360, 640, 360)]
    [InlineData(320, 180, 320, 180)]
    public async Task A_snapshot_is_fitted_to_the_budget_whatever_shape_it_is(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        (int width, int height) = await RenderAsync(sourceWidth, sourceHeight, Budget);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);

        // yuvj420p halves chroma on both axes, so an odd dimension has no legal representation and
        // the encoder refuses the frame outright.
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    /// <summary>
    /// The property stated directly: two cameras of wildly different shapes, one setting, and
    /// pixel counts within a few percent of each other. This is what the width limit could not do.
    /// </summary>
    [Fact]
    public async Task Two_cameras_of_different_shapes_get_the_same_amount_of_picture()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        (int wideWidth, int wideHeight) = await RenderAsync(3840, 1080, Budget);
        (int tallWidth, int tallHeight) = await RenderAsync(1080, 1920, Budget);

        long wide = (long)wideWidth * wideHeight;
        long tall = (long)tallWidth * tallHeight;

        Assert.InRange(wide / (double)tall, 0.98, 1.02);
    }

    [Fact]
    public async Task Zero_removes_the_cap_entirely()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        (int width, int height) = await RenderAsync(3840, 2160, megapixels: 0);

        Assert.Equal(3840, width);
        Assert.Equal(2160, height);
    }

    /// <summary>
    /// Renders one frame of a synthetic source through the snapshot output arguments and returns the
    /// size of the JPEG that lands.
    ///
    /// Reads the file back rather than parsing ffmpeg's log: the size on disk is what every consumer
    /// downstream sees, and the log is a description of it.
    /// </summary>
    private static async Task<(int Width, int Height)> RenderAsync(
        int sourceWidth, int sourceHeight, double megapixels)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serval-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // OutputArgs writes into its own subdirectory, relative to the working directory, the
            // same way a session's does.
            Directory.CreateDirectory(Path.Combine(dir, SnapshotWatcher.DirectoryName));

            List<string> args =
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi",
                "-i", $"testsrc2=size={sourceWidth}x{sourceHeight}:rate=1:duration=1",
                .. SnapshotWatcher.OutputArgs("0:v", 1.0, PixelBudget.Pixels(megapixels)),
            ];

            await RunAsync("ffmpeg", args, dir);

            string frame = Directory.EnumerateFiles(
                Path.Combine(dir, SnapshotWatcher.DirectoryName), "*.jpg").Single();

            string size = await RunAsync(
                "ffprobe",
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height",
                    "-of", "csv=p=0",
                    frame,
                ],
                dir);

            string[] parts = size.Trim().TrimEnd(',').Split(',');
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string> RunAsync(
        string file, IEnumerable<string> args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {file}.");

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string stdout = await output;
        string stderr = await errors;

        Assert.True(
            process.ExitCode == 0,
            $"{file} exited {process.ExitCode}: {stderr}");

        return stdout;
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
