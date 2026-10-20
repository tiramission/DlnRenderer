using System.Runtime.InteropServices;

namespace DlnRenderer.Player.Mpv;

/// <summary>
/// Minimal libmpv client API surface. Only the functions this player actually uses are declared,
/// so the native dependency stays explicit and auditable.
/// </summary>
internal static class MpvNative
{
    private const string Library = "libmpv-2.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr ctx, IntPtr args);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_request_log_messages(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    /// <summary>Runs an mpv command with UTF-8 arguments. Returns 0 on success.</summary>
    internal static int Command(IntPtr ctx, params string[] args)
    {
        var argv = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }

            argv[^1] = IntPtr.Zero;

            var handle = GCHandle.Alloc(argv, GCHandleType.Pinned);
            try
            {
                return mpv_command(ctx, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            foreach (var ptr in argv)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
    }

    internal static string ErrorText(int error)
    {
        var ptr = mpv_error_string(error);
        if (ptr == IntPtr.Zero)
        {
            return $"mpv error {error}";
        }

        var text = Marshal.PtrToStringUTF8(ptr) ?? $"mpv error {error}";
        // mpv_error_string returns a static string: it must NOT be freed.
        return text;
    }

    internal static int SetDouble(IntPtr ctx, string name, double value)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            Marshal.WriteInt64(buffer, BitConverter.DoubleToInt64Bits(value));
            return mpv_set_property(ctx, name, (int)MpvFormat.Double, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int SetFlag(IntPtr ctx, string name, bool value)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, value ? 1 : 0);
            return mpv_set_property(ctx, name, (int)MpvFormat.Flag, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string? GetString(IntPtr ctx, string name)
    {
        var ptr = mpv_get_property_string(ctx, name);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            mpv_free(ptr);
        }
    }
}
