using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// A minimal top-level window with a child HWND that libmpv renders into. It exists so the
/// service can present video without depending on WinUI or WinForms. The window is hidden while
/// audio plays and shown when a video track is actually present.
/// </summary>
/// <remarks>
/// Window creation and the message loop live on a dedicated thread. Every public member is safe to
/// call from any thread: work is posted to that thread and the returned task completes after it ran.
/// </remarks>
public sealed class VideoPresenter : IAsyncDisposable
{
    private const int WsOverlappedWindow = 0x00CF0000;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const int SwShow = 5;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint WmApp = 0x8000;
    private const uint WmInvoke = WmApp + 1;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _windowProc;
    private readonly Thread _thread;
    private nint _window;
    private nint _child;
    private volatile bool _visible;
    private Exception? _startupError;

    private VideoPresenter()
    {
        // Hold a strong reference so the native window class never points at a collected delegate.
        _windowProc = WindowProcedure;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "DlnRenderer video host",
        };
    }

    /// <summary>Handle for mpv's <c>wid</c> option. Available once <see cref="StartAsync"/> completed.</summary>
    public nint ChildHandle => _child;

    public bool IsVisible => _visible;

    public static async Task<VideoPresenter> StartAsync(CancellationToken cancellationToken = default)
    {
        var presenter = new VideoPresenter();
        presenter._thread.SetApartmentState(ApartmentState.STA);
        presenter._thread.Start();

        while (!presenter._ready.Wait(10, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (presenter._startupError is not null)
        {
            await presenter.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "The video presentation window could not be created.",
                presenter._startupError);
        }

        return presenter;
    }

    public void Show()
    {
        if (_visible)
        {
            return;
        }

        _visible = true;
        Run(() =>
        {
            if (_window != 0)
            {
                ShowWindow(_window, SwShowNoActivate);
            }

            if (_child != 0)
            {
                ShowWindow(_child, SwShow);
            }
        });
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        Run(() =>
        {
            if (_child != 0)
            {
                ShowWindow(_child, SwHide);
            }

            if (_window != 0)
            {
                ShowWindow(_window, SwHide);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        if (!_thread.IsAlive)
        {
            _ready.Dispose();
            return ValueTask.CompletedTask;
        }

        Run(() =>
        {
            if (_window != 0)
            {
                PostMessage(_window, WmClose, 0, 0);
            }
        });

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // The thread is a background thread; process exit must not hang on it.
            _ready.Dispose();
            return ValueTask.CompletedTask;
        }

        _ready.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThreadMain()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var className = $"DlnRendererVideoHost_{Environment.ProcessId}";

            var windowClass = new WindowClassEx
            {
                Size = Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = instance,
                ClassName = className,
            };

            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            _window = CreateWindowEx(
                0,
                className,
                "DlnRenderer",
                WsOverlappedWindow | WsClipSiblings | WsClipChildren,
                CwUseDefault,
                CwUseDefault,
                960,
                540,
                0,
                0,
                instance,
                0);

            if (_window == 0)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            _child = CreateWindowEx(
                0,
                "static",
                "DlnRendererVideo",
                WsChild | WsVisible | WsClipSiblings | WsClipChildren,
                0,
                0,
                1,
                1,
                _window,
                0,
                instance,
                0);

            if (_child == 0)
            {
                throw new InvalidOperationException(
                    $"Video child window creation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            _ready.Set();

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInvoke:
                while (_queue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _startupError ??= ex;
                    }
                }

                return 0;

            case WmSize:
                if (_child != 0)
                {
                    _ = GetClientRect(hwnd, out var client);
                    SetWindowPos(
                        _child,
                        0,
                        0,
                        0,
                        Math.Max(1, client.Right),
                        Math.Max(1, client.Bottom),
                        0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
                }

                return 0;

            case WmClose:
                DestroyWindow(hwnd);
                return 0;

            case WmDestroy:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void Run(Action action)
    {
        if (!_thread.IsAlive)
        {
            return;
        }

        _queue.Enqueue(action);
        if (_window != 0)
        {
            PostMessage(_window, WmInvoke, 0, 0);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public int Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
