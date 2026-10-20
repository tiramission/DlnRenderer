using System.Text.Json;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// Persists the device UUID. Control points identify a renderer by its UDN, so this value must
/// survive restarts; a new UUID on every start would make the device disappear from saved lists.
/// </summary>
public static class DeviceIdentity
{
    private sealed record State(Guid Uuid, string? FriendlyName);

    public static Guid LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
                if (state is not null && state.Uuid != Guid.Empty)
                {
                    return state.Uuid;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop startup; a fresh UUID is safer than a crash.
        }

        var uuid = Guid.NewGuid();
        Save(path, new State(uuid, null));
        return uuid;
    }

    public static void Save(string path, Guid uuid, string? friendlyName)
        => Save(path, new State(uuid, friendlyName));

    private static void Save(string path, State state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Identity persistence is best effort; the in-memory UUID is still valid for this run.
        }
    }
}
