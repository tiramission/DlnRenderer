using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace DlnRenderer.Player.Vlc;

/// <summary>
/// Forces the standard arrow cursor over libvlc's embedded video windows.
/// </summary>
/// <remarks>
/// <para>
/// VLC 3's video output builds its own child-window stack under the host HWND and manages the
/// pointer itself: an idle auto-hide implemented with a blank cursor, and a wait cursor (IDC_WAIT)
/// shown from its own <c>WM_SETCURSOR</c> handling while it considers the vout busy. Inside an
/// application window that behaviour reads as a stuck "loading" pointer. The renderer owns every
/// interaction through its own UI, so VLC gets none of it.
/// </para>
/// <para>
/// Class-cursor overrides are not enough - VLC sets the cursor explicitly from its window
/// procedure - so the guard subclasses the vout windows and answers <c>WM_SETCURSOR</c> with the
/// arrow, forwarding everything else to VLC's original procedure. The windows live on libvlc's own
/// thread; the replacement procedure touches no managed state beyond the (locked) original-proc
/// table, and the delegate is rooted statically because any vout thread may call it at any time.
/// </para>
/// <para>
/// The vout windows are created lazily on the first play and re-created when a vout session is
/// replaced, so <see cref="Install"/> re-runs on every start and subclasses only windows it has
/// not seen before.
/// </para>
/// </remarks>
internal static class VlcCursorGuard
{
    private const uint WmSetCursor = 0x0020;
    private const int GwlpWndProc = -4;
    private const int IdcArrow = 32512;

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, nint> OriginalProcs = new();
    private static readonly nint ArrowCursor = LoadCursor(default, IdcArrow);
    private static int _installRunning;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Rooted for the process lifetime: any VLC vout thread may call into it.</summary>
    private static readonly WndProcDelegate GuardProc = GuardWindowProc;

    /// <summary>
    /// Subclasses VLC's video windows under <paramref name="hostWindow"/>. Idempotent; safe to call
    /// on every playback start. The windows appear shortly after the vout starts, so the scan
    /// retries briefly before giving up (a missed pass is corrected by the next call).
    /// </summary>
    public static void Install(nint hostWindow)
    {
        if (hostWindow == 0)
        {
            return;
        }

        // One scan at a time: concurrent installs would race the subclass swap on the same hwnd.
        if (Interlocked.CompareExchange(ref _installRunning, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (ScanAndSubclass(hostWindow))
                    {
                        return;
                    }

                    Thread.Sleep(200);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _installRunning, 0);
            }
        });
    }

    /// <returns>True when every VLC window found is already guarded.</returns>
    private static bool ScanAndSubclass(nint hostWindow)
    {
        var complete = true;

        EnumChildWindows(hostWindow, (hwnd, _) =>
        {
            var name = new StringBuilder(128);
            _ = GetClassName(hwnd, name, 128);
            if (!name.ToString().StartsWith("VLC ", StringComparison.Ordinal))
            {
                return true;
            }

            lock (Gate)
            {
                if (OriginalProcs.ContainsKey(hwnd))
                {
                    return true;
                }

                var original = GetWindowLongPtr(hwnd, GwlpWndProc);
                if (original == 0)
                {
                    complete = false;
                    return true;
                }

                OriginalProcs[hwnd] = original;
                _ = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(GuardProc));
            }

            return true;
        }, default);

        return complete;
    }

    private static nint GuardWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmSetCursor)
        {
            _ = SetCursor(ArrowCursor);
            return (nint)1;
        }

        nint original;
        lock (Gate)
        {
            OriginalProcs.TryGetValue(hwnd, out original);
        }

        return original != 0
            ? CallWindowProc(original, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, int cursorId);
}
