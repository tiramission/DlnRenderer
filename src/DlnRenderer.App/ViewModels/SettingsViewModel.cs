using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Core.Network;
using DlnRenderer.Core.Settings;
using DlnRenderer.Services;
using Microsoft.UI.Xaml.Controls;

namespace DlnRenderer.ViewModels;

/// <summary>
/// Edits <see cref="RendererSettings"/> and writes them through <see cref="SettingsStore"/>.
/// </summary>
/// <remarks>
/// Saving does not restart the service: the device name and hardware decoding are read at service
/// start, so the page says so and offers an explicit restart. Silently restarting would interrupt a
/// cast that is currently playing.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private string _deviceName;
    private bool _startWithWindows;
    private bool _closeToTray;
    private bool _hardwareDecoding;
    private DisplayMode _displayMode;
    private string _statusText = string.Empty;
    private bool _hasStatus;
    private bool _isError;
    private bool _busy;
    private bool _firewallBusy;

    public SettingsViewModel()
    {
        App.Firewall.Changed += OnFirewallChanged;

        var settings = App.Settings;
        _deviceName = settings.DeviceName;
        _startWithWindows = settings.StartWithWindows;
        _closeToTray = settings.CloseToTray;
        _hardwareDecoding = settings.HardwareDecoding;
        _displayMode = settings.DisplayMode;
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (SetProperty(ref _deviceName, value))
            {
                NotifyDirty();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                NotifyDirty();
            }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
            {
                NotifyDirty();
            }
        }
    }

    public bool HardwareDecoding
    {
        get => _hardwareDecoding;
        set
        {
            if (SetProperty(ref _hardwareDecoding, value))
            {
                NotifyDirty();
            }
        }
    }

    /// <summary>
    /// Selected presentation mode. The two radio buttons bind to the two views of this one value, so
    /// they can never both be checked or both be clear.
    /// </summary>
    public DisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode == value)
            {
                return;
            }

            _displayMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFullscreenMode));
            OnPropertyChanged(nameof(IsEmbeddedMode));
            NotifyDirty();
        }
    }

    /// <summary>Radio button view: setting false is ignored, the group already unchecked it.</summary>
    public bool IsFullscreenMode
    {
        get => _displayMode == DisplayMode.Fullscreen;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.Fullscreen;
            }
        }
    }

    /// <summary>Radio button view: setting false is ignored, the group already unchecked it.</summary>
    public bool IsEmbeddedMode
    {
        get => _displayMode == DisplayMode.Embedded;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.Embedded;
            }
        }
    }

    /// <summary>
    /// True when the page differs from what is on disk. The save button and the footer hint both
    /// read this, so an edit that cannot be saved (validation) never looks applied.
    /// </summary>
    public bool IsDirty
    {
        get
        {
            var saved = App.Settings;
            return !string.Equals((DeviceName ?? string.Empty).Trim(), saved.DeviceName, StringComparison.Ordinal)
                || StartWithWindows != saved.StartWithWindows
                || CloseToTray != saved.CloseToTray
                || HardwareDecoding != saved.HardwareDecoding
                || DisplayMode != saved.DisplayMode;
        }
    }

    public bool CanSave => IsDirty && !Busy;

    /// <summary>One line for the command bar: what the state of the page is, in words.</summary>
    public string FooterHint
    {
        get
        {
            if (Busy)
            {
                return "正在处理…";
            }

            return IsDirty ? "有未保存的更改" : "所有更改已保存";
        }
    }

    /// <summary>Folder holding settings.json, the device UUID and the service logs.</summary>
    public string DataFolder { get; } = Path.GetDirectoryName(SettingsStore.DefaultPath) ?? string.Empty;

    /// <summary>One line naming what the firewall does or does not let through right now.</summary>
    public string FirewallStatusText
    {
        get
        {
            var exposure = App.Firewall.Exposure;
            if (_firewallBusy || App.Firewall.IsProbing)
            {
                return "正在检查防火墙…";
            }

            if (!exposure.IsKnown)
            {
                return "无法读取防火墙状态";
            }

            if (!exposure.HasActiveNetwork)
            {
                return "未连接到网络";
            }

            var missing = new List<string>(2);
            if (exposure.Ssdp != FirewallPortState.Covered)
            {
                missing.Add($"UDP {FirewallCoverage.SsdpPort}");
            }

            if (exposure.Http != FirewallPortState.Covered)
            {
                missing.Add($"TCP {exposure.HttpPort}");
            }

            return missing.Count == 0
                ? $"已放行 UDP {FirewallCoverage.SsdpPort} 与 TCP {exposure.HttpPort}"
                : $"未放行 {string.Join(" / ", missing)}";
        }
    }

    /// <summary>Why the status is what it is, and what the fix costs. Silence is not an option here.</summary>
    public string FirewallDetailText
    {
        get
        {
            var exposure = App.Firewall.Exposure;
            if (!exposure.IsKnown)
            {
                return "防火墙策略读不出来。如果控制端发现不了本设备，请用管理员 PowerShell 运行 tools/package/firewall-rules.ps1。";
            }

            if (!exposure.HasActiveNetwork)
            {
                return "连上局域网后才会检查；发现与投送都需要网络。";
            }

            if (exposure.IsOpen)
            {
                return "控制端可以正常发现并投送媒体。规则只对“专用/域”网络开放，公用网络下不暴露。";
            }

            return exposure.IsProfileRestricted
                ? "规则已存在，但当前网络被 Windows 标记为“公用”，规则不会生效。在“设置 > 网络和 Internet”里把该网络改为专用，或改用“允许发现”重新添加。"
                : "添加规则需要一次管理员授权（UAC）。规则只对“专用/域”网络开放，不会在公用网络上暴露本机。";
        }
    }

    public bool CanRefreshFirewall => !_firewallBusy && !App.Firewall.IsProbing;

    /// <summary>
    /// Also offered when the policy could not be read: the rules are idempotent and named, so adding
    /// them is a safe escape hatch for someone whose device is simply not showing up.
    /// </summary>
    public bool CanAllowDiscovery => !_firewallBusy
        && !App.Firewall.IsProbing
        && (App.Firewall.Exposure.NeedsAttention || !App.Firewall.Exposure.IsKnown);

    /// <summary>Which deployment shape is running; the settings that apply differ between them.</summary>
    public string DeploymentText => StartupRegistration.IsPackaged ? "MSIX 用户级包" : "绿色包 / 开发运行";

    public string VersionText
    {
        get
        {
            var version = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            }

            // The SDK appends the source revision ("0.1.0+abc123"); the page shows the version only.
            var plus = version!.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? version[..plus] : version;
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetProperty(ref _hasStatus, value);
    }

    public InfoBarSeverity Severity => _isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                NotifyDirty();
            }
        }
    }

    /// <summary>
    /// Validates, applies the startup preference and saves. Rejected input is shown verbatim instead
    /// of being truncated or ignored.
    /// </summary>
    public async Task SaveAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            var settings = new RendererSettings
            {
                DeviceName = DeviceName,
                StartWithWindows = StartWithWindows,
                CloseToTray = CloseToTray,
                HardwareDecoding = HardwareDecoding,
                DisplayMode = DisplayMode,
            }.Normalize();

            if (!RendererSettings.TryValidate(settings, out var validationError))
            {
                Show(validationError ?? "设置无效。", isError: true);
                return;
            }

            var (startupOk, startupError) = await StartupRegistration
                .ApplyAsync(settings.StartWithWindows, Environment.ProcessPath);
            if (!startupOk)
            {
                Show(startupError ?? "无法修改开机启动设置。", isError: true);
                return;
            }

            var saved = App.SettingsStore.Save(settings);
            if (!saved.Success)
            {
                Show(saved.Error ?? "无法保存设置。", isError: true);
                return;
            }

            App.ReloadSettings();
            DeviceName = settings.DeviceName;

            // The display mode is a window concern rather than a player one, so it is applied live
            // instead of waiting for a service restart. Without a reserved rectangle the service
            // stays fullscreen until the player page reports one.
            await ApplyDisplayModeAsync(settings.DisplayMode);

            NotifyDirty();
            Show("已保存。设备名称与硬件解码在重启服务后生效，显示方式已立即生效。", isError: false);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Re-probes the firewall; the result arrives through <see cref="App.Firewall"/>.</summary>
    public Task RefreshFirewallAsync() => App.Firewall.RefreshAsync();

    /// <summary>
    /// Adds the inbound rules through one UAC prompt. A refused prompt, a refused policy or a probe
    /// that still says "blocked" afterwards is reported verbatim, never as success.
    /// </summary>
    public async Task AllowDiscoveryAsync()
    {
        if (!CanAllowDiscovery)
        {
            return;
        }

        _firewallBusy = true;
        NotifyFirewallChanged();
        Show("正在请求管理员授权…", isError: false);

        try
        {
            var (succeeded, message) = await App.Firewall.AllowDiscoveryAsync();
            Show(message, isError: !succeeded);
        }
        finally
        {
            _firewallBusy = false;
            NotifyFirewallChanged();
        }
    }

    /// <summary>Detaches from the shared monitor; the page is recreated on every navigation.</summary>
    public void Dispose() => App.Firewall.Changed -= OnFirewallChanged;

    private void OnFirewallChanged(object? sender, EventArgs e) => NotifyFirewallChanged();

    private void NotifyFirewallChanged()
    {
        OnPropertyChanged(nameof(FirewallStatusText));
        OnPropertyChanged(nameof(FirewallDetailText));
        OnPropertyChanged(nameof(CanRefreshFirewall));
        OnPropertyChanged(nameof(CanAllowDiscovery));
    }

    /// <summary>Stops and starts the service so the saved settings take effect.</summary>
    public async Task RestartServiceAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            Show("正在重启后台服务…", isError: false);

            try
            {
                if (App.Client.IsConnected)
                {
                    await App.Client.SendAsync(IpcCommands.Shutdown);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write($"settings restart: shutdown failed: {ex.Message}");
            }

            // The service needs a moment to release the pipe and its ports.
            await Task.Delay(1500);

            var connected = await App.Client.ConnectAsync(startIfMissing: true);
            Show(
                connected ? "后台服务已重启，新设置已生效。" : "后台服务未能重启，请检查服务日志。",
                isError: !connected);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Stops the service and exits the UI.</summary>
    public async Task ExitAsync()
    {
        Show("正在退出…", isError: false);
        await App.ExitApplicationAsync();
    }

    /// <summary>
    /// Pushes the display mode to the running service.
    /// </summary>
    /// <remarks>
    /// Deliberately best-effort: the mode is persisted either way, and a service that is restarting
    /// reads it from the settings file on its own start. Failing the save because the pipe is down
    /// would block a change the user can see applied the next time the window reports its geometry.
    /// </remarks>
    private static async Task ApplyDisplayModeAsync(DisplayMode mode)
    {
        try
        {
            if (!App.Client.IsConnected)
            {
                return;
            }

            await App.Client.SendAsync(
                IpcCommands.SetVideoTarget,
                videoTarget: new VideoTarget { Mode = mode });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"apply display mode failed: {ex.Message}");
        }
    }

    public void DismissStatus() => HasStatus = false;

    /// <summary>Shows a failure raised by the page itself, e.g. a folder that cannot be opened.</summary>
    public void ReportError(string message) => Show(message, isError: true);

    private void NotifyDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(FooterHint));
    }

    private void Show(string message, bool isError)
    {
        StatusText = message;
        _isError = isError;
        HasStatus = true;
        OnPropertyChanged(nameof(Severity));
    }
}
