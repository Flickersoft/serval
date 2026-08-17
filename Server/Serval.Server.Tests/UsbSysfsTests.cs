using System.Globalization;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// Reading a USB device's negotiated speed. Worth its own file for the same reason
/// <see cref="GpuSysfsTests"/> is: the path arithmetic and the parsing can be exercised against a
/// temp directory on a machine with no Coral in it.
/// </summary>
public class UsbSysfsTests
{
    [Theory]
    [InlineData(20000, "USB 3.2")]
    [InlineData(10000, "USB 3.2")]
    [InlineData(5000, "USB 3")]
    [InlineData(480, "USB 2")]
    [InlineData(12, "USB 1")]
    [InlineData(1.5, "USB 1")]
    public void A_wire_speed_names_its_generation(double speed, string expected) =>
        Assert.Equal(expected, UsbSysfs.Generation(speed));

    /// <summary>The pair this exists for: the same Coral on the two paths, and the reason one of them
    /// delivers about a third of the other.</summary>
    [Fact]
    public void The_two_speeds_a_coral_trains_at_are_told_apart()
    {
        Assert.Equal("USB 3", UsbSysfs.Generation(5000));
        Assert.Equal("USB 2", UsbSysfs.Generation(480));
    }

    [Fact]
    public void An_unreadable_speed_is_not_said_rather_than_guessed()
    {
        Assert.Null(UsbSysfs.Generation(null));
        Assert.Null(UsbSysfs.Generation(0));
        Assert.Null(UsbSysfs.ParseSpeed(null));
        Assert.Null(UsbSysfs.ParseSpeed(""));
        Assert.Null(UsbSysfs.ParseSpeed("unknown"));
    }

    [Fact]
    public void Trailing_whitespace_from_sysfs_is_tolerated() =>
        Assert.Equal(5000, UsbSysfs.ParseSpeed("5000\n"));

    /// <summary>
    /// Invariant culture, deliberately. sysfs writes <c>1.5</c> for a full-speed device, and a
    /// comma-decimal thread would otherwise parse it as fifteen and call a USB 1 device USB 1 for the
    /// wrong reason — or, at 5000, produce something unrecognisable.
    /// </summary>
    [Fact]
    public void A_comma_decimal_culture_reads_the_same_number()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            Assert.Equal(1.5, UsbSysfs.ParseSpeed("1.5"));
            Assert.Equal(5000, UsbSysfs.ParseSpeed("5000"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_speed_file_sits_under_the_device_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "2-2"));

        try
        {
            string path = UsbSysfs.SpeedPath(root, "2-2");
            File.WriteAllText(path, "5000\n");

            Assert.True(File.Exists(path));
            Assert.Equal("USB 3", UsbSysfs.Generation(UsbSysfs.ParseSpeed(File.ReadAllText(path))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
