using Microsoft.Extensions.Logging;

namespace Serval.Ai;

/// <summary>
/// Builds the detector <see cref="DetectionOptions.Device"/> asks for.
///
/// <para><b>The one place that setting is read.</b> Before this existed, the backend setting was
/// declared, documented, copied — and consulted by nothing, so an operator who set it got the ONNX
/// detector regardless. Four call sites constructed <see cref="OnnxObjectDetector"/> directly; they
/// all come through here now, or they drift.</para>
///
/// <para><b>It is also the only place that has to know two axes were ever collapsed into one.</b>
/// A device name carries a runtime and a piece of silicon: the runtime picks the implementation and
/// says how to read <see cref="DetectionOptions.ModelPath"/>, the silicon becomes an ONNX Runtime
/// execution provider or an EdgeTPU delegate. <see cref="OnnxObjectDetector"/> is handed its provider
/// rather than reading a setting of its own, so nothing downstream can disagree with what was
/// selected.</para>
///
/// <para><b>Every check that can be made without a native library is made before one is loaded.</b>
/// That ordering is a testability constraint with a real payoff: the rejection paths can be asserted on
/// a machine with no Coral runtime and no ONNX Runtime provider at all. It also keeps a misconfiguration
/// reporting the file it could not read rather than a delegate that would not open.</para>
/// </summary>
public static class ObjectDetectorFactory
{
    /// <summary>Devices that can be named. Exposed so the settings catalogue can take its choices from
    /// here rather than restating them, which is what keeps the two from drifting.</summary>
    public static IReadOnlyList<string> Devices { get; } =
        [OnnxCpu, "onnx-cuda", "onnx-openvino", "onnx-tensorrt", TfliteEdgeTpu];

    /// <summary>The devices that run through ONNX Runtime, and so read an <c>.onnx</c>
    /// <see cref="DetectionOptions.ModelPath"/> and honour the thread and concurrency settings.
    ///
    /// <para>Exposed for the same reason as <see cref="Devices"/>: the settings catalogue marks
    /// several settings as applying only to this family, and a second hand-written list of the same
    /// names is a list that eventually disagrees.</para></summary>
    public static IReadOnlyList<string> OnnxDevices { get; } =
        [.. Devices.Where(static d => d.StartsWith(OnnxPrefix, StringComparison.Ordinal))];

    /// <inheritdoc cref="OnnxDevices"/>
    public static IReadOnlyList<string> TfliteDevices { get; } =
        [.. Devices.Where(static d => d.StartsWith(TflitePrefix, StringComparison.Ordinal))];

    private const string OnnxPrefix = "onnx-";
    private const string TflitePrefix = "tflite-";
    private const string OnnxCpu = "onnx-cpu";
    private const string TfliteEdgeTpu = "tflite-edgetpu";

