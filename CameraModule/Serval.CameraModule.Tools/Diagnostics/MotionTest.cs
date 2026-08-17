using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Runs the motion gate over a sequence of images and prints what it decided. Run with:
///   dotnet run -- --motion a.jpg b.jpg [c.jpg ...]
///
/// No model is loaded and no device is touched, so this answers "would this have woken the vision
/// model?" in milliseconds. That matters because the gate's thresholds are the one part of the
/// vision path that genuinely has to be tuned per camera: a wide outdoor view with foliage needs a
/// different <c>MinChangedFraction</c> from a static indoor hallway, and the alternative to
/// measuring is guessing and then wondering why a camera describes everything or nothing.
///
/// Frames are compared in order, each against the one before, exactly as the live path does.
/// </summary>
public static class MotionTest
{
    public static int Run(CameraModuleOptions options, IReadOnlyList<string> imagePaths)
    {
        if (imagePaths.Count < 2)
        {
            Console.Error.WriteLine("Usage: --motion <first.jpg> <second.jpg> [more.jpg ...]");
            return 1;
        }

        MotionOptions motion = options.Motion;

        Console.WriteLine(
            $"Compare at : {motion.CompareWidth}x{motion.CompareHeight} grayscale");
        Console.WriteLine($"Pixel delta: {motion.PixelDelta}");
        Console.WriteLine(
            $"Trigger    : {motion.MinChangedFraction:P2} to {motion.MaxChangedFraction:P2} of pixels changed");
        Console.WriteLine();

        var detector = new JpegMotionDetector(motion);
        int triggered = 0;

        for (int i = 0; i < imagePaths.Count; i++)
        {
            string path = imagePaths[i];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL: no such image: {path}");
                return 1;
            }

            MotionResult result;
            try
            {
                result = detector.Accept(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: could not decode {path}: {ex.Message}");
                return 1;
            }

            string verdict = i == 0
                ? "reference frame (nothing to compare against)"
                : result.Rejected
                    ? "REJECTED — whole-frame change, treated as lighting or IR-cut, not motion"
                    : result.Moved
                        ? "MOTION — would request a description"
                        : "still — below threshold";

            Console.WriteLine($"{Path.GetFileName(path),-28} changed {result.ChangedFraction,8:P3}  {verdict}");

            if (result.Moved)
            {
                triggered++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{triggered} of {imagePaths.Count - 1} comparison(s) would have triggered a description.");

        // Always a success exit: this reports a measurement, it does not assert one. There is no
        // universally correct answer here — what the number should be depends on the camera.
        return 0;
    }
}
