using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// The service states the Service Control Manager understands, as far as this process uses them.
/// </summary>
/// <remarks>
/// Only the three transitions that matter here are modelled: starting, running and stopping. A
/// service launched by the SCM and unless the process reports <see cref="ServiceState.Running"/>
/// within 30 seconds the SCM declares the start a failure (event 7009) even though the process is
/// healthy. Reporting status is therefore not optional bookkeeping: it is what keeps "started by
/// the SCM" a supported state instead of a permanently failing one.
///
/// The shipped MSIX package does not register a Windows service (a packaged service runs in
/// session 0 without a desktop, so libmpv cannot create a swapchain - see
/// <c>docs/architecture.md</c> 16.1). This code therefore only runs for a service the user
/// registered themselves with <c>sc create</c>.
/// </remarks>
public enum ServiceState
{
    /// <summary>SERVICE_START_PENDING (2). The SCM is waiting for the service to finish starting.</summary>
    Starting = 2,

    /// <summary>SERVICE_RUNNING (4). The service is up; this stops the SCM's start timer.</summary>
    Running = 4,

    /// <summary>SERVICE_STOP_PENDING (3). An orderly shutdown is in progress.</summary>
    Stopping = 3,
}

/// <summary>
/// Connects this process to the Service Control Manager, reports status and receives stop requests.
/// </summary>
/// <remarks>
/// <para>
/// The supported entry point is <c>StartServiceCtrlDispatcher</c>, not a bare
/// <c>RegisterServiceCtrlHandlerEx</c> from the main thread. Measured on this machine: calling
/// <c>RegisterServiceCtrlHandlerEx</c> directly returns a null handle with
/// <c>0xC0000005</c> in the last-error slot for <em>every</em> service name, including services that
/// exist and run, because the calling thread is not a service-main thread. The dispatcher is what
/// makes the thread one; it then invokes <see cref="ServiceMain"/> and the handler registration
/// inside that callback succeeds.
/// </para>
/// <para>
/// Deliberately not <c>ServiceBase</c>: that base class owns the process lifetime and this service
/// is started by the UI or a developer's shell as well, so usually there is no SCM to talk to.
/// Everything here is a no-op when there is none.
/// </para>
/// </remarks>
public sealed class ServiceStatusReporter : IDisposable
{
    private readonly ServiceLog _log;
    private readonly Action _stopRequested;
    private readonly Lock _gate = new();
    private readonly ServiceMainDelegate _serviceMain;

    private SERVICE_STATUS _status;
    private nint _statusHandle;
    private bool _disposed;
    private bool _running;

    // Set once SERVICE_STOPPED has been sent; the terminal state must never be overwritten.
    private bool _stopped;

    /// <param name="log">Where a status-reporting failure is recorded.</param>
    /// <param name="serviceMain">
    /// Called by the SCM on the dispatcher thread once the service is connected. The renderer
    /// startup runs from here, because the dispatcher blocks this thread until the SCM asks for a
    /// stop and the callback must return promptly.
    /// </param>
    /// <param name="stopRequested">Invoked when the SCM asks the service to stop or shut down.</param>
    public ServiceStatusReporter(ServiceLog log, Action serviceMain, Action stopRequested)
    {
        _log = log;
        _stopRequested = stopRequested;

        // Both delegates must be rooted for the lifetime of the process: they are marshalled to
        // native code as function pointers, and collecting one would leave the SCM holding a
        // dangling callback.
        _serviceMain = OnServiceMain;
        _serviceMainCallback = serviceMain;
        _handler = OnControl;
    }

    /// <summary>The service name this binary answers to when the SCM starts it.</summary>
    /// <remarks>
    /// Must match the name the service was registered under (<c>sc create DlnRenderer.Service</c>),
    /// because the SCM passes the name it knows to the dispatcher and a mismatch means
    /// <see cref="OnServiceMain"/> is never called.
    /// </remarks>
    public const string ServiceName = "DlnRenderer.Service";

    /// <summary>
    /// Connects to the SCM and blocks for the lifetime of the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns immediately when this process was not started by the SCM (the UI-launched and
    /// developer-launched cases): they have no service-main thread to create, and blocking on the
    /// dispatcher would hang a console run forever.
    /// </para>
    /// <para>
    /// Important: a return of true does <b>not</b> mean the SCM asked for a stop. The dispatcher
    /// returns as soon as ServiceMain returns, which happens immediately after the renderer has
    /// been handed off. Reading "dispatcher returned" as "stop requested" shuts a perfectly
    /// healthy service down half a second after it starts - the caller must wait for the renderer
    /// task itself, and only treat an actual stop control as a stop request.
    /// </para>
    /// </remarks>
    public void ConnectAndWait()
    {
        if (!IsServiceHost)
        {
            return;
        }

        // One entry plus the null terminator, exactly as SERVICE_TABLE_ENTRY requires.
        var table = new SERVICE_TABLE_ENTRY[2];
        table[0] = new SERVICE_TABLE_ENTRY { lpServiceName = ServiceName, lpServiceProc = _serviceMain };
        table[1] = default;

        if (!StartServiceCtrlDispatcher(table))
        {
            // ERROR_FAILED_SERVICE_CONTROLLER_CONNECT (1064) means "no SCM connection for this
            // process", which is the expected answer when the UI starts the binary by hand. Any
            // other error is a real misconfiguration worth recording.
            var error = Marshal.GetLastWin32Error();
            if (error != FailedServiceControllerConnect)
            {
                _log.Write(
                    $"scm: StartServiceCtrlDispatcher failed (Win32 error {error}); " +
                    "the service will not report status and the SCM will time this start out");
            }
        }
    }

