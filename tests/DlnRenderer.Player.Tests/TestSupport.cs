using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

/// <summary>
/// A fact that only runs when the native libmpv DLL is present in the test output.
/// The skip message explains how to provide it, so a missing binary never looks like a pass.
/// </summary>
public sealed class MpvFactAttribute : FactAttribute
{
    public MpvFactAttribute()
    {
        if (!MpvRuntime.IsAvailable())
        {
            Skip = $"libmpv-2.dll not found at {MpvRuntime.ExpectedPath}. Run scripts/fetch-libmpv.ps1.";
        }
    }
}

internal static class TestMedia
{
    /// <summary>Writes a 16-bit PCM mono sine wave so audio playback needs no external assets.</summary>
    public static string CreateToneWav(string directory, double seconds = 4.0, int sampleRate = 44100)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"tone-{Guid.NewGuid():N}.wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var sampleCount = (int)(sampleRate * seconds);
        var dataSize = sampleCount * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * t) * 8000));
        }

        return path;
    }

    /// <summary>Path of the committed video fixture copied next to the test assembly.</summary>
    public static string VideoFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Media", "sample-video.mp4");
}
