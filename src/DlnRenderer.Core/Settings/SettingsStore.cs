using System.Text.Json;
using System.Text.Json.Serialization;

namespace DlnRenderer.Core.Settings;

/// <summary>Outcome of a settings write, so the UI can show a real reason instead of "failed".</summary>
public sealed record SettingsSaveResult(bool Success, string? Error)
{
    public static SettingsSaveResult Ok() => new(true, null);

    public static SettingsSaveResult Fail(string error) => new(false, error);
}

/// <summary>
/// Reads and writes <see cref="RendererSettings"/> as a single JSON file.
/// </summary>
/// <remarks>
/// The file is shared with the service, so it is treated as untrusted input: a corrupt, truncated
/// or hand-edited file falls back to defaults instead of failing startup. Writes go to a temporary
/// file first and are then moved into place, so a crash mid-write cannot leave a half-written
/// settings file that the next start would reject.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Enums are written by name: settings.json is a user-visible file, and a bare number for the
        // display mode would tell them nothing. An unknown name is a parse failure, which Load turns
        // into defaults - the same treatment every other invalid value already gets.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    /// <param name="path">Override for tests; production uses <see cref="DefaultPath"/>.</param>
    public SettingsStore(string? path = null)
        => Path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

    /// <summary><c>%LOCALAPPDATA%\DlnRenderer\settings.json</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DlnRenderer",
        "settings.json");

    public string Path { get; }

    /// <summary>
    /// Loads the settings, falling back to defaults for a missing, unreadable, malformed or invalid
    /// file. Never throws for file-system or JSON reasons.
    /// </summary>
    public RendererSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return RendererSettings.Default;
                }

                var json = File.ReadAllText(Path);
                var settings = JsonSerializer.Deserialize<RendererSettings>(json, JsonOptions);
                if (settings is null)
                {
                    return RendererSettings.Default;
                }

                settings = settings.Normalize();
                return RendererSettings.TryValidate(settings, out _) ? settings : RendererSettings.Default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // A broken settings file must not stop the renderer from starting.
                return RendererSettings.Default;
            }
        }
    }

    /// <summary>
    /// Validates and writes the settings. An invalid value is rejected before anything is written.
    /// </summary>
    public SettingsSaveResult Save(RendererSettings settings)
    {
        var normalized = settings.Normalize();
        if (!RendererSettings.TryValidate(normalized, out var validationError))
        {
            return SettingsSaveResult.Fail(validationError ?? "设置无效。");
        }

        lock (_gate)
        {
            var temporary = Path + ".tmp";
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
                File.Move(temporary, Path, overwrite: true);
                return SettingsSaveResult.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return SettingsSaveResult.Fail($"无法保存设置：{ex.Message}");
            }
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
            // The temporary file is harmless if it cannot be removed now.
        }
    }
}
