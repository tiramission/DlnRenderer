using CommunityToolkit.Mvvm.ComponentModel;
using DlnRenderer.Core.Ipc;
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
    private string _statusText = string.Empty;
    private bool _hasStatus;
    private bool _isError;
    private bool _busy;

    public SettingsViewModel()
    {
        var settings = App.Settings;
        _deviceName = settings.DeviceName;
        _startWithWindows = settings.StartWithWindows;
        _closeToTray = settings.CloseToTray;
        _hardwareDecoding = settings.HardwareDecoding;
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool HardwareDecoding
    {
        get => _hardwareDecoding;
        set => SetProperty(ref _hardwareDecoding, value);
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
        private set => SetProperty(ref _busy, value);
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
            }.Normalize();

            if (!RendererSettings.TryValidate(settings, out var validationError))
            {
                Show(validationError ?? "设置无效。", isError: true);
                return;
            }

            if (!StartupRegistration.TryApply(settings.StartWithWindows, Environment.ProcessPath, out var startupError))
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
            Show("已保存。设备名称与硬件解码将在重启服务后生效。", isError: false);
        }
        finally
        {
            Busy = false;
        }

        await Task.CompletedTask;
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

    public void DismissStatus() => HasStatus = false;

    private void Show(string message, bool isError)
    {
        StatusText = message;
        _isError = isError;
        HasStatus = true;
        OnPropertyChanged(nameof(Severity));
    }
}
