using Microsoft.Extensions.Logging;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Grabs a single frame and writes it to disk:
///   dotnet run -- --capture-test frame.jpg
///
/// Proves the V4L2 interop works on real hardware before any model is involved. On a new
/// board this is the first thing to run: it isolates camera problems from model problems.
/// </summary>
public static class CaptureTest
{
    public static int Run(CameraModuleOptions options, ILoggerFactory loggerFactory, string outPath)
    {
        var logger = loggerFactory.CreateLogger("CaptureTest");
        var capture = options.Capture;

        try
        {
            using var camera = new V4l2MjpegCamera(capture.DevicePath, capture.Width, capture.Height, logger);

            // Report every frame in a short burst. The first frames after STREAMON are
            // routinely truncated while exposure settles, so this shows the warmup pattern
            // rather than hiding it behind a single sample.
            Console.WriteLine("Frame sequence (warmup is expected to be incomplete):");
            byte[]? frame = null;
            for (int i = 0; i < 12; i++)
            {
                byte[]? candidate = camera.ReadFrame(timeoutMs: 2000);
                if (candidate is null)
                {
                    Console.WriteLine($"  {i,2}: (timeout)");
                    continue;
                }

                bool complete = V4l2MjpegCamera.IsCompleteJpeg(candidate);
                Console.WriteLine($"  {i,2}: {candidate.Length,7:N0} bytes  {(complete ? "complete" : "TRUNCATED")}");

                if (complete)
                {
                    frame = candidate;
                }
            }

            if (frame is null)
            {
                Console.Error.WriteLine("FAIL: no complete frame received.");
                return 1;
            }

            File.WriteAllBytes(outPath, frame);
            Console.WriteLine();

            Console.WriteLine($"Resolution : {camera.Width}x{camera.Height}");
            Console.WriteLine($"Frame      : {frame.Length:N0} bytes -> {outPath}");

            int failures = 0;

            // A JPEG starts with SOI (FF D8) and ends with EOI (FF D9). If the driver had
            // silently handed us raw pixels instead, this is what would catch it.
            bool soi = frame.Length > 4 && frame[0] == 0xFF && frame[1] == 0xD8;
            bool eoi = frame.Length > 4 && frame[^2] == 0xFF && frame[^1] == 0xD9;

            if (soi)
            {
                Console.WriteLine("PASS: JPEG start-of-image marker present.");
            }
            else
            {
                Console.Error.WriteLine("FAIL: missing JPEG SOI marker — these are not JPEG bytes.");
                failures++;
            }

            if (eoi)
            {
                Console.WriteLine("PASS: JPEG end-of-image marker present (frame is complete).");
            }
            else
            {
                Console.Error.WriteLine("FAIL: missing JPEG EOI marker — frame is truncated.");
                failures++;
            }

            // A plausible MJPEG frame is tens of KB. A few hundred bytes means a black or
            // broken frame that would waste a very expensive VLM inference.
            if (frame.Length > 5000)
            {
                Console.WriteLine($"PASS: frame size is plausible ({frame.Length / 1024:N0} KB).");
            }
            else
            {
                Console.Error.WriteLine($"FAIL: frame is suspiciously small ({frame.Length} bytes).");
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "CAPTURE TEST PASSED" : $"CAPTURE TEST FAILED ({failures})");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
    }
}
