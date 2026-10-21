using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DlnRenderer.Services;

/// <summary>
/// Applies the "start with Windows" preference for whichever deployment shape is running.
/// </summary>
/// <remarks>
/// <para>
/// Unpackaged (portable build, development): the current user's Run key. Deliberately HKCU only:
/// starting with Windows is a per-user preference and must not require administrator rights.
/// </para>
/// <para>
/// Packaged (MSIX): the manifest's <c>windows.startupTask</c> through the <see cref="StartupTask"/>
/// API. A Run key would point into <c>C:\Program Files\WindowsApps\&lt;version&gt;\</c>, a path that
/// changes on every update and that the user cannot even list, so it would silently break after
/// the first upgrade.
/// </para>
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DlnRenderer";
    private const string StartupTaskId = "DlnRendererStartup";

    private const int AppModelErrorNoPackage = 15700;

    /// <summary>True when the process has package identity (installed from the MSIX).</summary>
    public static bool IsPackaged
    {
        get
        {
            var length = 0;
            // ERROR_INSUFFICIENT_BUFFER (122) means "has a package, tell me how long the name is";
            // APPMODEL_ERROR_NO_PACKAGE means plain unpackaged process.
            var result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage;
        }
    }

    /// <summary>
    /// Applies the preference. Returns false with a user-readable reason instead of throwing, so the
    /// settings page can report a refusal.
    /// </summary>
    public static async Task<(bool Success, string? Error)> ApplyAsync(bool enabled, string? executablePath)
    {
        return IsPackaged
            ? await ApplyPackagedAsync(enabled).ConfigureAwait(true)
            : TryApplyRunKey(enabled, executablePath);
    }

    private static async Task<(bool, string?)> ApplyPackagedAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);

            if (!enabled)
            {
                task.Disable();
                return (true, null);
            }

            switch (task.State)
            {
                case StartupTaskState.Enabled:
                    return (true, null);

                case StartupTaskState.DisabledByUser:
                    // Windows does not let an app override a choice the user made in Settings;
                    // pretending it worked would leave the toggle on and nothing happening.
                    return (false, "开机启动已被你在“设置 > 应用 > 启动”中关闭，请在那里重新开启。");

                case StartupTaskState.DisabledByPolicy:
                    return (false, "开机启动被组织策略禁用。");

                default:
                    var state = await task.RequestEnableAsync();
                    return state == StartupTaskState.Enabled
                        ? (true, null)
                        : (false, $"无法启用开机启动（状态：{state}）。");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (false, $"无法修改开机启动设置：{ex.Message}");
        }
    }

    private static (bool, string?) TryApplyRunKey(bool enabled, string? executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return (false, "无法打开当前用户的启动项注册表键。");
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return (true, null);
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return (false, "找不到应用程序可执行文件，无法设置开机启动。");
            }

            // Quoted so a path containing spaces still parses as one command.
            key.SetValue(ValueName, $"\"{executablePath}\"");
            return (true, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return (false, $"无法修改开机启动设置：{ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
