using System.Runtime.InteropServices;

namespace DlnRenderer.Player.Mpv;

/// <summary>Reports whether the native libmpv DLL can be located and loaded.</summary>
public static class MpvRuntime
{
    public const string LibraryFileName = "libmpv-2.dll";

    public static string ExpectedPath => Path.Combine(AppContext.BaseDirectory, LibraryFileName);

    /// <summary>
    /// Attempts to load the DLL from the application directory. Does not initialise a player,
    /// and therefore says nothing about whether playback itself works.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!File.Exists(ExpectedPath))
        {
            return false;
        }

        return NativeLibrary.TryLoad(ExpectedPath, out var handle) && Release(handle);
    }

    private static bool Release(IntPtr handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }
}