    /// <summary>
    /// Reports <see cref="ServiceState.Running"/>. This is the call that stops the SCM's 30-second
    /// start timer.
    /// </summary>
    public void ReportRunning()
    {
        lock (_gate)
        {
            if (!_running)
            {
                _running = true;
                Report(ServiceState.Running);
            }
        }
    }

    /// <summary>
    /// Reports <see cref="ServiceState.Stopping"/> and stops accepting further reports.
    /// </summary>
    public void ReportStopped()
    {
        lock (_gate)
        {
            // Nothing may follow the terminal SERVICE_STOPPED report: sending STOP_PENDING after
            // it would put the service back into stop-pending and leave the SCM waiting again.
            if (_stopped)
            {
                return;
            }

            if (_running || _status.dwCurrentState != (int)ServiceState.Stopping)
            {
                _running = false;
                Report(ServiceState.Stopping);
            }
        }
    }


    /// <summary>
    /// Reports SERVICE_STOPPED, which is what releases the SCM's stop-pending state.
    /// </summary>
    /// <remarks>
    /// Reporting Stopping alone leaves the service in STOP_PENDING forever: the SCM keeps waiting
    /// for the terminal status and the process is never considered done, so
    /// <c>StartServiceCtrlDispatcher</c> does not return and the process lingers with a live
    /// parent of services.exe. This is the last status report of the service's life, so it must be
    /// sent before the process exits - which means it cannot wait for the components to finish
    /// disposing.
    /// </remarks>
    public void ReportServiceStopped()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _running = false;
            _status.dwServiceType = ServiceWin32OwnProcess;
            _status.dwCurrentState = ServiceStopped;
            _status.dwControlsAccepted = 0;
            _status.dwWin32ExitCode = 0;
            _status.dwServiceSpecificExitCode = 0;
            _status.dwCheckPoint = 0;
            _status.dwWaitHint = 0;

            if (_statusHandle != nint.Zero && !SetServiceStatus(_statusHandle, ref _status))
            {
                _log.Write(
                    $"scm: SetServiceStatus(Stopped) failed (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
    }

    /// <summary>
    /// Releases the reporter, sending the terminal SERVICE_STOPPED status if nothing has yet.
    /// </summary>
    /// <remarks>
    /// A safety net for the shutdown path that never reached its explicit report: anything left in
    /// Starting, Running or Stopping keeps the service marked as failed/stop-pending after the
    /// process is already gone. Idempotent - a report that was already sent is not repeated.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReportServiceStopped();
    }


    /// <summary>
    /// The SCM's entry callback, invoked on the service-main thread created by the dispatcher.
    /// </summary>
    /// <remarks>
    /// Must return quickly: the SCM waits for ServiceMain to return before it considers the
    /// service started, and it holds that thread. The renderer therefore runs as a detached task
    /// from here, and reports its own <see cref="ServiceState.Running"/> once it is listening.
    /// </remarks>
    private void OnServiceMain(int argc, nint argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handler, nint.Zero);
        if (_statusHandle == nint.Zero)
        {
            _log.Write(
                $"scm: RegisterServiceCtrlHandler failed (Win32 error {Marshal.GetLastWin32Error()}); " +
                "status will not be reported and the SCM will time this start out");
            return;
        }

        _log.Write($"scm: service main entered (status handle 0x{_statusHandle:X})");
        Report(ServiceState.Starting);

