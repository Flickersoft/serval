using Serval.CameraModule;

namespace Serval.CameraModule.Tests;

public class V4l2InteropTests
{
    [Fact]
    public void Struct_layouts_match_the_kernel_abi()
    {
        // Guards the 208-vs-204 sizeof(v4l2_format) bug: a struct off by a byte makes the
        // _IOC-encoded ioctl code wrong, surfacing at runtime as a baffling ENOTTY. VerifyLayouts
        // throws if any managed struct no longer matches the size its ioctl code encodes.
        V4l2.VerifyLayouts();
    }
}
