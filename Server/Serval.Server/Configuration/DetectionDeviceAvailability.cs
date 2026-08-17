using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Serval.Ai;

namespace Serval.Server.Configuration;

/// <summary>
/// Which values of a choice setting this host and image cannot actually deliver, and why.
///
/// <para>The catalogue is a static table and says what a setting <em>accepts</em>; this says what it
/// can <em>do</em> here. Keeping them apart is what lets the catalogue stay a declaration while the
/// answer stays true on a machine it was not written for.</para>
///
/// <para>Advisory, not enforcement. <see cref="SettingsCatalog.Validate"/> still accepts any
/// catalogued value, because a config backup restored onto a different host must not be refused for
/// naming hardware that host does not have. What this drives is the App greying the option out —
/// and, when an unavailable value is nevertheless the one in force, showing it as the selection with
/// the reason attached, which is how somebody finds out their compose file is being ignored.</para>
/// </summary>
public interface ISettingChoiceProbe
{
    /// <summary>The catalogue key this answers for.</summary>
    string Key { get; }

    /// <summary>Unavailable value → why, empty when everything is selectable.</summary>
    IReadOnlyDictionary<string, string> Unavailable();
}

/// <summary>
/// What <c>Serval:Ai:Detection:Device</c> can run on this host, asked of the runtime rather than
/// assumed from a build flag.
///
/// <para>Probed because the honest answer differs per image and per host, and a hardcoded list would
/// be wrong in the one direction that matters: an image built with a GPU provider would keep showing
/// it as impossible. Asking means a future image needs no code change here to light its option up.
/// </para>
///
/// <para><b>Answered once and cached for the process.</b> The setting is restart-gated, so "what was
/// true when this server started" is the granularity that can actually be acted on, and settling for
/// it keeps a USB enumeration off every read of the settings page. A device lost while running is a
/// different question with a different answer, and <c>VitalsAlerts</c> already reports it.</para>
/// </summary>
public sealed class DetectionDeviceAvailability : ISettingChoiceProbe
{
    private readonly DetectionOptions _detection;
    private readonly ILogger<DetectionDeviceAvailability> _logger;
    private readonly Lock _gate = new();

    private IReadOnlyDictionary<string, string>? _answer;

    public DetectionDeviceAvailability(
        IOptions<ServerOptions> options,
        ILogger<DetectionDeviceAvailability> logger)
    {
        _detection = options.Value.Ai.Detection;
        _logger = logger;
    }

    public string Key => "Serval:Ai:Detection:Device";

    public IReadOnlyDictionary<string, string> Unavailable()
    {
        lock (_gate)
        {
            return _answer ??= Probe();
        }
    }

    private Dictionary<string, string> Probe()
    {
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);

        // The providers compiled into whichever libonnxruntime.so actually loaded — which is not
        // necessarily the one this package graph implies, since Detection:NativeLibraryPath can point
        // the resolver somewhere else entirely. That indirection is the whole reason to ask rather
        // than infer.
        HashSet<string> providers;

        try
        {
            providers = [.. OrtEnv.Instance().GetAvailableProviders()];
        }
        catch (Exception ex)
        {
            // Nothing to report against: without an answer, claiming a provider is missing would be
            // as much of a guess as claiming it is present, and the wrong guess here disables a
            // working option.
            _logger.LogWarning(
                "Could not ask ONNX Runtime which execution providers it carries ({Message}); every "
                + "device will be offered and an unavailable one will warn and fall back at startup.",
                ex.Message);
            return unavailable;
        }

        foreach (string device in ObjectDetectorFactory.OnnxDevices)
        {
            string silicon = ObjectDetectorFactory.Silicon(device);

            if (silicon == "cpu")
            {
                continue;
            }

            // ORT names them "CUDAExecutionProvider", "OpenVINOExecutionProvider" and so on. Matched
            // case-insensitively on the stem so the mapping survives ORT's own capitalisation, which
            // is not consistent between providers.
            if (!providers.Any(p => p.StartsWith(silicon, StringComparison.OrdinalIgnoreCase)))
            {
                unavailable[device] = $"This image's ONNX Runtime has no {silicon} provider.";
            }
        }

        // Hoisted out of the loop below: enumeration talks to libedgetpu and the USB bus, and the
        // answer is about the host rather than about any one device name.
        if (EdgeTpuObjectDetector.CountDevices(_detection, _logger) == 0)
        {
            foreach (string device in ObjectDetectorFactory.TfliteDevices)
            {
                unavailable[device] =
                    "No Edge TPU found. Check that the device is attached and that the container was "
                    + "given /dev/bus/usb and a cgroup rule for major 189.";
            }
        }

        return unavailable;
    }
}
