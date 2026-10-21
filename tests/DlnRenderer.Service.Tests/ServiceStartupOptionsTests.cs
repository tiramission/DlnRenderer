using System.Net;
using DlnRenderer.Service;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Command-line parsing for the service.
/// </summary>
/// <remarks>
/// These existed only inside <c>Program.TryParse</c> before it was split up for readability, so the
/// accepted flags, their validation and the exact error messages had no regression cover at all.
/// Every assertion below pins behaviour that was already shipped, not new behaviour.
/// </remarks>
public class ServiceStartupOptionsTests
{
    [Fact]
    public void No_arguments_yield_the_documented_defaults()
    {
        Assert.True(ServiceStartupOptions.TryParse([], out var options, out var error));

        Assert.Null(error);
        Assert.Equal("DlnRenderer", options.FriendlyName);
        Assert.Equal(ServiceStartupOptions.DefaultHttpPort, options.HttpPort);
        Assert.True(options.Ssdp);
        Assert.True(options.HardwareDecoding);
        Assert.Null(options.ListenAddress);
        Assert.Null(options.ExitAfter);
    }

    [Theory]
    [InlineData("--no-ssdp")]
    [InlineData("--no-hwdec")]
    public void Boolean_switches_are_accepted(string flag)
    {
        Assert.True(ServiceStartupOptions.TryParse([flag], out var options, out _));

        if (flag == "--no-ssdp")
        {
            Assert.False(options.Ssdp);
        }
        else
        {
            Assert.False(options.HardwareDecoding);
        }
    }

    [Fact]
    public void No_log_disables_file_logging()
    {
        Assert.True(ServiceStartupOptions.TryParse(["--no-log"], out var options, out _));
        Assert.Null(options.LogPath);
    }

    [Theory]
    [InlineData("18090", 18090)]
    [InlineData("0", 0)]
    [InlineData("65535", 65535)]
    public void Http_port_accepts_the_valid_range(string value, int expected)
    {
        Assert.True(ServiceStartupOptions.TryParse(["--http-port", value], out var options, out _));
        Assert.Equal(expected, options.HttpPort);
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void Http_port_rejects_out_of_range_and_malformed_values(string value)
    {
        Assert.False(ServiceStartupOptions.TryParse(["--http-port", value], out _, out var error));
        Assert.Contains("65535", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Http_port_without_a_value_is_rejected()
    {
        Assert.False(ServiceStartupOptions.TryParse(["--http-port"], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Device_name_is_trimmed()
    {
        Assert.True(ServiceStartupOptions.TryParse(["--device-name", "  Living Room  "], out var options, out _));
        Assert.Equal("Living Room", options.FriendlyName);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Blank_device_names_are_rejected(string value)
    {
        Assert.False(ServiceStartupOptions.TryParse(["--device-name", value], out _, out var error));
        Assert.Contains("--device-name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Listen_address_accepts_an_ipv4_address()
    {
        Assert.True(ServiceStartupOptions.TryParse(["--listen-address", "192.168.1.9"], out var options, out _));
        Assert.Equal(IPAddress.Parse("192.168.1.9"), options.ListenAddress);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.1.1.1")]
    public void Listen_address_rejects_malformed_values(string value)
    {
        Assert.False(ServiceStartupOptions.TryParse(["--listen-address", value], out _, out var error));
        Assert.Contains("--listen-address", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_path_is_stored_verbatim()
    {
        Assert.True(ServiceStartupOptions.TryParse(["--log", @"C:\logs\service.log"], out var options, out _));
        Assert.Equal(@"C:\logs\service.log", options.LogPath);
    }

    [Fact]
    public void Exit_after_requires_a_positive_number()
    {
        Assert.True(ServiceStartupOptions.TryParse(["--exit-after", "2.5"], out var options, out _));
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.ExitAfter);

        Assert.False(ServiceStartupOptions.TryParse(["--exit-after", "0"], out _, out _));
        Assert.False(ServiceStartupOptions.TryParse(["--exit-after", "-1"], out _, out _));
        Assert.False(ServiceStartupOptions.TryParse(["--exit-after", "abc"], out _, out _));
        Assert.False(ServiceStartupOptions.TryParse(["--exit-after"], out _, out _));
    }

    [Fact]
    public void Unknown_arguments_are_rejected_and_named()
    {
        Assert.False(ServiceStartupOptions.TryParse(["--bogus"], out _, out var error));
        Assert.Contains("--bogus", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Flags_combine()
    {
        Assert.True(ServiceStartupOptions.TryParse(
            ["--no-ssdp", "--no-hwdec", "--device-name", "Bedroom", "--http-port", "19000"],
            out var options,
            out _));

        Assert.False(options.Ssdp);
        Assert.False(options.HardwareDecoding);
        Assert.Equal("Bedroom", options.FriendlyName);
        Assert.Equal(19000, options.HttpPort);
    }

    [Fact]
    public void A_later_flag_overrides_an_earlier_one()
    {
        Assert.True(ServiceStartupOptions.TryParse(
            ["--device-name", "First", "--device-name", "Second"],
            out var options,
            out _));

        Assert.Equal("Second", options.FriendlyName);
    }
}
