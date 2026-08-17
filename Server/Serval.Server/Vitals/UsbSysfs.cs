using System.Globalization;

namespace Serval.Server.Vitals;

/// <summary>
/// What speed a USB device negotiated, from sysfs.
///
/// <para>Here for one reason: a Coral Edge TPU on a USB 2 path delivers about a third of what the
/// same stick delivers on USB 3 — measured at 29.7 inferences a second against 64.2 — and nothing
/// about the symptom points at the cause. The device works, the model is right, the scores are
/// right, it is simply slow, and the only way to find out is <c>lsusb -t</c> on the host. The
/// per-device figures on the status page make the difference visible; this names it.</para>
///
/// <para>Best-effort throughout. The file is world-readable and sysfs is mounted in the container,
/// but a device that is not on USB at all — a PCIe Coral — has no such path, and a null reads as
/// "not said" rather than as anything being wrong.</para>
///
/// <para>Pure, like <see cref="GpuSysfs"/>: the path arithmetic and the parsing are here so they can
/// be tested against a temp directory, and the read itself is in <see cref="SystemStatsCollector"/>.
/// </para>
/// </summary>
public static class UsbSysfs
{
    public const string DeviceRoot = "/sys/bus/usb/devices";

    /// <summary>Where the kernel publishes the negotiated speed in Mbit/s, for a device named the way
    /// libedgetpu names it — <c>2-2</c>.</summary>
    public static string SpeedPath(string root, string device) =>
        Path.Combine(root, device, "speed");

    /// <summary>
    /// The generation a speed in Mbit/s belongs to, as a person would say it.
    ///
    /// <para>Ranges rather than equality: the wire speeds in circulation are 1.5, 12, 480, 5000,
    /// 10000 and 20000, and a part reporting a value between two of them should round down to the
    /// generation it can actually achieve rather than fall out as unknown.</para>
    ///
    /// <para><b>Only meaningful once the device has been opened.</b> A Coral enumerates pre-firmware
    /// as a genuine USB 2.0 device and only trains at SuperSpeed after libedgetpu uploads its
    /// firmware, so this is read against a device the detector already holds open — never at startup
    /// to decide anything.</para>
    /// </summary>
    public static string? Generation(double? megabitsPerSecond) => megabitsPerSecond switch
    {
        null => null,
        >= 10000 => "USB 3.2",
        >= 5000 => "USB 3",
        >= 480 => "USB 2",
        > 0 => "USB 1",
        _ => null,
    };

    /// <summary>Reads the number out of a <c>speed</c> file's contents, or null if it says anything
    /// else. Invariant culture: sysfs writes <c>5000</c> and a comma-decimal locale must not turn
    /// that into five.</summary>
    public static double? ParseSpeed(string? contents) =>
        double.TryParse(
            contents?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            ? speed
            : null;
}
