using Microsoft.Win32;

namespace DlnRenderer.Services;

/// <summary>
/// Registers or removes the UI in the current user's Run key.
/// </summary>
/// <remarks>
/// Deliberately HKCU only: starting with Windows is a per-user preference and must not require
/// administrator rights. This is the unpackaged behaviour; a packaged build will need a startup
/// task instead, which is recorded as a packaging item rather than guessed at here.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DlnRenderer";

    /// <summary>
    /// Applies the preference. Returns false with a user-readable reason instead of throwing, so the
    /// settings page can report a denied registry write.
    /// </summary>
    public static bool TryApply(bool enabled, string? executablePath, out string? error)
    {
        error = null;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开当前用户的启动项注册表键。";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                error = "找不到应用程序可执行文件，无法设置开机启动。";
                return false;
            }

            // Quoted so a path containing spaces still parses as one command.
            key.SetValue(ValueName, $"\"{executablePath}\"");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = $"无法修改开机启动设置：{ex.Message}";
            return false;
        }
    }
}
