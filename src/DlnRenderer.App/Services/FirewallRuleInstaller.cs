using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DlnRenderer.Core.Network;

namespace DlnRenderer.Services;

/// <summary>Creates the inbound firewall rules the renderer needs, through one elevated PowerShell run.</summary>
/// <remarks>
/// <para>
/// The rules cannot be created in-process: adding them to the local policy needs administrator
/// rights, and the app deliberately does not run elevated. So the work is handed to a short
/// PowerShell script started with the <c>runas</c> verb, which shows the standard UAC prompt and
/// nothing else.
/// </para>
/// <para>
/// Names and scope match <c>tools/package/firewall-rules.ps1</c> on purpose: the portable installer
/// and the uninstaller address the rules by display name, so the two paths must agree or an
/// in-app repair would leave rules behind that uninstall does not remove.
/// </para>
/// </remarks>
internal static class FirewallRuleInstaller
{
    /// <summary>A UAC prompt nobody answered is not worth waiting on forever.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const int ErrorCancelled = 1223;

    /// <summary>Display name of the SSDP rule; shared with the portable installer scripts.</summary>
    public const string SsdpRuleName = "DlnRenderer SSDP (UDP 1900)";

    public static string ServiceRuleName(int httpPort) => $"DlnRenderer Service (TCP {httpPort})";

    /// <summary>
    /// Adds the rules. Returns a user-readable reason instead of throwing, so the settings page can
    /// report a cancelled prompt, a policy refusal or a missing cmdlet without a stack trace.
    /// </summary>
    /// <param name="httpPort">Port the service listens on.</param>
    /// <param name="servicePath">Service executable, used to scope the rule when unpackaged.</param>
    /// <param name="packageFamilyName">Package family name, used to scope the rule when packaged.</param>
    public static async Task<(bool Succeeded, string? Error)> InstallAsync(
        int httpPort,
        string? servicePath,
        string? packageFamilyName,
        CancellationToken cancellationToken = default)
    {
        var (script, logPath) = BuildScript(httpPort, servicePath, packageFamilyName);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // UseShellExecute is what makes Verb work; without it Windows ignores "runas" and the
            // script would run unelevated and silently fail. The window is hidden because the script
            // writes its outcome to the log file below instead of asking the user to read a console.
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "无法启动管理员进程，防火墙规则未添加。");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var outcome = ReadLog(logPath);
            return process.ExitCode == 0
                ? (true, null)
                : (false, outcome ?? $"添加防火墙规则失败（退出码 {process.ExitCode}）。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // UAC "No" is a normal answer, not an error worth logging as a failure.
            return (false, "已取消管理员授权，防火墙规则未添加。");
        }
        catch (OperationCanceledException)
        {
            return (false, "等待管理员授权超时，防火墙规则未添加。");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"firewall rules install failed: {ex.Message}");
            return (false, $"无法添加防火墙规则：{ex.Message}");
        }
        finally
        {
            TryDelete(logPath);
        }
    }

    /// <summary>
    /// The script and the log file it writes. Kept separate from the process handling so the text can
    /// be inspected (and syntax-checked) without elevation.
    /// </summary>
    internal static (string Script, string LogPath) BuildScript(
        int httpPort,
        string? servicePath,
        string? packageFamilyName)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"DlnRenderer-firewall-{Guid.NewGuid():N}.log");
        var serviceRule = ServiceRuleName(httpPort);
        var script = new StringBuilder();

        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$log = {Quote(logPath)}");
        script.AppendLine("try {");
        script.AppendLine($"  $ssdp = {Quote(SsdpRuleName)}");
        script.AppendLine($"  $service = {Quote(serviceRule)}");

        // Refresh our own rules only: anything else the user installed stays untouched.
        script.AppendLine("  Get-NetFirewallRule -DisplayName $ssdp, $service -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        script.AppendLine(
            $"  New-NetFirewallRule -DisplayName $ssdp -Direction Inbound -Protocol UDP -LocalPort {FirewallCoverage.SsdpPort} " +
            "-Action Allow -Profile Private,Domain " +
            "-Description 'DlnRenderer: DLNA discovery (SSDP M-SEARCH responses, alive/byebye).' | Out-Null");

        // Scope the HTTP rule to our own binary so the port is not opened for every program. The
        // packaged path contains the version and changes on every upgrade, so a package-scoped rule
        // is preferred there; the program path is the fallback if that scope is rejected.
        script.AppendLine(
            $"  $rule = @{{ DisplayName = $service; Direction = 'Inbound'; Protocol = 'TCP'; LocalPort = {httpPort}; " +
            "Action = 'Allow'; Profile = 'Private,Domain'; " +
            "Description = 'DlnRenderer: device description, SOAP control and GENA callbacks.' }");
        script.AppendLine($"  $program = {Quote(servicePath)}");

        if (!string.IsNullOrWhiteSpace(packageFamilyName))
        {
            script.AppendLine($"  $rule['PackageFamilyName'] = {Quote(packageFamilyName)}");
            script.AppendLine("  try { New-NetFirewallRule @rule | Out-Null }");
            script.AppendLine("  catch {");
            script.AppendLine("    $rule.Remove('PackageFamilyName')");
            script.AppendLine("    if (-not $program) { throw }");
            script.AppendLine("    $rule['Program'] = $program");
            script.AppendLine("    New-NetFirewallRule @rule | Out-Null");
            script.AppendLine("  }");
        }
        else
        {
            script.AppendLine("  if ($program) { $rule['Program'] = $program }");
            script.AppendLine("  New-NetFirewallRule @rule | Out-Null");
        }

        script.AppendLine($"  'ok' | Set-Content -LiteralPath $log -Encoding UTF8");
        script.AppendLine("  exit 0");
        script.AppendLine("}");
        script.AppendLine("catch {");
        script.AppendLine("  \"error: $($_.Exception.Message)\" | Set-Content -LiteralPath $log -Encoding UTF8");
        script.AppendLine("  exit 1");
        script.AppendLine("}");
        return (script.ToString(), logPath);
    }

    /// <summary>Single-quoted PowerShell literal; a quote inside is doubled.</summary>
    private static string Quote(string? value) => $"'{value?.Replace("'", "''")}'";

    private static string? ReadLog(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return null;
            }

            var text = File.ReadAllText(logPath).Trim();
            if (text.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return text.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                ? text["error:".Length..].Trim()
                : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file must not turn a successful rule change into a failure.
        }
    }
}
