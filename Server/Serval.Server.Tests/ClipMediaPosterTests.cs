using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serval.Server.Clips;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// That a poster ffmpeg did not actually put a frame in is treated as no poster at all.
///
/// <para><b>Found on a live deployment, not by reasoning.</b> <c>-y</c> creates the output before
/// ffmpeg knows whether it has a frame for it, so a seek landing past the last frame leaves a
/// zero-byte file — and ffmpeg can still exit 0. The old check was <c>File.Exists</c>, so that empty
/// file was reported as a successful write and then served as an empty 200 by every route gated the
/// same way. On the alert card that is a broken picture; since the push notification's image is the
/// alert's poster, it is also a notification that arrives with no picture, which is one of the
/// symptoms this whole change set is about.</para>
///
/// <para>ffmpeg is a real subprocess here, driven through <c>Ingest:FfmpegPath</c> — a stub that
/// reproduces the observed behaviour exactly (create the file, write nothing, exit 0) rather than a
/// mock of what it was assumed to do.</para>
/// </summary>
public class ClipMediaPosterTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("serval-poster-tests").FullName;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // A temp directory that outlives the run is not a failed test.
        }
    }

    /// <summary>A stand-in ffmpeg. <paramref name="script"/> is the body; the output path ffmpeg
    /// was given is its last argument, which the body reads as <c>$out</c>. The empty <c>for</c>
    /// that sets it is the POSIX spelling — <c>/bin/sh</c> is dash on Debian and Ubuntu, and
    /// <c>${@: -1}</c> is a bash expansion dash rejects before the body runs.
    ///
    /// <para>Marked linux-only so the mode call sits under a guard the analyzer can see — the same
    /// shape <c>DetectFrameReaderTests</c> uses, and these tests are shell-script-driven anyway.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    private ClipMedia MediaRunning(string script)
    {
        string stub = Path.Combine(_dir, "fake-ffmpeg");
        File.WriteAllText(stub, "#!/bin/sh\nfor out; do :; done\n" + script + "\n");
        File.SetUnixFileMode(
            stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new ClipMedia(
            Options.Create(new ServerOptions { Ingest = new IngestOptions { FfmpegPath = stub } }),
            NullLogger<ClipMedia>.Instance);
    }

    private string Poster => Path.Combine(_dir, "poster.jpg");

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task A_poster_ffmpeg_left_empty_is_a_failure_and_is_not_left_on_disk()
    {
        // Exactly what was seen in the wild: the file exists, exit code is 0, size is zero.
        ClipMedia media = MediaRunning(""": > "$out"; exit 0""");

        bool wrote = await media.TryWritePosterAsync(
            Path.Combine(_dir, "clip.mp4"), Poster, seekSeconds: 5,
            TestContext.Current.CancellationToken);

        Assert.False(wrote);
        Assert.False(File.Exists(Poster));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task A_poster_with_a_frame_in_it_is_kept()
    {
        ClipMedia media = MediaRunning("""printf 'jpegbytes' > "$out"; exit 0""");

        bool wrote = await media.TryWritePosterAsync(
            Path.Combine(_dir, "clip.mp4"), Poster, seekSeconds: 5,
            TestContext.Current.CancellationToken);

        Assert.True(wrote);
        Assert.Equal(
            "jpegbytes",
            await File.ReadAllTextAsync(Poster, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The half-written case: ffmpeg failed partway and the bytes so far are not a picture. Leaving
    /// them would serve a truncated JPEG, which renders as broken rather than as absent.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task A_poster_from_a_failed_run_is_removed_even_when_it_has_bytes()
    {
        ClipMedia media = MediaRunning("""printf 'partial' > "$out"; exit 1""");

        bool wrote = await media.TryWritePosterAsync(
            Path.Combine(_dir, "clip.mp4"), Poster, seekSeconds: 5,
            TestContext.Current.CancellationToken);

        Assert.False(wrote);
        Assert.False(File.Exists(Poster));
    }
}