    /// <summary>
    /// Whether the configured device has the files it needs, and what is missing when it does not.
    ///
    /// <para><b>Deliberately does not enumerate devices.</b> A USB hiccup at boot would otherwise
    /// degrade the whole server to the motion gate silently — the worst available failure, because
    /// everything downstream then looks healthy. Whether a device is attached is decided in
    /// <see cref="Create"/>, where its absence can be thrown.</para>
    /// </summary>
    public static bool IsConfigured(DetectionOptions options, out string reason)
    {
        string device = Normalise(options.Device);

        if (!Devices.Contains(device))
        {
            reason =
                $"Detection:Device is '{options.Device}', which is not one of "
                + $"{string.Join(", ", Devices)}.";
            return false;
        }

        if (!File.Exists(options.LabelsPath))
        {
            reason = $"the labels file '{options.LabelsPath}' is missing";
            return false;
        }

        if (!File.Exists(options.ModelPath))
        {
            reason = $"device '{device}' needs '{options.ModelPath}', which is missing";
            return false;
        }

        // The one mistake sharing a path makes possible, so it is the one thing that must not happen
        // quietly. Both runtimes would otherwise fail deep inside a native loader, where the message
        // is about a malformed buffer rather than about a model belonging to the other family.
        bool wantsTflite = device.StartsWith(TflitePrefix, StringComparison.Ordinal);
        bool isTflite = LooksLikeTflite(options.ModelPath);

        if (wantsTflite != isTflite)
        {
            reason = wantsTflite
                ? $"device '{device}' needs the edgetpu_compiler output, but '{options.ModelPath}' is "
                    + "not a TFLite model"
                : $"device '{device}' needs ONNX weights, but '{options.ModelPath}' is a TFLite model";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Builds the detector, and warns about anything that will make it quietly useless.
    /// </summary>
    /// <param name="loggerFactory">A factory rather than a logger, because which concrete type is built
    /// is decided in here — so the caller cannot know the category to ask for.</param>
    /// <exception cref="InvalidOperationException">An unknown device, or <c>tflite-edgetpu</c> with no
    /// device attached.</exception>
    public static IObjectDetector Create(DetectionOptions options, ILoggerFactory loggerFactory)
    {
        string device = Normalise(options.Device);
        ILogger logger = loggerFactory.CreateLogger(typeof(ObjectDetectorFactory).FullName!);

        if (!Devices.Contains(device))
        {
            throw new InvalidOperationException(
                $"Detection:Device is '{options.Device}'. Valid values are "
                + $"{string.Join(", ", Devices)}.");
        }

        WarnAboutConfiguredClasses(options, logger);

        return device == TfliteEdgeTpu
            ? EdgeTpuObjectDetector.Create(
                options, loggerFactory.CreateLogger<EdgeTpuObjectDetector>())
            : OnnxObjectDetector.Create(
                options, Silicon(device), loggerFactory.CreateLogger<OnnxObjectDetector>());
    }

    /// <summary>The ONNX Runtime execution provider a device name asks for — the half after the
    /// runtime prefix, which is exactly what <see cref="OnnxObjectDetector"/> needs and all of it.
    /// </summary>
    public static string Silicon(string device)
    {
        string normalised = Normalise(device);

        return normalised.StartsWith(OnnxPrefix, StringComparison.Ordinal)
            ? normalised[OnnxPrefix.Length..]
            : normalised;
    }

    /// <summary>
    /// Whether a file is a TFLite model, from the FlatBuffer file identifier every one of them carries
    /// at byte 4.
    ///
    /// <para>Sniffed rather than taken from the extension because the extension is a claim and this is
    /// evidence, and because the failure being caught is somebody pointing the one model path at the
    /// wrong file — where the name is the thing most likely to be misleading. There is no equivalent
    /// test for ONNX: it is protobuf with no magic number, so "not TFLite" is treated as ONNX and its
    /// own loader produces the detailed error.</para>
    /// </summary>
    private static bool LooksLikeTflite(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];

            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return false;
            }

            return header[4] == (byte)'T'
                && header[5] == (byte)'F'
                && header[6] == (byte)'L'
                && header[7] == (byte)'3';
        }
        catch (IOException)
        {
            // Unreadable for some other reason. Not this check's business to report — the caller has
            // already established the file exists, and whichever loader opens it next will say why.
            return false;
        }
    }

    /// <summary>
    /// Warns about configured classes the model cannot produce.
    ///
    /// <para><b>The highest value-per-line check in the detection path, and it applies to both
    /// runtimes.</b> The three class lists are matched against the model's own label strings with
    /// <see cref="StringComparer.Ordinal"/>, and a miss fails <em>closed</em>: no episode opens, the
    /// vision model is never woken, and nothing is ever flagged as an alert. There is no error, no empty
    /// result to notice, and the only trace is a counter that reaches no endpoint. A vocabulary swap —
    /// COCO-80 to COCO-90, or a labels file from the wrong family — is exactly what causes it.</para>
    ///
    /// <para>Here rather than in either detector because this is the one place holding the options, the
    /// labels and a logger at the same time, and because both deserve it.</para>
    /// </summary>
    private static void WarnAboutConfiguredClasses(DetectionOptions options, ILogger logger)
    {
        LabelFile labels;

        try
        {
            labels = LabelFile.Load(options.LabelsPath);
        }
        catch (Exception ex)
        {
            // The detector's own Create will fail on this in a moment with a better message; this check
            // is an extra and must never be the thing that takes the server down.
            logger.LogDebug("Could not cross-check configured classes: {Message}", ex.Message);
            return;
        }

        var known = new HashSet<string>(labels.Labels, StringComparer.Ordinal);

        Check("Detection:Classes", options.EffectiveClasses);
        Check("Detection:AlertClasses", options.EffectiveAlertClasses);
        Check("Detection:DescribeClasses", options.EffectiveDescribeClasses);

        void Check(string setting, IReadOnlyList<string> configured)
        {
            string[] missing = [.. configured.Where(name => !known.Contains(name))];

            if (missing.Length == 0)
            {
                return;
            }

            logger.LogWarning(
                "{Setting} names {Missing}, which '{LabelsPath}' does not provide. Class names are "
                + "matched exactly, so those entries can never match anything: no episode opens, no "
                + "description is requested and nothing is flagged. Check that the labels file belongs "
                + "to these weights — COCO-90 and COCO-80 share spellings but not indices.",
                setting, string.Join(", ", missing.Select(static m => $"'{m}'")), options.LabelsPath);
        }
    }

    private static string Normalise(string? device) =>
        string.IsNullOrWhiteSpace(device) ? OnnxCpu : device.Trim().ToLowerInvariant();
}
