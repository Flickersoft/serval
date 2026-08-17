using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace Serval.CameraModule;

/// <summary>
/// Teaches the runtime to find PortAudio by its versioned soname.
///
/// PortAudioSharp's P/Invoke declares <c>DllImport("portaudio")</c>, which resolves to the
/// unversioned <c>libportaudio.so</c> — a file that only ships in the <c>-dev</c> package.
/// Every Linux distro's runtime package installs the versioned <c>libportaudio.so.2</c>
/// instead, so a plain <c>apt install libportaudio2</c> would otherwise still fail with a
/// DllNotFoundException. This resolver loads the versioned library directly, so the runtime
/// package alone is enough on the Orange Pi (and anywhere else).
/// </summary>
internal static class PortAudioNativeResolver
{
    [ModuleInitializer]
    internal static void Register()
    {
        // Registered against PortAudioSharp's assembly (where the DllImport lives), before any
        // PortAudio call runs. A module initializer executes at assembly load, ahead of Main.
        NativeLibrary.SetDllImportResolver(typeof(PortAudio).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "portaudio")
        {
            foreach (string candidate in new[] { "libportaudio.so.2", "libportaudio.so" })
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    return handle;
                }
            }
        }

        // Anything else (or nothing found): fall back to the default resolution.
        return IntPtr.Zero;
    }
}