        // Hand control to the caller's startup, which will report Running when it is ready.
        _serviceMainCallback();
    }

    private void Report(ServiceState state)
    {
        _status.dwServiceType = ServiceWin32OwnProcess;
        _status.dwCurrentState = (int)state;
        _status.dwControlsAccepted = state == ServiceState.Running
            ? ServiceAcceptStop | ServiceAcceptShutdown
            : 0;
        _status.dwWin32ExitCode = 0;
        _status.dwServiceSpecificExitCode = 0;
        _status.dwCheckPoint = 0;
        _status.dwWaitHint = 0;

        if (_statusHandle != nint.Zero && !SetServiceStatus(_statusHandle, ref _status))
        {
            var error = Marshal.GetLastWin32Error();

            // ERROR_INVALID_HANDLE means the SCM has already torn the status handle down, which is
            // normal once a stop is under way: the terminal SERVICE_STOPPED report is the one that
            // matters, and this one is redundant rather than a failure.
            if (error != ErrorInvalidHandle)
            {
                _log.Write(
                    $"scm: SetServiceStatus({state}) failed (Win32 error {error})");
            }
        }
    }

    /// <summary>
    /// SCM control callback. Runs on a thread pool thread, so it must not block.
    /// </summary>
    /// <returns>
    /// <c>NO_ERROR</c> unless the control was not handled. The native prototype
    /// (<c>LPHANDLER_FUNCTION_EX</c>) returns <c>DWORD</c>; a void delegate leaves the return register
    /// undefined and the SCM then crashes reading it, so the signature must match exactly.
    /// </returns>
    private uint OnControl(int control, int eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceControlStop:
            case ServiceControlShutdown:
                _log.Write($"scm: stop requested (control {control})");
                _stopRequested();
                break;
        }

        return NoError;
    }

    /// <summary>
    /// True when this process was launched by the Service Control Manager.
    /// </summary>
    /// <remarks>
    /// Detected from the parent process rather than assumed from <c>--service</c>, because that
    /// flag is also what a developer passes when starting the binary by hand. It remains a
    /// fallback only for the window where the parent has already exited.
    /// </remarks>
    private bool IsServiceHost
        => _isServiceHost ??= ServiceLaunchDetector.IsServiceHost();

    private bool? _isServiceHost;

    // Kept alive for the process lifetime; see the constructor.
    private readonly Action _serviceMainCallback;
    private readonly ServiceControlHandler _handler;


    // SERVICE_STATUS constants, spelled out because they are the wire format to the SCM and a
    // wrong value there is rejected with ERROR_INVALID_DATA rather than being diagnosed.
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const uint NoError = 0;
    private const int FailedServiceControllerConnect = 1064;

    /// <summary>SERVICE_STOPPED (1). The terminal state the SCM waits for.</summary>
    private const int ServiceStopped = 1;

    /// <summary>ERROR_INVALID_HANDLE (6). Expected once the SCM has closed the status handle.</summary>
    private const int ErrorInvalidHandle = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpServiceName;

        public ServiceMainDelegate? lpServiceProc;
    }

    private delegate void ServiceMainDelegate(int argc, nint argv);

    private delegate uint ServiceControlHandler(int control, int eventType, nint eventData, nint context);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] table);

    // The Ex variant is what the SCM actually calls back through: it supplies the event type and
    // context the handler signature declares. The plain variant only passes the control code, so a
    // four-parameter delegate registered with it never receives anything usable.
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode)]
    private static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName, ServiceControlHandler callback, nint context);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "SetServiceStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint statusHandle, ref SERVICE_STATUS status);
}

/// <summary>
/// Decides whether this process was started by the Service Control Manager.
/// </summary>
/// <remarks>
/// The parent image name is the signal. It is kept separate from the process API so the rule can be
/// reasoned about (and tested) without a real service: <see cref="IsServiceHost"/> reads the
/// current process, <see cref="IsServiceControlManager"/> is the pure comparison.
/// </remarks>
public static class ServiceLaunchDetector
{
    /// <summary>
    /// True when the current process's parent is the Service Control Manager, or the
    /// <c>--service</c> flag was passed explicitly.
    /// </summary>
    public static bool IsServiceHost()
        => IsServiceControlManager(ParentImageName) || WasLaunchedWithServiceFlag();

    /// <summary>
    /// True when <paramref name="parentImageName"/> is the Service Control Manager itself.
    /// </summary>
    public static bool IsServiceControlManager(string? parentImageName)
        => string.Equals(parentImageName, "services.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>File name of the current process's parent, or null when it cannot be read.</summary>
    public static string? ParentImageName
    {
        get
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                var parentId = ParentProcessId(self.Id);
                return parentId > 0 ? ImageNameOf(parentId) : null;
            }
            catch (Exception)
            {
                // A process snapshot can fail for reasons that have nothing to do with being a
                // service; treating that as "not a service" only loses the status report, which the
                // --service flag already covers.
                return null;
            }
        }
    }

    /// <summary>
    /// True when <c>--service</c> is on this process's command line: a backstop for the window
    /// where the parent has already exited before it could be inspected.
    /// </summary>
    public static bool WasLaunchedWithServiceFlag()
    {
        try
        {
            return Environment.GetCommandLineArgs().Contains("--service");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ImageNameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            // The parent exited between the snapshot and the lookup.
            return null;
        }
    }

    /// <summary>Parent process id, or 0 when it cannot be read.</summary>
    private static int ParentProcessId(int processId)
    {
        var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
        using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return 0;
        }

        if (!Process32First(snapshot, ref entry))
        {
            return 0;
        }

        do
        {
            if (entry.th32ProcessID == processId)
            {
                return entry.th32ParentProcessID;
            }
        }
        while (Process32Next(snapshot, ref entry));

        return 0;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public int th32DefaultHeapID;
        public int th32ModuleID;
        public int th32Threads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);
}
