using DlnRenderer.Core.Settings;

namespace DlnRenderer.Core.Tests;

/// <summary>
/// Persistence rules for <see cref="SettingsStore"/>.
/// </summary>
/// <remarks>
/// The settings file is shared by the UI and the service and can be edited by hand, so the tests
/// treat it as untrusted input: defaults on anything unreadable, validation before anything is
/// written, and no half-written file left behind.
/// </remarks>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dlnr-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_missing_file_yields_defaults()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(RendererSettings.DefaultDeviceName, settings.DeviceName);
        Assert.True(settings.CloseToTray);
        Assert.True(settings.HardwareDecoding);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void Settings_round_trip_through_the_file()
    {
        var store = new SettingsStore(SettingsPath);
        var expected = new RendererSettings
        {
            DeviceName = "客厅",
            StartWithWindows = true,
            CloseToTray = false,
            HardwareDecoding = false,
        };

        var saved = store.Save(expected);
        Assert.True(saved.Success, saved.Error);

        var loaded = store.Load();
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public void The_file_is_readable_json_with_the_expected_names()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.True(store.Save(new RendererSettings { DeviceName = "Bedroom" }).Success);

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"deviceName\": \"Bedroom\"", json, StringComparison.Ordinal);
        Assert.Contains("\"closeToTray\": true", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"deviceName\": 5}")]
    public void A_corrupt_or_wrongly_shaped_file_falls_back_to_defaults(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(RendererSettings.Default, settings);
    }

    [Fact]
    public void An_invalid_device_name_in_the_file_is_replaced_by_defaults()
    {
        File.WriteAllText(SettingsPath, "{\"deviceName\":\"   \"}");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(RendererSettings.Default, settings);
    }

    [Fact]
    public void Whitespace_around_a_stored_name_is_trimmed_on_load()
    {
        File.WriteAllText(SettingsPath, "{\"deviceName\":\"  Living Room  \"}");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal("Living Room", settings.DeviceName);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        File.WriteAllText(SettingsPath, "{\"deviceName\":\"Kitchen\",\"fromTheFuture\":123}");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal("Kitchen", settings.DeviceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    public void An_invalid_device_name_is_rejected_and_nothing_is_written(string name)
    {
        var store = new SettingsStore(SettingsPath);

        var result = store.Save(new RendererSettings { DeviceName = name });

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_rejected()
    {
        var store = new SettingsStore(SettingsPath);
        var name = new string('a', RendererSettings.MaxDeviceNameLength + 1);

        var result = store.Save(new RendererSettings { DeviceName = name });

        Assert.False(result.Success);
        Assert.Contains(RendererSettings.MaxDeviceNameLength.ToString(), result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Saving_creates_the_directory_and_leaves_no_temporary_file()
    {
        var nested = Path.Combine(_directory, "a", "b", "settings.json");
        var store = new SettingsStore(nested);

        Assert.True(store.Save(new RendererSettings { DeviceName = "Study" }).Success);

        Assert.True(File.Exists(nested));
        Assert.False(File.Exists(nested + ".tmp"));
    }

    [Fact]
    public void Saving_replaces_an_existing_file()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.True(store.Save(new RendererSettings { DeviceName = "First" }).Success);
        Assert.True(store.Save(new RendererSettings { DeviceName = "Second" }).Success);

        Assert.Equal("Second", store.Load().DeviceName);
    }

    [Fact]
    public void A_device_name_of_exactly_the_limit_is_accepted()
    {
        var store = new SettingsStore(SettingsPath);
        var name = new string('a', RendererSettings.MaxDeviceNameLength);

        Assert.True(store.Save(new RendererSettings { DeviceName = name }).Success);
        Assert.Equal(name, store.Load().DeviceName);
    }
}
